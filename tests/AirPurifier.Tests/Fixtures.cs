using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AirPurifier.Core;

namespace AirPurifier.Tests;

internal static class Fixtures
{
    internal const string Normal = "fc6201351002000031424543003130000000000000fa";
    internal const string Night = "fc6201351002000031414543003130000000000000fb";
    internal const string Turbo = "fc6201351002000031434543003130000000000000f9";
    internal const string Off = "fc6201351002000030424543003130000000000000fb";
    // Owner-labeled manual-speed captures from firmware 80.00.
    internal const string ManualSilent = "fc62013510020000313045430031300000000000000c";
    internal const string ManualLow = "fc62013510020000313145430031300000000000000b";
    internal const string ManualMid = "fc62013510020000313245430031300000000000000a";
    internal const string ManualHigh = "fc620135100200003133454300313000000000000009";
    internal const string ManualTurbo = "fc620135100200003134454300313000000000000008";
    internal const string AutoCleaningActive = "fc6201351002000031424543003131000000000000f9";
    internal const string SmartSearchOff = "fc6201351002000031424543003030000000000000fb";
    internal const string Mac = "70:61:be:5b:6d:1e";

    internal static string Snapshot(string frame = Turbo, string mac = Mac, string firmware = "80.00") =>
        $"<LSV><MAC>{mac}</MAC><APP_VER>{firmware}</APP_VER><RSSI>-58</RSSI><STATUS>ERROR</STATUS>" +
        $"<CONNECT>ON</CONNECT><ECHONET>OFF</ECHONET><CODE><VALUE>{frame}</VALUE></CODE></LSV>";

    internal static PurifierOptions Options() => new() { ReadbackDelay = TimeSpan.Zero };

    internal static byte[] WithByte(string hex, int index, byte value)
    {
        var frame = Convert.FromHexString(hex);
        frame[index] = value;
        frame[^1] = PurifierProtocol.Checksum(frame);
        return frame;
    }
}

internal sealed class ScriptedDevice(params string[] frames) : HttpMessageHandler
{
    private int _count;
    internal ConcurrentQueue<string> Requests { get; } = new();
    internal Func<string, int, CancellationToken, Task<string>>? Respond { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal("http://192.168.1.53/smart", request.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.True(request.Headers.ConnectionClose);
        var payload = PurifierProtocol.Decrypt(await request.Content!.ReadAsStringAsync(cancellationToken));
        Requests.Enqueue(payload);
        var index = Interlocked.Increment(ref _count) - 1;
        var xml = Respond is null
            ? Fixtures.Snapshot(frames.Length == 0 ? Fixtures.Turbo : frames[Math.Min(index, frames.Length - 1)])
            : await Respond(payload, index, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PurifierProtocol.Encrypt(xml), Encoding.UTF8, "text/plain")
        };
    }
}
