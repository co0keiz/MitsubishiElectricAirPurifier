# Mitsubishi Electric MA-EW85S-E: verified local control

Protocol specification for the PowerShell reference client, the ASP.NET Core API, and a future Home Assistant integration.

**Verified on 21 October 2026:** reading status and switching power ON → OFF → ON over the local network. Each transition was confirmed by a separate status request approximately five seconds after sending the command. The purifier was left ON.

**Auto-mode update, same date:** owner-labeled physical-panel captures confirmed **Normal=`42`, Night=`41`, Turbo=`43`** at general-state byte 9. The reference client then successfully switched **Turbo → Normal → Night → Turbo**, confirming each change with a separate status request. Power and bytes 10–20 stayed unchanged. The purifier was left **ON in Turbo**. See section 6a for packet evidence and usage.

**Manual-speed update, same date:** owner-labeled captures and live command readbacks verified **Silent=`30`, Low=`31`, Mid=`32`, High=`33`, Turbo=`34`** at byte 9. All five speeds worked through both PowerShell and the native ASP.NET Core API. Final state after this validation: **ON in Auto Normal**, as requested by the owner. See section 16.

**Auto-cleaning update, same date:** an owner-labeled running cycle established byte 14=`31`; the byte returned to `30` when the cycle ended. A command using mask `40 00` and byte 14=`31` successfully started another cycle, confirmed by separate readback. The native API independently started cleaning as well. Both preserved **ON in Auto Normal**. See section 17.

**Smart Search update, same date:** the owner confirmed Smart Search had been ON during the earlier captures, then selected OFF. The new capture changed byte 13 from `31` to `30`. Commands with mask `20 00` successfully selected both values through separate readback. PowerShell and the native API independently verified ON/OFF, leaving **Smart Search OFF, power ON, Auto Normal**. See section 18.

This is an independently tested protocol description, not an official Mitsubishi specification. Findings apply to the tested unit and firmware. An ASP.NET Core API is now implemented in this repository; see [README.md](README.md) for its endpoints and run instructions. The Python and Home Assistant designs below remain implementation suggestions.

## 1. Tested device and scope

| Item | Observed value |
| --- | --- |
| Product | Mitsubishi Electric MA-EW85S-E air purifier |
| Unit type from `/unitinfo` | `HAP MA-EW85S` |
| Adapter name | `MA` |
| Application firmware | `80.00` |
| IT protocol version | `04.00` |
| Local IP during testing | `192.168.1.53` |
| MAC reported by the device API | `70:61:be:5b:6d:1e` |
| Working control endpoint | `http://192.168.1.53/smart` |
| Transport | HTTP POST, TCP port 80 |
| HTTP authentication for `/smart` | None was needed |
| Application encryption key | ASCII `unregistered` + four zero bytes |

The IP and MAC above are installation-specific. A reusable integration should accept the host/IP and learn/store the device identity from its status response.

**Verified:** local status, device identity, firmware, reported Wi-Fi signal, power state, power commands, Normal/Night/Turbo auto modes, Silent/Low/Mid/High/Turbo manual speeds, Smart Search ON/OFF, and starting/observing pre-filter auto cleaning. No Mitsubishi app or account was used for these operations.

**Unverified:** manual louvre positioning, PM2.5 interpretation, filter-life interpretation, other firmware versions, and other models. The reference client's auto-mode profile is bound to the device MAC and firmware. Manual speeds and Smart Search use confirmed firmware-80.00 mappings. Do not substitute air-conditioner state mappings.

The existing cloud connection setting remained `ON`. Local commands were sent directly to the device, but this session did not test long-term operation with internet access blocked or permanently disable the manufacturer's cloud connection.

## 2. HTTP request

Send an encrypted request to:

```http
POST /smart HTTP/1.1
Host: 192.168.1.53
Content-Type: text/plain; charset=utf-8
Connection: close
User-Agent: KirigamineRemote/5.1.0
```

The headers above reproduce the working client. Whether the specific User-Agent or `Connection: close` is mandatory was not tested. No `Authorization` header was used.

The HTTP body is:

```xml
<?xml version="1.0" encoding="UTF-8"?><ESV>BASE64_OF_IV_PLUS_CIPHERTEXT</ESV>
```

`BASE64_OF_IV_PLUS_CIPHERTEXT` is a placeholder in this documentation, not literal request text.

The working client used a five-second HTTP timeout and bypassed system HTTP proxies for this LAN address.

## 3. Encryption and response decoding

Use AES-128-CBC with the following exact key:

```python
KEY = b"unregistered\x00\x00\x00\x00"
assert len(KEY) == 16
# Hex: 756e7265676973746572656400000000
```

For every request:

