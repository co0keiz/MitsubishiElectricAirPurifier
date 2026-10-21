using System.Security.Cryptography;
using System.Text;
using System.Xml;
using AirPurifier.Core;

namespace AirPurifier.Tests;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData(Fixtures.Normal, AutoMode.Normal)]
    [InlineData(Fixtures.Night, AutoMode.Night)]
    [InlineData(Fixtures.Turbo, AutoMode.Turbo)]
    public void ParsesOwnerLabeledCaptures(string hex, AutoMode mode)
    {
        var snapshot = PurifierProtocol.ParseSnapshot(Fixtures.Snapshot(hex), Fixtures.Options(), DateTimeOffset.UnixEpoch);
        Assert.True(snapshot.State.IsOn);
        Assert.Equal(mode, snapshot.State.AutoMode);
        Assert.Equal(DateTimeOffset.UnixEpoch, snapshot.State.ObservedAtUtc);
        Assert.Equal(hex, snapshot.State.GeneralFrameHex);
        Assert.Equal(0, snapshot.Frame.Skip(1).Sum(value => value) & 0xff);
    }

    [Theory]
    [InlineData(true, Fixtures.Off, "fc41013510010100314245430031300000000000001b")]
    [InlineData(false, Fixtures.Normal, "fc41013510010100304245430031300000000000001c")]
    public void BuildsExactCapturedPowerCommands(bool on, string status, string expected)
    {
        var frame = PurifierProtocol.BuildCommand(Convert.FromHexString(status), on: on);
        Assert.Equal(expected, Convert.ToHexStringLower(frame));
    }

    [Theory]
    [InlineData(AutoMode.Normal, "fc41013510010200314245430031300000000000001a")]
    [InlineData(AutoMode.Night, "fc41013510010200314145430031300000000000001b")]
    [InlineData(AutoMode.Turbo, "fc410135100102003143454300313000000000000019")]
    public void BuildsExactCapturedModeCommands(AutoMode mode, string expected)
    {
        var frame = PurifierProtocol.BuildCommand(Convert.FromHexString(Fixtures.Normal), mode: mode);
        Assert.Equal(expected, Convert.ToHexStringLower(frame));
    }

    [Theory]
    [InlineData(Fixtures.ManualSilent, ManualSpeed.Silent, "fc41013510010200313045430031300000000000002c")]
    [InlineData(Fixtures.ManualLow, ManualSpeed.Low, "fc41013510010200313145430031300000000000002b")]
    [InlineData(Fixtures.ManualMid, ManualSpeed.Mid, "fc41013510010200313245430031300000000000002a")]
    [InlineData(Fixtures.ManualHigh, ManualSpeed.High, "fc410135100102003133454300313000000000000029")]
    [InlineData(Fixtures.ManualTurbo, ManualSpeed.Turbo, "fc410135100102003134454300313000000000000028")]
    public void ParsesAndBuildsManualSpeedFrames(string status, ManualSpeed speed, string expectedCommand)
    {
        var snapshot = PurifierProtocol.ParseSnapshot(Fixtures.Snapshot(status), Fixtures.Options(), DateTimeOffset.UtcNow);
        Assert.Equal(PurifierOperatingMode.Manual, snapshot.State.OperatingMode);
        Assert.Equal(speed, snapshot.State.ManualSpeed);
        Assert.Null(snapshot.State.AutoMode);
        var command = PurifierProtocol.BuildCommand(Convert.FromHexString(Fixtures.Normal), manualSpeed: speed);
        Assert.Equal(expectedCommand, Convert.ToHexStringLower(command));
        Assert.Equal(0, command.Skip(1).Sum(value => value) & 0xff);
    }

    [Fact]
    public void ManualCommandsPreserveUnrelatedSettingsAndRejectCombinedWrites()
    {
        var source = Fixtures.WithByte(Fixtures.Night, 11, 0x42);
        var original = source.ToArray();
        var command = PurifierProtocol.BuildCommand(source, manualSpeed: ManualSpeed.Low);
        foreach (var index in new[] { 0, 2, 3, 4, 8 }.Concat(Enumerable.Range(10, 11)))
            Assert.Equal(source[index], command[index]);
        Assert.Equal(original, source);
        Assert.Throws<ArgumentException>(() => PurifierProtocol.BuildCommand(source, on: true, manualSpeed: ManualSpeed.Low));
        Assert.Throws<ArgumentException>(() => PurifierProtocol.BuildCommand(source, mode: AutoMode.Normal, manualSpeed: ManualSpeed.Low));
        Assert.Throws<ArgumentOutOfRangeException>(() => PurifierProtocol.EncodeManualSpeed((ManualSpeed)999));
        Assert.Null(PurifierProtocol.DecodeManualSpeed(0x30, "81.00"));
    }

    [Fact]
    public void WritesPreserveFreshUnrelatedFieldsAndDoNotMutateSource()
    {
        var source = Fixtures.WithByte(Fixtures.Off, 11, 0x44);
        var original = source.ToArray();
        var mode = PurifierProtocol.BuildCommand(source, mode: AutoMode.Night);
        foreach (var index in new[] { 0, 2, 3, 4, 8 }.Concat(Enumerable.Range(10, 11)))
            Assert.Equal(source[index], mode[index]);
        var power = PurifierProtocol.BuildCommand(source, on: true);
        Assert.Equal(source[9..21], power[9..21]);
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(3, 0x30)]
    [InlineData(8, 0x32)]
    public void RejectsUnsupportedFamilyAndPowerEncoding(int index, byte value) =>
        Assert.Throws<PurifierException>(() => PurifierProtocol.ValidateFrame(Fixtures.WithByte(Fixtures.Normal, index, value)));

    [Fact]
    public void RejectsWrongLengthAndChecksum()
    {
        Assert.Throws<PurifierException>(() => PurifierProtocol.ValidateFrame([0xfc, 0x62]));
        var frame = Convert.FromHexString(Fixtures.Normal);
        frame[^1] ^= 1;
        Assert.Throws<PurifierException>(() => PurifierProtocol.ValidateFrame(frame));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234567890123456")]
    [InlineData("<CSV></CSV>")]
    [InlineData("<CSV><TEST>µ 日本語</TEST></CSV>")]
    public void EncryptionRoundTripsWithRandomIv(string text)
    {
        var first = PurifierProtocol.Encrypt(text);
        Assert.Equal(text, PurifierProtocol.Decrypt(first));
        Assert.NotEqual(first, PurifierProtocol.Encrypt(text));
    }

    [Fact]
    public void EncryptionMatchesIndependentAesAndIsoPaddingFixture()
    {
        var envelope = PurifierProtocol.ParseXml(PurifierProtocol.Encrypt("<CSV></CSV>"));
        var encoded = Convert.FromBase64String(envelope.Root!.Value);
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes("unregistered\0\0\0\0");
        var plain = aes.DecryptCbc(encoded.AsSpan(16), encoded.AsSpan(0, 16), PaddingMode.None);
        Assert.Equal(Convert.FromHexString("3c4353563e3c2f4353563e8000000000"), plain);
    }

    [Theory]
    [InlineData("<LSV/>")]
    [InlineData("<ESV>AA==</ESV>")]
    [InlineData("<ESV><child/></ESV>")]
    public void RejectsMalformedEnvelopes(string envelope) =>
        Assert.Throws<PurifierException>(() => PurifierProtocol.Decrypt(envelope));

    [Fact]
    public void RejectsInvalidPadding()
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes("unregistered\0\0\0\0");
        var ciphertext = aes.EncryptCbc(new byte[16], aes.IV, PaddingMode.None);
        var envelope = "<ESV>" + Convert.ToBase64String([.. aes.IV, .. ciphertext]) + "</ESV>";
        Assert.Throws<PurifierException>(() => PurifierProtocol.Decrypt(envelope));
    }

    [Fact]
    public void RejectsDtdAndOversizedXml()
    {
        Assert.Throws<XmlException>(() => PurifierProtocol.ParseXml("<!DOCTYPE ESV [<!ENTITY test 'text'>]><ESV>&test;</ESV>"));
        Assert.Throws<XmlException>(() => PurifierProtocol.ParseXml("<ESV>" + new string('x', 65536) + "</ESV>"));
    }

    [Fact]
    public void IgnoresProfileFramesAndKeepsUnknownModeUnknown()
    {
        var xml = $"<LSV><MAC>{Fixtures.Mac}</MAC><PROFILECODE><VALUE>{Fixtures.Normal}</VALUE></PROFILECODE></LSV>";
        Assert.Throws<PurifierException>(() => PurifierProtocol.ParseSnapshot(xml, Fixtures.Options(), DateTimeOffset.UtcNow));
        var unknown = Convert.ToHexStringLower(Fixtures.WithByte(Fixtures.Normal, 9, 0x44));
        var snapshot = PurifierProtocol.ParseSnapshot(Fixtures.Snapshot(unknown), Fixtures.Options(), DateTimeOffset.UtcNow);
        Assert.Null(snapshot.State.AutoMode);
        Assert.Equal("0x44", snapshot.State.AutoModeRaw);
    }

    [Theory]
    [InlineData(Fixtures.Normal, false)]
    [InlineData(Fixtures.AutoCleaningActive, true)]
    public void ReportsAutoCleaningSeparatelyFromTheSelectedMode(string frame, bool active)
    {
        var snapshot = PurifierProtocol.ParseSnapshot(Fixtures.Snapshot(frame), Fixtures.Options(), DateTimeOffset.UtcNow);
        Assert.Equal(active, snapshot.State.IsAutoCleaning);
        Assert.Equal(AutoMode.Normal, snapshot.State.AutoMode);
        Assert.True(snapshot.State.IsOn);
    }

    [Fact]
    public void UnknownCleaningByteOrFirmwareDoesNotReportAnAssumedIdleState()
    {
        var frame = Convert.ToHexStringLower(Fixtures.WithByte(Fixtures.Normal, 14, 0x32));
        var unknown = PurifierProtocol.ParseSnapshot(Fixtures.Snapshot(frame), Fixtures.Options(), DateTimeOffset.UtcNow);
        Assert.Null(unknown.State.IsAutoCleaning);
        var firmware = PurifierProtocol.ParseSnapshot(Fixtures.Snapshot(firmware: "81.00"), Fixtures.Options(), DateTimeOffset.UtcNow);
        Assert.Null(firmware.State.IsAutoCleaning);
    }
}
