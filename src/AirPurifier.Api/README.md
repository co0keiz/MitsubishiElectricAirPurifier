# AirPurifier.Api

An ASP.NET Core 10 HTTP API around [AirPurifier.Core](../AirPurifier.Core/README.md). It directly controls a local MA-EW85S-E on firmware `80.00`; it does not launch PowerShell. Home Assistant has its own Python client and does not need this API.

Run all commands from the repository root.

## Start

Install the .NET 10 SDK and configure the purifier's host and expected MAC. The illustrative values below must be replaced with your device's values.

```powershell
$env:Purifier__Host = '192.0.2.10'
$env:Purifier__ExpectedMac = '00:11:22:33:44:55'
dotnet run --project src/AirPurifier.Api
```

The application listens at `http://localhost:5080`. In Rider, use the project's `http` launch profile. Starting it validates options but does not contact the appliance.

| URL | Purpose | Contacts the purifier? |
| --- | --- | --- |
| `/health` | Process health | No |
| `/scalar/v1` | Interactive API reference | Opening the page: no; executing device requests: yes |
| `/openapi/v1.json` | OpenAPI document | No |
| `/api/purifier/status` | Fresh device state | Yes |

## Configuration

Set values in [appsettings.json](appsettings.json) or through environment variables:

| Setting | Environment variable | Default / accepted values |
| --- | --- | --- |
| `Purifier:Host` | `Purifier__Host` | Original test address; host/IP without scheme, path, or port |
| `Purifier:ExpectedMac` | `Purifier__ExpectedMac` | Original test MAC; colon-separated hexadecimal |
| `Purifier:RequestTimeout` | `Purifier__RequestTimeout` | `00:00:05`; greater than zero and at most 60 seconds |
| `Purifier:ReadbackDelay` | `Purifier__ReadbackDelay` | `00:00:05`; zero through 30 seconds |
| `Purifier:ReadbackAttempts` | `Purifier__ReadbackAttempts` | `3`; 1–10 |
| `Api:ApiKey` | `Api__ApiKey` | Unset; optional key for `/api/*` |

The shipped address and MAC belong to the original test installation. Update both for another purifier. Firmware is reported by the device; it is not a configuration override.

To listen on LAN interfaces with an API key:

```powershell
$env:Api__ApiKey = 'replace-with-your-own-key'
dotnet run --project src/AirPurifier.Api --no-launch-profile -- --urls http://0.0.0.0:5080
```

Send the key in the `X-Api-Key` request header. Scalar exposes the same header through its authentication control when a key is configured. The key is required on `/api/*`; health, Scalar, and OpenAPI remain accessible. Default binding is loopback with no key. This application does not configure HTTPS itself.

## Endpoints

The base path is `/api/purifier`. Setting requests use `Content-Type: application/json`, except the cleaning action, which needs no body.

| Method and path | JSON body | Result |
| --- | --- | --- |
| `GET /status` | None | Fresh state |
| `PUT /power` | `{"on":true}` or `{"on":false}` | Confirmed power state |
| `PUT /auto-mode` | `{"mode":"normal"}` | Normal, night, or turbo auto mode |
| `PUT /manual-speed` | `{"speed":"low"}` | Silent, low, mid, high, or turbo manual speed |
| `PUT /smart-search` | `{"enabled":true}` or `{"enabled":false}` | Explicit Smart Search setting |
| `POST /auto-cleaning` | None | Cleaning start confirmed, or already active |

Use [AirPurifier.Api.http](AirPurifier.Api.http) in Rider for prepared requests, or the [root README](../../README.md#endpoints) for PowerShell examples and a complete status response. Running a device request contacts the configured appliance.

Settings other than power require the purifier to be ON. Every write first reads current state, sends at most one command, and confirms it through separate status reads. An already matching setting is a no-op. Allow about 40 seconds with default timeouts, plus time waiting behind another operation. Use one API instance per purifier and avoid competing controllers.

## Responses and failures

Success returns `PurifierState`, including firmware, MAC, power, decoded operating mode, nullable Smart Search and cleaning flags, raw adapter fields, receipt timestamp, and frame hex. Unknown decoded values remain null. The adapter's raw status string does not determine power or command success.

Failures use `application/problem+json`:

| HTTP status | Meaning |
| --- | --- |
| 400 | Invalid or missing JSON fields, wrong types, or invalid enum names |
| 401 | Missing or incorrect API key |
| 409 | A setting requires power ON |
| 502 | Invalid device protocol response, identity mismatch, or unsupported write firmware |
| 503 | Device connection or HTTP exchange failed |
| 504 | A device request timed out or readbacks did not confirm the setting |

Device errors include `code`, `confirmed: false`, and `commandMayHaveSucceeded`. When the last field is true, read status before choosing whether to retry. The API does not resend the write automatically. A disconnected caller can also leave the operation's outcome uncertain.

See [Troubleshooting](../../docs/troubleshooting.md) for common cases and [Development and testing](../../docs/development.md) for the offline regression suite.