1. Encode the inner request XML as UTF-8. The tested inner XML has no XML declaration.
2. Apply ISO/IEC 7816-4 padding: append `0x80`, followed by zero bytes until the length is a multiple of 16. If the input is already block-aligned, append a complete padding block.
3. Generate a new cryptographically random 16-byte IV.
4. Encrypt the padded bytes using AES-CBC.
5. Concatenate `IV || ciphertext`.
6. Base64-encode that concatenation and put it inside the outer `<ESV>` element.

Pseudocode:

```python
padding_length = 16 - (len(plaintext) % 16)
padded = plaintext + b"\x80" + b"\x00" * (padding_length - 1)
ciphertext = aes_cbc_encrypt(KEY, iv, padded)
encoded = base64.b64encode(iv + ciphertext).decode("ascii")
```

The HTTP response is also an `<ESV>` envelope. Parse its text, Base64-decode it, take the first 16 bytes as the response IV, and decrypt the remaining bytes with the same key. Remove the ISO/IEC 7816-4 padding, decode UTF-8, and parse the resulting `<LSV>` XML.

Validate the envelope, Base64 data, block lengths, padding, XML, and binary-frame checksum before trusting state or issuing a write. Do not treat corrupted data as a valid OFF state. A strict client should report an unsupported/encryption/protocol error if decoding fails; automatic key discovery was not implemented or tested.

## 4. Read status without changing configuration

Inner request XML:

```xml
<CSV></CSV>
```

Do not add `CONNECT`, `ECHONET`, or `RESET` elements to a normal status request. They can change adapter settings or restart it.

The decrypted response has this structure; this example is shortened, and the serial value is omitted:

```xml
<LSV>
  <MAC>70:61:be:5b:6d:1e</MAC>
  <CONNECT>ON</CONNECT>
  <STATUS>NORMAL</STATUS>
  <PROFILECODE>
    <VALUE>fc7b013510c9040018000f010000000000000000004a</VALUE>
    <VALUE>fc7b013510cd4d0000000000005201000000070000cb</VALUE>
  </PROFILECODE>
  <DATDATE>2001/01/01 00:00:45</DATDATE>
  <CODE>
    <VALUE>fc6201351002000030424543003130000000000000fb</VALUE>
    <!-- Other CODE/VALUE frames also occur here. -->
  </CODE>
  <APP_VER>80.00</APP_VER>
  <RSSI>-58</RSSI>
  <ECHONET>OFF</ECHONET>
</LSV>
```

Useful fields:

| XML field | Treatment |
| --- | --- |
| `MAC` | Device identity, suitable for a stable integration identifier |
| `APP_VER` | Firmware text, observed as `80.00` |
| `RSSI` | Reported Wi-Fi signal; observed negative values such as `-58` dBm |
| `CODE/VALUE` | Binary state frames encoded as hexadecimal strings |
| `PROFILECODE/VALUE` | Separate capability/profile frames; their full meaning was not decoded |
| `CONNECT` | Adapter cloud connection setting, not appliance power |
| `ECHONET` | Adapter ECHONET setting, not appliance power |
| `STATUS` | Adapter status text. Both `NORMAL` and `ERROR` were seen; do not use this as power or a decoded appliance fault |
| `DATDATE` | Device timestamp. A date in 2001 appeared after startup, so use the client's receipt time for freshness |

Find the general state frame among **direct `CODE/VALUE` children**, using the prefix `fc6201351002`. Do not accidentally select `PROFILECODE/VALUE` or an unrelated sensor/status frame.

## 5. Binary state frame and power decoding

Hex-decode the selected frame into 22 bytes. Indices in this document are zero-based.

| Byte index | Read response | Power command | Interpretation |
| --- | --- | --- | --- |
| 0 | `FC` | `FC` | Frame start |
| 1 | `62` | `41` | Observed response/write message types |
| 2–3 | `01 35` | `01 35` | Tested air-purifier family identifier; do not substitute the AC identifier `01 30` |
| 4 | `10` | `10` | Payload length byte: 16 |
| 5 | `02` | `01` | Observed status/write subtypes |
| 6–7 | `00 00` | `01 00` | Working power-only write mask |
| 8 | `30` or `31` | `30` or `31` | **`30` = OFF; `31` = ON**, confirmed against physical button changes and local commands |
| 9 | Existing value | Preserve for power writes | Operating-mode value; writable separately with mask `02 00`; learn auto-mode labels from the physical panel |
| 10–12 | Existing values | Preserve from fresh status | Other settings; their individual meanings are not established here |
| 13 | `30` or `31` | Preserve for other writes | Smart Search setting: `30` = OFF, `31` = ON; write separately with mask `20 00` |
| 14 | `30` or `31` | Preserve for power/mode writes | Auto-cleaning activity: `30` = idle, `31` = active; start separately with mask `40 00` |
| 15–20 | Existing values | Preserve from fresh status | Other settings; their individual meanings are not established here |
| 21 | Checksum | Recalculate | Sum of bytes 1–21 modulo 256 must be zero |

Reject a frame with the wrong length, prefix, checksum, or an unrecognized power byte. Power is not encoded as `00/01` in this model's observed state frame.

