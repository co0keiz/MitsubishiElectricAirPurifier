# Mitsubishi Electric Air Purifier local control

Local control for the Mitsubishi Electric **MA-EW85S-E**, firmware **80.00**, through a Home Assistant custom integration, an ASP.NET Core 10 API, reusable Python and C# clients, and a standalone PowerShell reference client. Each implementation talks directly to the purifier on the LAN.

Supported controls are power, **Normal**, **Night**, and **Turbo** auto modes, **Silent**, **Low**, **Mid**, **High**, and **Turbo** manual speeds, **Smart Search ON/OFF**, and pre-filter **auto cleaning**. Home Assistant and the API each work independently of PowerShell and its learned mode profile.

Owner-labeled captures confirm all manual speeds on firmware `80.00`: **Silent=`0x30`, Low=`0x31`, Mid=`0x32`, High=`0x33`, Turbo=`0x34`**. Manual Turbo (`0x34`) is separate from Auto Turbo (`0x43`).

## Documentation

| Start here | Guide |
| --- | --- |
| Add the purifier to Home Assistant | [Integration installation and usage](custom_components/ma_ew85s_local/README.md) |
| Run the HTTP API | [API setup and configuration](src/AirPurifier.Api/README.md) |
| Use the C# client in another application | [AirPurifier.Core](src/AirPurifier.Core/README.md) |
| Use the Python client directly | [Async Python protocol client](custom_components/ma_ew85s_local/protocol/README.md) |
| Set up development and run offline tests | [Development guide](docs/development.md) |
| Understand the components and command lifecycle | [Architecture](docs/architecture.md) |
| Diagnose setup or command failures | [Troubleshooting](docs/troubleshooting.md) |
| Find all guides and protocol references | [Documentation index](docs/README.md) |

The [protocol specification](MA-EW85S-local-control.md) is the source of truth for packet layouts, captured fixtures, and hardware evidence. The C# API and PowerShell client have dated hardware verification; Python and Home Assistant have offline simulator verification. Other models and firmware have no verified write support.

## Home Assistant integration

The independent `MA-EW85S-E Local` custom integration controls the purifier directly on the LAN. It requires **Home Assistant 2026.10 or later** and supports only the verified **MA-EW85S-E firmware 80.00**. No account, cloud service, PowerShell process, or ASP.NET API is needed.

Supported entities:

- Fan: power, Auto Normal/Night/Turbo presets, and five manual speeds at 20/40/60/80/100%.
- Smart Search switch with explicit ON/OFF.
- Start auto cleaning button and a separate cleaning activity binary sensor.
- Wi-Fi signal sensor in dBm, diagnostic and disabled by default.

To install through HACS:

1. Use this repository URL: `https://github.com/co0keiz/MitsubishiElectricAirPurifier`.
2. In HACS, open **Custom repositories**, add that URL, and select **Integration**.
3. Download **MA-EW85S-E Local** and restart Home Assistant.
4. Open **Settings → Devices & services → Add integration**, select **MA-EW85S-E Local**, and enter the purifier's host name or IP address.

