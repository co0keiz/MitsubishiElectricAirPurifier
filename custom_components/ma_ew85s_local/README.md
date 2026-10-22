# MA-EW85S-E Local for Home Assistant

A custom integration for the Mitsubishi Electric **MA-EW85S-E**, firmware **80.00**. It communicates with the purifier directly on your LAN using its local encrypted protocol. No Mitsubishi account, cloud API, ASP.NET API, or PowerShell process is required.

Requires **Home Assistant 2026.10 or later**. The integration is tested offline against captured frames and a simulated encrypted purifier. Live Python/HA hardware verification has not been performed.

## Install with HACS

1. Open HACS and its **Custom repositories** menu.
2. Add `https://github.com/co0keiz/MitsubishiElectricAirPurifier` with category **Integration**.
3. Download **MA-EW85S-E Local**.
4. Restart Home Assistant.
5. Open **Settings → Devices & services → Add integration**, search for **MA-EW85S-E Local**, and enter the purifier's host name or IP address.

Setup reads status, requires firmware `80.00`, and stores the MAC as the stable device identity. It does not send a setting command. Another entry for the same MAC is rejected.

## Manual installation

Copy this entire `ma_ew85s_local` directory into your Home Assistant configuration directory:

```text
<configuration directory>/
  custom_components/
    ma_ew85s_local/
      __init__.py
      manifest.json
      protocol/
      ...
```

Restart Home Assistant, then add the integration through Devices & services as above. Configuration is managed through the UI; there is no YAML setup platform.

## Entities

| Entity | Purpose | Notes |
| --- | --- | --- |
| Fan, named for the device | Power, manual speed, and auto presets | Five manual speeds; three auto presets |
| Smart Search switch | Explicit ON/OFF | An unrecognized encoding is unknown |
| Start auto cleaning button | Start a pre-filter cleaning cycle | Sends no new start if already active |
| Auto cleaning binary sensor | Report whether cleaning is running | Uses the running device class; unknown encodings remain unknown |
| Wi-Fi signal sensor | Report RSSI in dBm | Diagnostic; disabled by default |

Enable Wi-Fi signal from the device's entity settings if you want it. Entity IDs depend on the device name and your registry; select your own entities in actions and automations.

Manual speed percentages map as follows:

| Speed | Percentage |
| --- | --- |
| Silent | 20% |
| Low | 40% |
| Mid | 60% |
| High | 80% |
| Turbo | 100% |

Presets are `auto_normal`, `auto_night`, and `auto_turbo`. In auto mode, `preset_mode` is populated and `percentage` is empty. In manual mode, the reverse applies. Manual Turbo and Auto Turbo are distinct settings. Setting percentage to zero turns the fan off.

## Actions

Mode, speed, Smart Search, and cleaning changes require power ON. Calling `fan.turn_on` with a percentage or preset powers on first, then selects the setting. Replace the example entity ID below with your fan's actual ID.

Select a manual speed and turn on:

```yaml
action: fan.turn_on
target:
  entity_id: fan.your_purifier
data:
  percentage: 40
```

Select an auto preset and turn on:

```yaml
action: fan.turn_on
target:
  entity_id: fan.your_purifier
data:
  preset_mode: auto_night
```

Use `fan.set_percentage` or `fan.set_preset_mode` to change an already running purifier. Use the Smart Search switch's explicit turn-on/turn-off actions and the cleaning button's press action for those features. Cleaning success confirms the start, and the activity sensor reports when the device finishes the cycle.

## Updates, failures, and address changes

Idle polling runs every 30 seconds. A write reads current state, sends one command, and checks up to three separate readbacks five seconds apart. Unchanged requested settings return fresh state without a write. The interval and retry timings are implementation defaults, not measured hardware limits.

Communication failures make entities unavailable. Errors after a write attempt may mean the command was applied; the integration refreshes status and does not automatically resend it. An OFF-device setting request raises a service validation error.

If the purifier's IP changes, use the integration entry's **Reconfigure** action to enter its new address. The new address must return the same MAC. Reconfiguration updates the existing entry and reloads it.

Download diagnostics from the integration entry when investigating an issue. Host and MAC are fully redacted, and the state-bearing portion of the frame is removed. See [Troubleshooting](../../docs/troubleshooting.md) for error-specific guidance.

## Supported scope and development

Only this model and firmware have verified write mappings. Louvre position, air quality, filter life, discovery, raw commands, and cloud/ECHONET/Wi-Fi/reset operations are not exposed. Ordinary control leaves those adapter settings untouched.

The [protocol subpackage](protocol/README.md) has no Home Assistant imports and can be used independently. See [Architecture](../../docs/architecture.md), [Development and testing](../../docs/development.md), and the [protocol specification](../../MA-EW85S-local-control.md) for implementation details and evidence.