Real captured fixtures:

```text
ON status:  fc6201351002000031424543003130000000000000fa
OFF status: fc6201351002000030424543003130000000000000fb
```

## 6. Construct and send a power command

Build each command from a fresh validated general state frame. Preserve all other fields rather than copying an old unit's complete settings.

```python
def valid_frame_checksum(frame: bytes) -> bool:
    return (sum(frame[1:]) & 0xFF) == 0


def parse_power(frame: bytes) -> bool:
    if len(frame) != 22 or frame[:6] != bytes.fromhex("fc6201351002"):
        raise ValueError("Unsupported purifier status frame")
    if not valid_frame_checksum(frame):
        raise ValueError("Invalid frame checksum")
    if frame[8] not in (0x30, 0x31):
        raise ValueError("Unknown power encoding")
    return frame[8] == 0x31


def build_power_command(status_frame: bytes, enabled: bool) -> bytes:
    parse_power(status_frame)  # Validate before constructing a write.
    frame = bytearray(status_frame)
    frame[1] = 0x41
    frame[5] = 0x01
    frame[6] = 0x01
    frame[7] = 0x00
    frame[8] = 0x31 if enabled else 0x30
    frame[-1] = (-sum(frame[1:-1])) & 0xFF
    return bytes(frame)
```

Wrap the resulting hex string in inner XML:

```xml
<CSV><CODE><VALUE>COMMAND_HEX</VALUE></CODE></CSV>
```

Then encrypt and POST it using sections 2–3.

Exact command frames that worked with this unit's captured settings:

```text
ON:  fc41013510010100314245430031300000000000001b
OFF: fc41013510010100304245430031300000000000001c
```

Exact plaintext requests used successfully:

```xml
<CSV><CODE><VALUE>fc41013510010100314245430031300000000000001b</VALUE></CODE></CSV>
```

```xml
<CSV><CODE><VALUE>fc41013510010100304245430031300000000000001c</VALUE></CODE></CSV>
```

### Checksum pitfall

Correct:

```python
frame[-1] = (-sum(frame[1:-1])) & 0xFF
```

This excludes the initial `FC` and the old checksum. Equivalently, the final bytes excluding `FC` must sum to zero modulo 256.

**Do not calculate `0xFC - sum(frame[1:-1])`.** That was the implementation error in the earlier unsuccessful attempts. Correcting it was followed by successful power control. The captured ON/OFF status checksums are `FA` and `FB`; the example command checksums are `1B` and `1C`.

## 6a. Learn and select Normal, Night, and Turbo

The mode field is **byte 9**, with write mask **`02 00`** at bytes 6–7. Construct a mode command by cloning fresh validated status, setting bytes 1=`41`, 5=`01`, 6=`02`, 7=`00`, and 9 to the desired learned value, then recalculating the checksum. Preserve byte 8 (power) and bytes 10–20.

Live probe evidence on the tested firmware:

```text
Before:   fc6201351002000031424543003130000000000000fa
Command:  fc410135100102003143454300313000000000000019
Readback: fc6201351002000031434543003130000000000000f9
Restore:  fc41013510010200314245430031300000000000001a
Readback: fc6201351002000031424543003130000000000000fa
```

Owner-labeled physical-panel captures confirmed all three mappings. The same reference client then issued all three mode commands successfully:

| Mode | Byte 9 | Owner-labeled capture (UTC) | Separate command readback (UTC) |
| --- | --- | --- | --- |
| Normal | `42` | `2026-10-21T19:01:09Z` | `2026-10-21T19:03:54Z` |
| Night | `41` | `2026-10-21T19:02:28Z` | `2026-10-21T19:03:59Z` |
| Turbo | `43` | `2026-10-21T19:03:18Z` | `2026-10-21T19:04:04Z` |

Captured status and corresponding command fixtures, with the other settings as observed during testing:

```text
Normal status:  fc6201351002000031424543003130000000000000fa
Night status:   fc6201351002000031414543003130000000000000fb
Turbo status:   fc6201351002000031434543003130000000000000f9
Normal command: fc41013510010200314245430031300000000000001a
Night command:  fc41013510010200314145430031300000000000001b
Turbo command:  fc410135100102003143454300313000000000000019
```

Each mode transition used fresh status, a mode-only command, and a separate readback about five seconds later. Power stayed ON and bytes 10–20 stayed unchanged. The final mode was Turbo. Build commands from current status rather than replaying the complete fixtures.

**This installation is ready:** `ma-ew85s-auto-modes.json` was saved from the three owner-labeled captures. Use `SetAutoMode` below directly; no further learning is needed for this device/firmware. The local profile is ignored by Git, so a fresh checkout needs its own profile.

To create or replace a profile, run the one-time interactive learning action in a PowerShell 7 terminal with the purifier ON:

```powershell
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action LearnAutoModes
```

