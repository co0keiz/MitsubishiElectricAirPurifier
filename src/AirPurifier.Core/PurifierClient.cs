using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace AirPurifier.Core;

public sealed class PurifierClient : IPurifierClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly PurifierOptions _options;
    private readonly TimeProvider _time;
    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PurifierClient(HttpClient http, PurifierOptions options, TimeProvider? timeProvider = null)
    {
        options.Validate();
        _http = http;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _endpoint = new UriBuilder("http", options.Host, 80, "/smart").Uri;
    }

    public async Task<PurifierState> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadSnapshotAsync(cancellationToken)).State; }
        finally { _gate.Release(); }
    }

    public Task<PurifierState> SetPowerAsync(bool on, CancellationToken cancellationToken = default) =>
        SetAsync(cancellationToken, on: on);

    public Task<PurifierState> SetAutoModeAsync(AutoMode mode, CancellationToken cancellationToken = default)
    {
        _ = PurifierProtocol.EncodeMode(mode);
        return SetAsync(cancellationToken, mode: mode);
    }

    public Task<PurifierState> SetManualSpeedAsync(ManualSpeed speed, CancellationToken cancellationToken = default)
    {
        _ = PurifierProtocol.EncodeManualSpeed(speed);
        return SetAsync(cancellationToken, manualSpeed: speed);
    }

    public Task<PurifierState> StartAutoCleaningAsync(CancellationToken cancellationToken = default) =>
        SetAsync(cancellationToken, startAutoCleaning: true);

    public Task<PurifierState> SetSmartSearchAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SetAsync(cancellationToken, smartSearchEnabled: enabled);

    private async Task<PurifierState> SetAsync(CancellationToken cancellationToken, bool? on = null,
        AutoMode? mode = null, ManualSpeed? manualSpeed = null, bool startAutoCleaning = false,
        bool? smartSearchEnabled = null)
    {
        await _gate.WaitAsync(cancellationToken);
        var writeAttempted = false;
        try
        {
            var before = await ReadSnapshotAsync(cancellationToken);
            if (before.State.Firmware != PurifierProtocol.VerifiedFirmware)
                throw new PurifierException(PurifierError.UnsupportedFirmware,
                    "Writes are verified only for firmware 80.00. The reported firmware is unsupported.");
            if (!on.HasValue && !before.State.IsOn)
                throw new PurifierException(PurifierError.DeviceOff, "Turn the purifier on before changing mode, speed, or Smart Search, or starting auto cleaning.");
            if (startAutoCleaning && before.State.IsAutoCleaning is null)
                throw PurifierProtocol.ProtocolError("The current auto-cleaning state is unknown; no start command was sent.");
            if (smartSearchEnabled.HasValue && before.State.IsSmartSearchEnabled is null)
                throw PurifierProtocol.ProtocolError("The current Smart Search state is unknown; no write was sent.");
            if (on == before.State.IsOn || (mode.HasValue && mode == before.State.AutoMode) ||
                (manualSpeed.HasValue && manualSpeed == before.State.ManualSpeed) ||
                (startAutoCleaning && before.State.IsAutoCleaning == true) ||
                (smartSearchEnabled.HasValue && before.State.IsSmartSearchEnabled == smartSearchEnabled.Value))
                return before.State;

            var command = PurifierProtocol.BuildCommand(before.Frame, on, mode, manualSpeed, startAutoCleaning, smartSearchEnabled);
            writeAttempted = true;
            // Acknowledge the exchange, but never use the immediate reply as confirmation.
            _ = await ExchangeAsync($"<CSV><CODE><VALUE>{Convert.ToHexStringLower(command)}</VALUE></CODE></CSV>", cancellationToken);
            for (var attempt = 0; attempt < _options.ReadbackAttempts; attempt++)
            {
                await Task.Delay(_options.ReadbackDelay, _time, cancellationToken);
                var after = await ReadSnapshotAsync(cancellationToken);
                if (after.State.Firmware != before.State.Firmware)
                    throw PurifierProtocol.ProtocolError("Firmware changed during command readback.");
                if (!on.HasValue && after.State.IsOn != before.State.IsOn)
                    throw PurifierProtocol.ProtocolError("Power changed during command readback.");
                if ((startAutoCleaning || smartSearchEnabled.HasValue) && after.Frame[9] != before.Frame[9])
                    throw PurifierProtocol.ProtocolError("Operating mode changed during feature readback.");
                if ((on.HasValue && after.State.IsOn == on.Value) ||
                    (mode.HasValue && after.State.AutoMode == mode.Value) ||
                    (manualSpeed.HasValue && after.State.ManualSpeed == manualSpeed.Value) ||
                    (startAutoCleaning && after.State.IsAutoCleaning == true) ||
                    (smartSearchEnabled.HasValue && after.State.IsSmartSearchEnabled == smartSearchEnabled.Value))
                    return after.State;
            }
            throw new PurifierException(PurifierError.CommandUnconfirmed,
                "The requested setting was not confirmed by readback.");
        }
        catch (PurifierException exception) when (writeAttempted)
        {
            throw new PurifierException(exception.Code,
                exception.Message + " The command may have succeeded; read status before retrying.",
                exception, commandMayHaveSucceeded: true);
        }
        finally { _gate.Release(); }
    }

    private async Task<PurifierSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) =>
        await ExchangeAsync("<CSV></CSV>", cancellationToken);

    private async Task<PurifierSnapshot> ExchangeAsync(string plaintext, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.ConnectionClose = true;
            request.Headers.UserAgent.ParseAdd("KirigamineRemote/5.1.0");
            request.Content = new StringContent(PurifierProtocol.Encrypt(plaintext), Encoding.UTF8, "text/plain");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            // Bound the body before allocating/decrypting it, including chunked responses.
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int count;
            while ((count = await body.ReadAsync(chunk, timeout.Token)) != 0)
            {
                if (buffer.Length + count > PurifierProtocol.MaxXmlLength)
                    throw PurifierProtocol.ProtocolError("Response exceeded the expected size.");
                buffer.Write(chunk, 0, count);
            }
            var envelope = new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var xml = PurifierProtocol.Decrypt(envelope);
            return PurifierProtocol.ParseSnapshot(xml, _options, _time.GetUtcNow());
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PurifierException(PurifierError.RequestTimedOut, "The purifier request timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new PurifierException(PurifierError.ConnectionFailed, "Could not communicate with the purifier.", exception);
        }
        catch (IOException exception)
        {
            throw new PurifierException(PurifierError.ConnectionFailed, "The purifier response could not be read.", exception);
        }
        catch (Exception exception) when (exception is XmlException or FormatException or CryptographicException or DecoderFallbackException)
        {
            throw PurifierProtocol.ProtocolError("The purifier returned an invalid encrypted response or state frame.", exception);
        }
    }

    public void Dispose() => _gate.Dispose();
}