See the [HACS custom integration requirements](https://www.hacs.dev/docs/publish/integration/) for publishing metadata.

Alternatively, copy `custom_components/ma_ew85s_local` into your Home Assistant configuration's `custom_components` directory and restart. Setup reads status and learns the MAC address as the device identity. Use the integration's **Reconfigure** action after an IP address change; an address belonging to a different MAC is rejected.

The integration polls every 30 seconds. Each write reads fresh state, sends one command, and confirms it through up to three separate readbacks five seconds apart. Writes are never automatically resent. An uncertain result raises an error and refreshes status; a network failure makes the entities unavailable. Mode, speed, Smart Search, and cleaning actions require power ON. Calling the fan's turn-on action with a preset or percentage powers on first and then selects it. Cleaning confirms the cycle's start; the purifier ends the cycle automatically.

Cloud, ECHONET, Wi-Fi configuration, reset, raw commands, discovery, louvre position, air quality, and filter life are outside this integration's scope. Diagnostics redact the host, MAC, and the state-bearing portion of the frame.

For offline Python protocol tests on Windows or Linux, install Python 3.13+ and run:

```text
python -m venv .venv
```

Activate it with `.venv\Scripts\Activate.ps1` in PowerShell or `source .venv/bin/activate` on Linux, then run:

```text
python -m pip install ".[test]"
python -m pytest -q
```

Home Assistant tests require **Linux and Python 3.14** for the pinned Home Assistant 2026.10.2 test environment:

```text
python -m pip install ".[test,ha-test]"
python -m pytest -q
```

Without Home Assistant installed, the three HA test modules are skipped. The full suite uses `pytest-homeassistant-custom-component` and an encrypted loopback-only simulator; it never sends commands to an appliance. CI runs the full suite on Linux. Python/HA support has been verified offline against the captured fixtures, with no live Python/HA hardware test.

## Run

Install the .NET 10 SDK, then run from this repository:

```powershell
dotnet run --project src/AirPurifier.Api
```

The API listens at **http://localhost:5080**. Its default device is `192.168.1.53`, with expected API MAC `70:61:be:5b:6d:1e`. Launching the application does not contact or change the purifier.

The solution contains the API, the reusable `AirPurifier.Core` protocol client, and offline tests. In Rider, run the `AirPurifier.Api` project's `http` profile. [AirPurifier.Api.http](src/AirPurifier.Api/AirPurifier.Api.http) contains ready-to-run requests.

Open **http://localhost:5080/scalar/v1** for the interactive Scalar API reference. It loads the generated OpenAPI document at **http://localhost:5080/openapi/v1.json** and lets you send status, power, auto-mode, manual-speed, Smart Search, and auto-cleaning requests from the browser. Both documentation endpoints are available in all environments; opening them does not contact the purifier. `/health` checks the API process without contacting the appliance.

## Endpoints

| Method | Path | JSON body | Result |
| --- | --- | --- | --- |
| GET | `/api/purifier/status` | — | Fresh device status |
| PUT | `/api/purifier/power` | `{"on":true}` or `{"on":false}` | Set power and verify it |
| PUT | `/api/purifier/auto-mode` | `{"mode":"normal"}`, `{"mode":"night"}`, or `{"mode":"turbo"}` | Set auto mode and verify it |
| PUT | `/api/purifier/manual-speed` | `{"speed":"silent"}` | Set silent, low, mid, high, or turbo manual speed and verify it |
| POST | `/api/purifier/auto-cleaning` | — | Start pre-filter cleaning and verify that it is active |
| PUT | `/api/purifier/smart-search` | `{"enabled":true}` or `{"enabled":false}` | Set Smart Search ON/OFF and verify it |

Examples in PowerShell:

```powershell
$api = 'http://localhost:5080/api/purifier'
Invoke-RestMethod "$api/status"
Invoke-RestMethod "$api/power" -Method Put -ContentType 'application/json' -Body '{"on":true}'
Invoke-RestMethod "$api/auto-mode" -Method Put -ContentType 'application/json' -Body '{"mode":"night"}'
Invoke-RestMethod "$api/auto-mode" -Method Put -ContentType 'application/json' -Body '{"mode":"normal"}'
Invoke-RestMethod "$api/auto-mode" -Method Put -ContentType 'application/json' -Body '{"mode":"turbo"}'
Invoke-RestMethod "$api/manual-speed" -Method Put -ContentType 'application/json' -Body '{"speed":"silent"}'
Invoke-RestMethod "$api/auto-cleaning" -Method Post
Invoke-RestMethod "$api/smart-search" -Method Put -ContentType 'application/json' -Body '{"enabled":true}'
Invoke-RestMethod "$api/smart-search" -Method Put -ContentType 'application/json' -Body '{"enabled":false}'
Invoke-RestMethod "$api/power" -Method Put -ContentType 'application/json' -Body '{"on":false}'
```

Every successful operation returns fresh device state, for example:

```json
{
  "deviceHost": "192.168.1.53",
  "mac": "70:61:be:5b:6d:1e",
  "firmware": "80.00",
  "isOn": true,
  "autoMode": "turbo",
  "manualSpeed": null,
  "isAutoCleaning": false,
  "isSmartSearchEnabled": true,
  "autoModeRaw": "0x43",
  "rssiReported": "-58",
  "adapterStatusRaw": "ERROR",
  "cloudConnectionRaw": "ON",
  "echonetRaw": "OFF",
  "observedAtUtc": "2026-10-21T19:04:04+00:00",
  "generalFrameHex": "fc6201351002000031434543003130000000000000f9",
  "confirmed": true,
  "operatingMode": "auto"
}
```

The adapter's raw `ERROR` status is separate from appliance power and command success. Status includes `operatingMode` (`auto` or `manual`), with exactly one of `autoMode` and `manualSpeed` populated for recognized settings. Manual Turbo and Auto Turbo are separate settings. The existing `autoModeRaw` property is retained and contains the raw selector for either kind of operation. Unknown values or unsupported firmware leave the decoded mode/speed fields null. Writes require verified firmware `80.00`.

`isAutoCleaning` reports whether pre-filter cleaning is active (`null` for an unknown encoding or unsupported firmware). The cleaning endpoint requires power ON and leaves power and the selected mode unchanged. If cleaning is already running, it returns current status without sending another command. A successful response confirms the cycle **has started**, not that it has finished; read status to observe its completion. The device ends the cycle automatically. Cleaning is a one-shot action, not a persistent operating-mode selection.

`isSmartSearchEnabled` reports the Smart Search setting (`null` for an unknown encoding or unsupported firmware). Set an explicit `enabled` boolean to turn it ON or OFF. Both directions require power ON and verified firmware `80.00`, preserve the selected mode, and use separate readback. A setting that already matches returns current status without writing; there is no toggle command.

## PowerShell reference client

The standalone PowerShell 7 script also supports the manual speeds:

```powershell
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SetManualSpeed -ManualSpeed Silent
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SetManualSpeed -ManualSpeed Low
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SetManualSpeed -ManualSpeed Mid
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SetManualSpeed -ManualSpeed High
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SetManualSpeed -ManualSpeed Turbo
```

All five manual speed labels are confirmed. Manual commands require firmware `80.00` and power ON; they do not require the auto-mode profile. They preserve power and other settings, skip writes for an already selected speed, and confirm the selector using up to three separate readbacks. Status includes `OperatingMode`, `ManualSpeed`, and `AutoMode`; inactive or unknown decoded values are null. Existing auto-mode commands continue to use their learned profile.

Start pre-filter auto cleaning with:

```powershell
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action StartAutoCleaning
```

This requires power ON, preserves the selected mode, and confirms the start using a separate readback. It does not restart an already active cycle. `Status` and command results include `IsAutoCleaning`.

Switch Smart Search with:

```powershell
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SetSmartSearch -SmartSearch On
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SetSmartSearch -SmartSearch Off
```

Both commands require power ON and firmware `80.00`, do not need an auto-mode profile, and verify the explicit setting by readback. `Status` and write results include `IsSmartSearchEnabled`.

Run `pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SelfTest` for offline power, auto-mode, and manual-speed checks. On this machine, the portable runtime used during development is `$env:TEMP\purifier-pwsh-7.6.5\pwsh.exe` if `pwsh` is not on PATH.

## Command behavior

Auto-mode/manual-speed selection, Smart Search changes, and starting auto cleaning require the purifier to be ON. Set power first if needed. Selecting a manual speed switches from auto to manual operation; selecting an auto mode switches back. Commands preserve other settings, and a setting that already matches is returned without a write. Device operations are serialized within this API process, including readback. Run one API instance per purifier and avoid simultaneously sending commands through the PowerShell client.

After a write, the API waits five seconds and makes a separate status request. It performs up to three checks and returns success only after confirmation. The immediate write response is not treated as proof of completion. Write commands are never automatically resent. With default timeouts, allow up to about **40 seconds**, plus any time waiting for another device operation, for a command response.

Failures use `application/problem+json`:

| HTTP status | Meaning |
| --- | --- |
| 400 | Missing/invalid JSON, boolean, or mode name |
| 401 | Missing or incorrect API key when one is configured |
| 409 | Mode/speed/Smart Search change or cleaning start requested while the purifier is OFF |
| 502 | Invalid response, identity mismatch, or unsupported firmware for a write |
| 503 | Device connection/HTTP failure |
| 504 | Device request timeout or setting not confirmed within the readback attempts |

Device failures include `code`, `confirmed: false`, and `commandMayHaveSucceeded`. When the latter is `true`, read fresh status before deciding whether to retry: the command might already have been applied. A disconnected caller can also leave a command outcome unknown; the next request always reads fresh status.

## Configuration

Edit [appsettings.json](src/AirPurifier.Api/appsettings.json), or use standard ASP.NET Core environment variables:

```powershell
$env:Purifier__Host = '192.168.1.53'
$env:Purifier__ExpectedMac = '70:61:be:5b:6d:1e'
dotnet run --project src/AirPurifier.Api
```

`Purifier:RequestTimeout` and `Purifier:ReadbackDelay` are time spans; `Purifier:ReadbackAttempts` is an integer. Configuration is validated at startup. The verified Normal/Night/Turbo values are built into the protocol client for firmware `80.00`.

For access from another LAN device, select a network binding and an API key:

```powershell
$env:Api__ApiKey = 'replace-with-your-own-key'
dotnet run --project src/AirPurifier.Api --no-launch-profile -- --urls http://0.0.0.0:5080
```

Send the key as `X-Api-Key` on `/api/*` requests. When a key is configured, the OpenAPI document describes that requirement and Scalar offers an API-key authentication field; enter your key there before sending requests. The configured key value is never embedded in the documentation. By default, the API binds only to loopback, with no key required. The purifier transport bypasses system proxies, follows no redirects, and leaves cloud, ECHONET, Wi-Fi, and reset settings untouched.

## Test

```powershell
dotnet test MitsubishiElectricAirPurifier.slnx
```

Tests use a simulated encrypted purifier and an in-process ASP.NET Core host. They make no requests to the real appliance. They cover captured packets, checksum and field preservation, encryption/padding and XML rejection, power/mode/manual-speed/Smart Search/cleaning operations, delayed and failed readback, identity and firmware checks, cancellation while queued, serialization, HTTP validation, API keys, error responses, Scalar's bundled scripts, and OpenAPI authentication metadata.

On 21 October 2026, all **109 tests passed**, including Smart Search, cleaning, and Scalar/OpenAPI checks. PowerShell's offline checks also passed. Live tests verified all five manual speeds through both PowerShell and the native API, preserving power and unrelated settings. Auto-mode control, a power no-op, and cleaning start were verified through the API. Smart Search ON/OFF was then verified through both clients, leaving **Smart Search OFF, power ON, Auto Normal**, matching the owner's selected settings. Temporary test servers were stopped after validation.

See [MA-EW85S-local-control.md](MA-EW85S-local-control.md) for the protocol specification: the [verification matrix](MA-EW85S-local-control.md#verification-matrix), the [frame byte map](MA-EW85S-local-control.md#5-general-state-frame), the [write procedure](MA-EW85S-local-control.md#6-writing-settings), the [captured fixtures](MA-EW85S-local-control.md#8-fixtures-appendix), the [dated hardware evidence](MA-EW85S-local-control.md#11-evidence-log-and-references), and the [Home Assistant integration](MA-EW85S-local-control.md#93-home-assistant-integration). The existing PowerShell reference remains available independently.