At each prompt, use the purifier's physical **Auto** button to select the requested **Normal**, **Night**, or **Turbo** lamp, then press Enter. Do not use the Manual fan-speed button. The script waits five seconds before each capture. Type `q` to cancel. Learning sends only status requests; it does not send mode or power commands. It leaves the device in the last mode you selected (Turbo after completing all three prompts).

Three distinct validated mode captures are saved in `ma-ew85s-auto-modes.json` beside the script, bound to the expected MAC and reported firmware. The file is ignored by Git. An incomplete, duplicate, or cancelled learning run does not overwrite an existing profile. `-ModeProfilePath <path>` selects another location; use the same path for learning, status, and mode selection. The parent directory must already exist.

Then select modes explicitly:

```powershell
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SetAutoMode -AutoMode Normal
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SetAutoMode -AutoMode Night
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SetAutoMode -AutoMode Turbo
```

Mode selection requires the purifier to be ON; use the existing `-Action On` first when necessary. A request for the current mode returns status without sending a write. Other selections send one mode-only command and perform up to three separate readbacks five seconds apart. Success requires the requested mode and unchanged power, with identity validation on every readback. Unconfirmed commands are not resent automatically.

`Status` and write results include `OperatingMode`, `AutoMode`, `ManualSpeed`, and `AutoModeRaw`. `AutoMode` is null before learning, for values absent from the profile, when manual operation is selected, or when the saved profile is invalid. Raw values remain visible; `AutoModeRaw` contains the common selector byte for both auto and manual operation. A mismatched or missing profile prevents auto-mode writes; manual speeds use the firmware-specific mapping instead. Relearn after changing the device or firmware.

## 7. Verify command completion

An HTTP 200 response confirms an HTTP exchange, not completed power control. In the successful tests, the immediate decrypted command response still contained the **previous** power state.

The verified sequence was:

1. Read fresh status and confirm the expected device identity.
2. Validate and clone its general state frame.
3. Send one power command.
4. Wait approximately five seconds.
5. Make a separate status request.
6. Decode byte 8 and require it to equal the requested power value.

Observed results:

| Command | Before | Separate readback | Result |
| --- | --- | --- | --- |
| ON | `30` / OFF | `31` / ON | Verified |
| OFF | `31` / ON | `30` / OFF | Verified |
| ON | `30` / OFF | `31` / ON | Verified; final state |

Suggested integration behavior, beyond the single readback used in testing:

- Serialize each device's status and write operations with a lock.
- Use bounded readback retries, for example a first check after five seconds and subsequent checks up to a 15-second deadline. These timing limits are starting defaults, not measured hardware guarantees.
- After a timeout, read current state before considering a resend; the original command might have succeeded.
- Use explicit desired ON/OFF states, not toggle commands, for retries.
- Preserve the last confirmed state and its receipt timestamp; mark it stale/unavailable on failure rather than displaying an assumed success.
- A 30-second idle polling interval is a proposed starting point. Maximum request rate and long-term reliability were not characterized.

## 8. Diagnostics and limitations

### Optional browser diagnostics

The following browser login was verified on this firmware:

```text
Username: admin
Password: me1debug@0567
```

`GET /unitinfo` returned model, firmware, and communication information. The root page `/` was a network-configuration form. These are optional diagnostics; `/smart` power/status requests did not need those HTTP credentials.

Do not make normal integration setup depend on `/unitinfo` or the diagnostic login. Other firmware may restrict them. Keep network setup, debug toggles, factory reset, firmware update, and arbitrary raw commands outside the first integration's scope.

### ECHONET

An ECHONET setting was exposed and briefly enabled during investigation. The attempted UDP queries did not produce a usable response, and the setting was restored to OFF. Working power control used `/smart` with ECHONET OFF. Enabling ECHONET is not a prerequisite for this implementation.

### Temporary loss of reachability

During the session the device temporarily stopped responding at its IP. The cause was not established. Later it became reachable again at `192.168.1.53`, after which the corrected power commands succeeded.

Diagnostics used ping, HTTP port 80, and local address-resolution checks. A steady Wi-Fi light did not by itself establish reachability from the computer. A future client should report a connection failure separately from a protocol/decryption error and allow the configured host/IP to be updated. Routine recovery must not automatically reset or reconfigure the purifier.

### Access boundary

The control endpoint accepted requests without HTTP authentication and uses a shared, publicly known application key. Treat this as a trusted-LAN interface. A separate API service should bind to loopback by default and require appropriate access controls before being exposed to other hosts; do not expose the purifier directly to the internet.

## 9. Reference client

The companion file `ma-ew85s-reference.ps1` contains a reference implementation of the verified power and mode-field algorithms. It uses **PowerShell 7+** and built-in .NET libraries. The original live tests and the mode-field probe ran on PowerShell `7.6.5`. See section 6a for auto-mode learning and selection.

From the folder containing that file:

