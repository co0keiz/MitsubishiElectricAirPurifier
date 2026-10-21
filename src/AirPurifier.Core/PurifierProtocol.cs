using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AirPurifier.Core;

internal static class PurifierProtocol
{
    internal const string VerifiedFirmware = "80.00";
    internal const int MaxXmlLength = 65536;
    private static readonly byte[] Key = Encoding.ASCII.GetBytes("unregistered\0\0\0\0");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Encrypt(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var padded = new byte[bytes.Length + 16 - bytes.Length % 16];
        bytes.CopyTo(padded, 0);
        padded[bytes.Length] = 0x80;
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.GenerateIV();
        var ciphertext = aes.EncryptCbc(padded, aes.IV, PaddingMode.None);
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><ESV>" +
               Convert.ToBase64String([.. aes.IV, .. ciphertext]) + "</ESV>";
    }

    internal static string Decrypt(string envelope)
    {
        var root = ParseXml(envelope).Root;
        if (root?.Name != "ESV" || root.HasElements)
            throw ProtocolError("Expected an ESV response envelope.");
        var bytes = Convert.FromBase64String(root.Value.Trim());
        if (bytes.Length < 32 || (bytes.Length - 16) % 16 != 0)
            throw ProtocolError("Invalid encrypted response length.");
        using var aes = Aes.Create();
        aes.Key = Key;
        var plaintext = aes.DecryptCbc(bytes.AsSpan(16), bytes.AsSpan(0, 16), PaddingMode.None);
        var end = plaintext.Length - 1;
        while (end >= 0 && plaintext[end] == 0) end--;
        if (end < 0 || plaintext[end] != 0x80 || plaintext.Length - end > 16)
            throw ProtocolError("Invalid ISO/IEC 7816-4 response padding.");
        return StrictUtf8.GetString(plaintext, 0, end);
    }

    internal static XDocument ParseXml(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlLength
        });
        return XDocument.Load(reader);
    }

    internal static byte Checksum(ReadOnlySpan<byte> frame)
    {
        var sum = 0;
        for (var i = 1; i < frame.Length - 1; i++) sum += frame[i];
        return unchecked((byte)-sum);
    }

    internal static void ValidateFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != 22 || !frame[..6].SequenceEqual<byte>([0xfc, 0x62, 0x01, 0x35, 0x10, 0x02]))
            throw ProtocolError("Unsupported purifier general-state frame.");
        if (Checksum(frame) != frame[^1]) throw ProtocolError("Invalid frame checksum.");
        if (frame[8] is not (0x30 or 0x31)) throw ProtocolError("Unknown purifier power encoding.");
    }

    internal static byte[] BuildCommand(ReadOnlySpan<byte> status, bool? on = null, AutoMode? mode = null,
        ManualSpeed? manualSpeed = null, bool startAutoCleaning = false, bool? smartSearchEnabled = null)
    {
        ValidateFrame(status);
        if ((on.HasValue ? 1 : 0) + (mode.HasValue ? 1 : 0) + (manualSpeed.HasValue ? 1 : 0) +
            (startAutoCleaning ? 1 : 0) + (smartSearchEnabled.HasValue ? 1 : 0) != 1)
            throw new ArgumentException("Select exactly one setting per command.");
        var frame = status.ToArray();
        frame[1] = 0x41;
        frame[5] = 0x01;
        frame[6] = on.HasValue ? (byte)0x01 : smartSearchEnabled.HasValue ? (byte)0x20 :
            startAutoCleaning ? (byte)0x40 : (byte)0x02;
        frame[7] = 0x00;
        if (on.HasValue) frame[8] = on.Value ? (byte)0x31 : (byte)0x30;
        if (mode.HasValue) frame[9] = EncodeMode(mode.Value);
        if (manualSpeed.HasValue) frame[9] = EncodeManualSpeed(manualSpeed.Value);
        if (startAutoCleaning) frame[14] = 0x31;
        if (smartSearchEnabled.HasValue) frame[13] = smartSearchEnabled.Value ? (byte)0x31 : (byte)0x30;
        frame[^1] = Checksum(frame);
        return frame;
    }

    internal static byte EncodeMode(AutoMode mode) => mode switch
    {
        AutoMode.Normal => 0x42,
        AutoMode.Night => 0x41,
        AutoMode.Turbo => 0x43,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    internal static AutoMode? DecodeMode(byte value, string firmware) => firmware != VerifiedFirmware ? null : value switch
    {
        0x42 => AutoMode.Normal,
        0x41 => AutoMode.Night,
        0x43 => AutoMode.Turbo,
        _ => null
    };

    internal static byte EncodeManualSpeed(ManualSpeed speed) => speed switch
    {
        ManualSpeed.Silent => 0x30,
        ManualSpeed.Low => 0x31,
        ManualSpeed.Mid => 0x32,
        ManualSpeed.High => 0x33,
        ManualSpeed.Turbo => 0x34,
        _ => throw new ArgumentOutOfRangeException(nameof(speed))
    };

    internal static ManualSpeed? DecodeManualSpeed(byte value, string firmware) => firmware != VerifiedFirmware ? null : value switch
    {
        0x30 => ManualSpeed.Silent,
        0x31 => ManualSpeed.Low,
        0x32 => ManualSpeed.Mid,
        0x33 => ManualSpeed.High,
        0x34 => ManualSpeed.Turbo,
        _ => null
    };

    private static bool? DecodeBooleanSetting(byte value, string firmware) => firmware != VerifiedFirmware ? null : value switch
    {
        0x30 => false,
        0x31 => true,
        _ => null
    };

    internal static PurifierSnapshot ParseSnapshot(string xml, PurifierOptions options, DateTimeOffset observedAt)
    {
        var root = ParseXml(xml).Root;
        if (root?.Name != "LSV") throw ProtocolError("Expected an LSV response.");
        var mac = root.Element("MAC")?.Value.Trim();
        if (!string.Equals(mac, options.ExpectedMac, StringComparison.OrdinalIgnoreCase))
            throw new PurifierException(PurifierError.IdentityMismatch,
                "Device identity mismatch. Check Purifier:Host and Purifier:ExpectedMac.");
        var value = root.Elements("CODE").Elements("VALUE")
            .Select(element => element.Value.Trim())
            .FirstOrDefault(hex => hex.StartsWith("fc6201351002", StringComparison.OrdinalIgnoreCase));
        if (value is null) throw ProtocolError("No supported general-state frame was returned.");
        var frame = Convert.FromHexString(value);
        ValidateFrame(frame);
        var firmware = root.Element("APP_VER")?.Value.Trim() ?? "";
        var state = new PurifierState(options.Host, mac!, firmware, frame[8] == 0x31,
            DecodeMode(frame[9], firmware), DecodeManualSpeed(frame[9], firmware),
            DecodeBooleanSetting(frame[14], firmware), DecodeBooleanSetting(frame[13], firmware),
            $"0x{frame[9]:x2}",
            root.Element("RSSI")?.Value, root.Element("STATUS")?.Value,
            root.Element("CONNECT")?.Value, root.Element("ECHONET")?.Value,
            observedAt, Convert.ToHexStringLower(frame));
        return new PurifierSnapshot(frame, state);
    }

    internal static PurifierException ProtocolError(string message, Exception? innerException = null) =>
        new(PurifierError.ProtocolError, message, innerException);
}

internal sealed record PurifierSnapshot(byte[] Frame, PurifierState State);
