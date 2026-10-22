# AirPurifier.Core

A reusable, asynchronous .NET 10 client for local Mitsubishi Electric MA-EW85S-E control. It has no dependency on ASP.NET Core, PowerShell, or a learned mode profile. Writes are supported only for firmware `80.00`.

## Use from another project

Add a project reference to `AirPurifier.Core.csproj`. Construct one client per purifier and reuse it so status reads and writes share its serialization lock.

The following example reads device status when executed. Replace the illustrative address and MAC with your device's identity.

```csharp
using AirPurifier.Core;

using var http = new HttpClient(new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false
})
{
    Timeout = Timeout.InfiniteTimeSpan
};

using var client = new PurifierClient(http, new PurifierOptions
{
    Host = "192.0.2.10",
    ExpectedMac = "00:11:22:33:44:55"
});

var state = await client.GetStatusAsync();
Console.WriteLine($"{state.Firmware}: power={state.IsOn}, mode={state.OperatingMode}");
```

The caller owns `HttpClient` and configures its transport. The API uses the same proxy/redirect settings shown above. `PurifierClient` applies a per-request timeout, and disposing it releases its lock resources without disposing the caller's HTTP client.

## Public API

`PurifierClient` implements `IPurifierClient`. Each operation accepts an optional `CancellationToken` and returns `Task<PurifierState>`.

| Method | Input |
| --- | --- |
| `GetStatusAsync` | No setting |
| `SetPowerAsync` | `bool on` |
| `SetAutoModeAsync` | `AutoMode.Normal`, `.Night`, or `.Turbo` |
| `SetManualSpeedAsync` | `ManualSpeed.Silent`, `.Low`, `.Mid`, `.High`, or `.Turbo` |
| `SetSmartSearchAsync` | `bool enabled` |
| `StartAutoCleaningAsync` | No setting |

Mode, speed, Smart Search, and cleaning require power ON. Smart Search and cleaning also require a recognized current flag. A matching requested state returns a fresh snapshot without writing. Cleaning success confirms the cycle started, rather than waiting for completion.

## Options and state

`PurifierOptions` requires a host name/IP without a scheme, path, or port and a colon-separated expected MAC. Its constructor defaults identify the original test installation, so set both explicitly for another device.

Defaults are a five-second `RequestTimeout`, five-second `ReadbackDelay`, and three `ReadbackAttempts`. Validation allows timeout greater than zero and at most 60 seconds, delay from zero through 30 seconds, and 1–10 attempts. An optional `TimeProvider` constructor parameter supports deterministic readback timing in tests.

`PurifierState` exposes identity, firmware, power, nullable decoded mode/speed/feature flags, raw adapter fields, receipt time, and the validated frame. `OperatingMode` is derived from the decoded selector. Manual Turbo and Auto Turbo are distinct. Unknown values and unsupported firmware leave the relevant decoded fields null. Raw adapter `STATUS`, cloud, and ECHONET values do not determine appliance power.

## Confirmation and errors

Writes perform a fresh identity-checked read, send one command, and confirm through separate delayed status requests. The immediate command reply is not proof of completion. The client's lock covers the whole read/write/readback sequence; it does not coordinate other client instances or processes.

Device failures raise `PurifierException`, whose `Code` is one of `ConnectionFailed`, `RequestTimedOut`, `ProtocolError`, `IdentityMismatch`, `UnsupportedFirmware`, `DeviceOff`, or `CommandUnconfirmed`.

If `CommandMayHaveSucceeded` is true, obtain fresh status before deciding whether to retry. Writes are never automatically resent. Caller cancellation propagates separately; a cancelled operation may also have reached the device if it was already sending a write.

For byte-level behavior, see the [protocol specification](../../MA-EW85S-local-control.md). For test setup, see [Development and testing](../../docs/development.md). The [HTTP API](../AirPurifier.Api/README.md) provides an example consumer of this library.