```powershell
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action Status
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action On
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action Off
```

Run its offline packet/encryption checks without contacting the purifier:

```powershell
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SelfTest
```

The reference client passed offline packet, encryption, profile-validation, learning/cancellation, and simulated mode-readback checks. Mode-readback tests cover a stale initial reply, delayed updates, bounded timeout without resending, a no-op, an OFF device, identity mismatches before and after writing, changed firmware, and changed power. Live mode selection also passed for Normal, Night, and Turbo, leaving the purifier ON in Turbo with other general-state settings preserved. No additional power cycle was performed for the mode update.

It defaults to this installation's IP and expected API MAC. Override those only for a deliberately selected compatible device:

```powershell
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action Status -DeviceHost 192.168.1.53 -ExpectedMac 70:61:be:5b:6d:1e
```

Run one invocation at a time. The reference client is synchronous and is not a Home Assistant component. A Python integration should implement the same protocol with asynchronous I/O rather than launching PowerShell.

## 10. Suggested local API contract

This is the original proposed wrapper contract, not a native endpoint on the purifier. The implemented ASP.NET Core API uses `/api/purifier/status`, `/api/purifier/power`, and `/api/purifier/auto-mode`; see [README.md](README.md) for its actual request/response contract.

```http
GET /api/purifiers/{device_id}/state
PUT /api/purifiers/{device_id}/power
Content-Type: application/json

{"on": true}
```

Example wrapper response shape:

```json
{
  "device_id": "7061be5b6d1e",
  "host": "192.168.1.53",
  "model": "MA-EW85S-E",
  "firmware": "80.00",
  "power": true,
  "available": true,
  "confirmed": true,
  "observed_at": "<client-generated ISO 8601 timestamp>"
}
```

A synchronous power endpoint should complete only after readback verification, or return a clearly identified timeout/connection/protocol error. An asynchronous endpoint should return a pending operation and offer operation-status retrieval. Do not report `confirmed: true` from the initial command response.

## 11. Suggested Home Assistant implementation

These are recommendations for a future custom integration. Recheck current Home Assistant requirements when building it.

