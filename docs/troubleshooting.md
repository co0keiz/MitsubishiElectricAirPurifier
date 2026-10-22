# Troubleshooting

Start by identifying which interface failed: Home Assistant, the HTTP API, or a direct client. A healthy API process does not prove the purifier is reachable: `/health` checks the process only, while `/api/purifier/status` performs a device read.

## Setup and connectivity

| Symptom | Check or next step |
| --- | --- |
| Home Assistant reports `cannot_connect` | Check the host/IP, LAN reachability, and that the device returns the supported purifier status format. This message also covers invalid XML, frame, or identity responses during setup. |
| Setup reports `unsupported_firmware` | Only firmware `80.00` is supported by HA setup. Do not bypass the guard for another version. |
| An IP address changed | Use the integration's Reconfigure action. For the API, update `Purifier:Host` and restart it. Preserve the expected MAC for the same device. |
| Reconfigure reports a different purifier | The MAC at the new address does not match the config entry. Verify the address rather than replacing the stored identity to silence the error. |
| A duplicate integration is rejected | The purifier's MAC is already configured, even if it now has a different IP. Reconfigure the existing entry. |
| API startup rejects options | Use a host name or IP without `http://`, a path, or a port; supply a colon-separated MAC. Check timeout/readback bounds in the [API guide](../src/AirPurifier.Api/README.md#configuration). |
| API returns HTTP 401 | Supply the configured key in `X-Api-Key`. If no API key is configured, the API does not require that header. |

## Commands and state

**Device OFF.** Mode, speed, Smart Search, and cleaning actions require power ON. HA reports `ServiceValidationError`, and the API returns HTTP 409. Power on first. HA's fan turn-on action can accept the desired percentage or preset and perform those operations in order.

**A command takes several seconds.** Writes use a separate delayed status read for confirmation. The defaults allow three readbacks five seconds apart, each with a five-second HTTP timeout. A command can take about 40 seconds, plus time queued behind another operation.

**The result says the command may have succeeded.** Read current status before deciding whether to retry. The write may have reached the purifier even if its reply or readback was lost. Clients do not automatically resend it. The API exposes `commandMayHaveSucceeded`; Python exposes `command_may_have_succeeded`. HA refreshes state after ambiguous errors.

**Readback reports changed power, firmware, or mode.** Another controller, a physical panel action, or a device change may have invalidated confirmation. Read fresh state and avoid competing controllers. Do not treat the immediate command response as proof of success.

**An entity is unavailable.** A coordinator refresh or communication attempt failed. The integration preserves cached state but reports unavailable until a successful update; it does not assume the purifier turned OFF.

**A flag is unknown.** An unrecognized Smart Search or cleaning encoding is reported as unknown. The relevant command is rejected while its current encoding is unknown. Direct clients also leave decoded mode and feature fields unknown for unsupported firmware.

**The fan percentage is empty.** This is expected in auto mode: `preset_mode` identifies the active preset. In manual operation, `percentage` identifies one of the five speeds and `preset_mode` is empty. Auto Turbo and manual Turbo are separate selections.

**The Wi-Fi sensor is missing.** It is diagnostic and disabled by default. Enable Wi-Fi signal in the HA entity settings. See the [integration README](../custom_components/ma_ew85s_local/README.md#entities).

**Cleaning is still running after a successful action.** Success confirms the cycle started. The device finishes it automatically; the cleaning activity sensor reports its progress. Pressing Start auto cleaning while already active sends no extra command.

**The adapter reports `STATUS=ERROR`.** That raw adapter field was seen alongside successful appliance commands. It is not a decoded purifier fault or the power state. Use the validated general state and command readback.

## Diagnostics and issue reports

HA diagnostics redact the host, MAC, and the state-bearing portion of the frame. Use them alongside the integration version, HA version, firmware, action attempted, and exact error code when reporting a problem. API and direct-client status objects can contain host and MAC information, so review them before sharing.

Report reproducible problems through the [repository issue tracker](https://github.com/co0keiz/MitsubishiElectricAirPurifier/issues). Include whether the case was reproduced with the offline simulator or with hardware. Do not include keys, credentials, or raw diagnostic-login output.

For test-environment failures, see [Development and testing](development.md). The full HA suite requires the pinned Linux/Python environment; a Windows protocol-only run intentionally skips the HA modules.