- Start with a power **switch** entity. Add fan or air-quality features only after their commands/fields have been verified. `async_turn_on` and `async_turn_off` should call the same explicit power operation. See the [switch entity documentation](https://contributors.home-assistant.io/core/entities/switch/).
- Use one asynchronous protocol client and one coordinator per device. Keep polling and network I/O out of entity properties. See [fetching data and DataUpdateCoordinator](https://contributors.home-assistant.io/core/integrations/fetching-data/).
- A user config flow should accept a host/IP, validate a status response, and derive a stable unique ID from the returned MAC. Detect duplicates and support host changes. See [config flows and stable identifiers](https://contributors.home-assistant.io/core/integrations/config-flow/).
- Describe the integration as `local_polling` in its manifest. See the [integration manifest documentation](https://contributors.home-assistant.io/core/integrations/manifest/).
- Publish confirmed state after commands, distinguish unavailable from OFF, and cleanly unload timers, sessions, and entities. Default setup should not alter cloud or ECHONET settings.

Possible initial structure:

```text
custom_components/ma_ew85s_local/
  __init__.py
  manifest.json
  config_flow.py
  coordinator.py
  const.py
  switch.py
  strings.json
  translations/en.json
```

Keep the reusable protocol code in a separate module/library so an API wrapper and the Home Assistant integration can share it. This proposed file layout is not an existing package.

## 12. Prompt to reuse with a coding assistant

Copy the following and attach this document plus `ma-ew85s-reference.ps1`:

The same prompt is supplied separately in `implementation-prompt.txt` for easy copying.

```text
Build local control software for my Mitsubishi Electric MA-EW85S-E air purifier using the attached verified protocol specification and reference client. First implement a reusable asynchronous Python protocol library. Then implement either a Home Assistant custom integration or a local REST API wrapper, according to the target I choose below. Do not build both front ends unless I request both.

Target: [Home Assistant custom integration / local REST API].

My tested device is at 192.168.1.53, reports API MAC 70:61:be:5b:6d:1e, model HAP MA-EW85S, and firmware 80.00. Make the host configurable; use the returned MAC for stable identity. Reading status, turning power on/off, Normal/Night/Turbo auto modes, Silent/Low/Mid/High/Turbo manual speeds, Smart Search ON/OFF, and starting pre-filter auto cleaning are verified. Manual louvre positioning, filter life, and air-quality fields remain unverified, so do not invent their mappings.

The device uses HTTP POST /smart with an encrypted ESV XML envelope, AES-128-CBC, key b"unregistered\x00\x00\x00\x00", a random 16-byte IV, ISO/IEC 7816-4 padding, and Base64 of IV followed by ciphertext. No HTTP authentication was needed for /smart. A read-only inner request is exactly <CSV></CSV>. Decrypt the ESV response to LSV XML and find a 22-byte CODE/VALUE frame with prefix fc6201351002. Validate its checksum. Byte 8 is 0x31 for ON and 0x30 for OFF.

For a power write, clone a fresh validated status frame, set indices 1=0x41, 5=0x01, 6=0x01, 7=0x00, and 8 to the desired power value. Preserve other fields and recompute the last byte as (-sum(frame[1:-1])) & 0xff. Encrypt <CSV><CODE><VALUE>{frame.hex()}</VALUE></CODE></CSV>. The initial reply can show the previous state. Confirm success with a separate status request after about five seconds, with bounded retry handling.

For an auto-mode write, clone fresh validated status, set indices 1=0x41, 5=0x01, 6=0x02, 7=0x00, and 9 to 0x42 for Normal, 0x41 for Night, or 0x43 for Turbo. Preserve power (byte 8) and bytes 10-20, then recompute the checksum as above. Require the purifier to be ON. Confirm the desired mode and unchanged power using separate bounded readback requests. These mappings were confirmed by owner-labeled physical-panel captures and successful local commands on firmware 80.00.

For a manual-speed write, use the same mode-only construction and mask, with byte 9 set to 0x30 for Silent, 0x31 for Low, 0x32 for Mid, 0x33 for High, or 0x34 for Turbo. This selects manual operation, distinct from auto modes. Manual Turbo is 0x34; Auto Turbo is 0x43. Require power ON, preserve power and bytes 10-20, and confirm the requested manual speed and unchanged power through separate readback. All five manual speeds were owner-labeled and successfully commanded through both PowerShell and the native ASP.NET Core API on firmware 80.00.

For a pre-filter auto-cleaning start, clone fresh validated status, set bytes 1=0x41, 5=0x01, 6=0x40, 7=0x00, and 14=0x31, preserving all other fields and recalculating the checksum. Require power ON and firmware 80.00. Status byte 14 is 0x30 when idle and 0x31 when cleaning is active; unknown values must not be treated as idle. If already active, return status without restarting. Confirm active cleaning through separate readback, with unchanged power and operating mode. This confirms start, not completion. The start command and active readback were verified on the device and through the native API.

For Smart Search ON/OFF, clone fresh validated status, set bytes 1=0x41, 5=0x01, 6=0x20, 7=0x00, and 13=0x31 for ON or 0x30 for OFF, preserving all other fields and recalculating the checksum. Require power ON and firmware 80.00. Unknown byte-13 encodings must not be treated as OFF. If already at the requested state, return fresh status without writing. Confirm the explicit setting through separate readback with unchanged power and operating mode. Both directions were verified through PowerShell and the native API.

Serialize each device's operations. Use explicit desired states, robust validation, bounded timeouts, safe handling of ambiguous network failures, and confirmed state reporting. Do not reset the purifier, modify its Wi-Fi configuration, toggle debug mode, enable ECHONET, change cloud settings, or send arbitrary experimental commands as part of routine operation. Keep the wrapper on the trusted local network.

Add meaningful tests using the captured ON/OFF status frames and exact successful command frames in the specification. Cover checksum calculation, field preservation, padding/encryption round trips, malformed responses, unknown models/states, delayed readback, timeout recovery, and device identity mismatches. For Home Assistant, follow the current official integration conventions and start with a power switch. For a REST API, return confirmed results or explicit pending/error states. Do not run live power-cycle tests until the implementation and offline checks are ready for review.
```

## 13. Implementation acceptance tests

Before live testing, demonstrate all of the following:

1. Parse the captured ON/OFF frames as true/false, respectively.
2. Validate both captured state checksums and both successful command checksums.
3. Build the exact ON command from the captured OFF frame and the exact OFF command from the captured ON frame.
4. Preserve bytes 9–20 when constructing a write from any validated frame, including a fixture with altered settings and a corrected checksum.
5. Reject invalid lengths, a changed checksum, an air-conditioner `0130` frame, and an unknown power byte.
6. Round-trip UTF-8 request XML with random IVs, including block-aligned input and ISO/IEC 7816-4 padding. Fixed IVs may be used for deterministic unit tests only.
7. Reject malformed encrypted envelopes/padding and malformed or unexpected response XML.
8. Simulate an initial write response that still says OFF, followed by a status response saying ON; confirm only after the latter.
9. Simulate timeouts, stale state, and identity mismatches without displaying false success or writing to a replacement device.

## 14. Evidence and further references

The MA-EW85S-E power values, frame construction, packet examples, and ON/OFF/ON results above come from this session's direct tests on the owner's unit. They are not inferred from a product listing or advertised air-conditioner compatibility.

Related primary implementation sources consulted during investigation:

- [PyMitsubishi communication implementation](https://github.com/pymitsubishi/pymitsubishi/blob/main/pymitsubishi/mitsubishi_api.py): related Mitsubishi transport/encryption and diagnostic-login implementation. Its supported AC hardware is different from this purifier.
- [PyMitsubishi packet parser and writer](https://github.com/pymitsubishi/pymitsubishi/blob/main/pymitsubishi/mitsubishi_parser.py): related framing and checksum implementation. Do not reuse its AC state mappings for the purifier.
- [Original Mitsubishi Wi-Fi adapter investigation](https://blog.dest-unreach.be/2022/06/04/mitsubishi-wifi-adapter/): background on the related adapter interface.

Home Assistant documentation links were checked on 21 October 2026. Follow their current versions when implementing the future integration.

## 15. Implemented ASP.NET Core API

The solution now includes `src/AirPurifier.Core` (native asynchronous C# protocol client), `src/AirPurifier.Api` (ASP.NET Core 10), and `tests/AirPurifier.Tests` (offline protocol and HTTP integration tests). The API does not launch PowerShell or depend on its installation-specific mode profile. It uses the confirmed firmware-80.00 mappings from section 6a and rejects writes on unsupported firmware.

Run `dotnet run --project src/AirPurifier.Api` from the repository root. The default binding is `http://localhost:5080`. The API exposes fresh status, explicit power selection, and Normal/Night/Turbo selection. Every write uses fresh identity-checked status and separate bounded readback; device operations are serialized. Errors distinguish invalid requests, an OFF device, protocol/identity failures, connection failures, and unconfirmed or timed-out commands.

Validation completed on 21 October 2026: **55 offline tests passed**. A live HTTP check against the running API read status, confirmed a power no-op, and switched **Normal → Night → Normal**, with separate device readback for each transition. The purifier was left **ON in Normal**, its mode at the start of this API check. No live power cycle was performed. The temporary test server was stopped afterward.

## 16. Verified manual speeds

The owner selected each speed using the physical Manual button. Fresh status captures confirmed the following mappings at **byte 9** on firmware `80.00`:

| Manual speed | Byte 9 | Owner-labeled capture (UTC, 21 October 2026) |
| --- | --- | --- |
| Silent | `30` | `19:27:35Z` |
| Low | `31` | `19:34:04Z` |
| Mid | `32` | `19:37:43Z` |
| High | `33` | `19:38:56Z` |
| Turbo | `34` | `19:40:03Z` |

Captured status and corresponding command fixtures, with the other settings observed during testing:

```text
Silent status:  fc62013510020000313045430031300000000000000c
Low status:     fc62013510020000313145430031300000000000000b
Mid status:     fc62013510020000313245430031300000000000000a
High status:    fc620135100200003133454300313000000000000009
Turbo status:   fc620135100200003134454300313000000000000008
Silent command: fc41013510010200313045430031300000000000002c
Low command:    fc41013510010200313145430031300000000000002b
Mid command:    fc41013510010200313245430031300000000000002a
High command:   fc410135100102003133454300313000000000000029
Turbo command:  fc410135100102003134454300313000000000000028
```

Manual operation uses the same selector and mode-only write mask `02 00` as auto operation. Clone fresh validated status, set bytes 1=`41`, 5=`01`, 6=`02`, 7=`00`, and 9 to the requested speed, then recalculate the checksum. Preserve power (byte 8) and bytes 10–20. Manual Turbo=`34` is distinct from Auto Turbo=`43`.

The API exposes `PUT /api/purifier/manual-speed` with a JSON body such as `{"speed":"low"}`; accepted values are `silent`, `low`, `mid`, `high`, and `turbo`. The PowerShell equivalent is `-Action SetManualSpeed -ManualSpeed Low`, accepting all five speed names. Both require power ON and firmware `80.00`. No auto-mode profile is needed for manual commands. A matching speed returns fresh status without a write; other selections use one command and up to three separate readbacks, without resending the command.

Status reports `OperatingMode`, `AutoMode`, and `ManualSpeed` (camel case in JSON), with inactive decoded values set to null. `AutoModeRaw` remains the common raw selector byte for compatibility. Selecting a manual speed switches to manual operation; selecting an auto mode switches back.

**Live verification:** PowerShell successfully selected Silent → Low → Mid → High → Turbo, each confirmed through separate readback between `19:40:35Z` and `19:40:56Z`, then selected Auto Normal at `19:41:01Z`. The running native ASP.NET Core API independently selected all five manual speeds between approximately `19:41:32Z` and `19:41:53Z`, then confirmed **ON in Auto Normal** at approximately `19:41:58Z`, as requested by the owner. Power and bytes 10–20 stayed unchanged throughout both manual-speed sequences. No power cycle was performed; the temporary API server was stopped afterward.

**Offline validation:** all **82 C# tests** passed. PowerShell's self-test passed as well, covering all five exact manual command fixtures, auto/manual distinction, field preservation, delayed readback, no-op, bounded timeout without resend, invalid speed, and identity/firmware/power rejection.

## 17. Verified pre-filter auto-cleaning start

An owner-labeled active cycle was captured at `2026-10-21T19:46:38Z`. Compared with idle Auto Normal, only general-state byte 14 changed from `30` to `31` (plus the checksum). A read-only observation saw the cycle finish and byte 14 return to `30` at `19:49:08Z`.

The successful start command clones fresh validated status, sets bytes 1=`41`, 5=`01`, 6=`40`, 7=`00`, and 14=`31`, and recalculates the checksum. Preserve power, the operating-mode selector, and all other fields. Tested fixtures:

```text
Idle status:     fc6201351002000031424543003130000000000000fa
Start command:   fc4101351001400031424543003131000000000000db
Active readback: fc6201351002000031424543003131000000000000f9
```

The targeted reference-client probe sent this start command once and confirmed active cleaning through a separate status request at `19:49:52Z`. Power and bytes 9–13 and 15–20 stayed unchanged. After that cycle ended, the native ASP.NET Core API independently changed idle to active and confirmed **ON, Auto Normal, cleaning active** at `19:52:44Z`. The PowerShell `StartAutoCleaning` action subsequently returned the active state without sending another start.

Use `POST /api/purifier/auto-cleaning` with no body, or `pwsh -File ./ma-ew85s-reference.ps1 -Action StartAutoCleaning`. Both require ON state, firmware `80.00`, and a recognized idle/active encoding. They skip the write when already active. A start uses one write and up to three separate readbacks. Success requires active cleaning and unchanged power/mode. It confirms **start**, not completion of the cycle. No stop, reset, scheduling, or filter-counter operation is exposed.

Status includes nullable `IsAutoCleaning` (`isAutoCleaning` in JSON): `false` for byte 14=`30`, `true` for `31`, and null for unknown encodings or unsupported firmware. Do not treat an unknown byte as idle. The selected auto/manual mode remains a separate state field.

The C# suite now has **93 passing tests**. Tests cover the exact successful cleaning command, field preservation, an active immediate acknowledgement followed by delayed status confirmation, an already-running no-op, natural completion, bounded unconfirmed starts without resending, rejected OFF/unknown/unsupported states, power/mode changes during readback, the HTTP start endpoint, and its OpenAPI API-key metadata. PowerShell's expanded offline checks pass as well. The temporary API server was stopped without interrupting the cleaning cycle.

## 18. Verified Smart Search ON/OFF

The owner confirmed Smart Search was ON throughout the earlier captures. After the owner switched it OFF, a fresh status capture at `2026-10-21T19:58:54Z` returned `fc6201351002000031424543003030000000000000fb`. Relative to the earlier idle Auto Normal frame, only byte 13 changed from `31` to `30`, plus the checksum.

Smart Search uses **byte 13**, with write mask **`20 00`** at bytes 6–7. Clone fresh validated status, set bytes 1=`41`, 5=`01`, 6=`20`, 7=`00`, and 13=`31` for ON or `30` for OFF. Recalculate the checksum and preserve all other fields. Tested fixtures:

```text
Smart Search ON status:  fc6201351002000031424543003130000000000000fa
Smart Search OFF status: fc6201351002000031424543003030000000000000fb
Smart Search ON command: fc4101351001200031424543003130000000000000fc
Smart Search OFF command:fc4101351001200031424543003030000000000000fd
```

The targeted probe confirmed ON at `20:00:07Z` and OFF at `20:00:12Z`, with separate status requests. Power, operating mode, and all other general-state fields stayed unchanged. The production PowerShell implementation independently passed ON/OFF live checks. The native API then confirmed ON at `20:04:16Z` and OFF at `20:04:21Z`. Final state: **Smart Search OFF, power ON, Auto Normal**. The temporary API server was stopped afterward.

Use `PUT /api/purifier/smart-search` with `{"enabled":true}` or `{"enabled":false}`. PowerShell uses `-Action SetSmartSearch -SmartSearch On` or `-Action SetSmartSearch -SmartSearch Off`. Both require power ON, firmware `80.00`, and a recognized current Smart Search encoding. No auto-mode profile is needed. Matching states are read-only no-ops. Other requests send one command and verify the requested setting through up to three separate readbacks, with unchanged power/mode. Commands are never automatically resent.

Status includes nullable `IsSmartSearchEnabled` (`isSmartSearchEnabled` in JSON): `false` for byte 13=`30`, `true` for `31`, and null for unknown encodings or unsupported firmware. Smart Search is independent of the selected auto/manual operating mode and of the cleaning activity flag at byte 14. Manual louvre-angle commands remain unverified.

The current C# suite has **109 passing tests**, and PowerShell's expanded self-test passes. New tests cover both exact commands, preservation of unrelated fields and source frames, delayed readback despite an acknowledgement already showing the target state, true and false no-ops, bounded failure without resend, OFF/unknown/unsupported-state rejection, changed power/mode during readback, both HTTP boolean values, missing/invalid bodies, and the endpoint's OpenAPI API-key metadata.
