# Mitsubishi Electric MA-EW85S-E: verified local control

Technical handoff for a future local API or Home Assistant custom integration.

**Verified on 21 October 2026:** reading status and switching power ON → OFF → ON over the local network. Each transition was confirmed by a separate status request approximately five seconds after sending the command. The purifier was left ON.

This is an independently tested protocol description, not an official Mitsubishi specification. Findings apply to the tested unit and firmware. The API and Home Assistant designs below are implementation suggestions, not completed integrations.

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

**Verified:** local status, device identity, firmware, reported Wi-Fi signal, power state, and power commands. No Mitsubishi app or account was used for these operations.

**Unverified:** fan-speed commands, operating modes, louvers, PM2.5 interpretation, filter-life interpretation, other firmware versions, and other models. Do not expose these as working features based on air-conditioner implementations.

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
| 9–20 | Existing values | Preserve from fresh status | Other settings; their individual meanings are not established here |
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

The companion file `ma-ew85s-reference.ps1` contains a cleaned reference implementation of the verified algorithm. It uses **PowerShell 7+** and built-in .NET libraries. The original live tests ran on PowerShell `7.6.5`.

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

The cleaned reference client passed these offline checks and a live read-only status request during documentation preparation. The original probe performed the verified ON/OFF/ON cycle; the cleaned client uses the same power-frame algorithm. No additional power cycle was performed just to prepare these documents.

It defaults to this installation's IP and expected API MAC. Override those only for a deliberately selected compatible device:

```powershell
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action Status -DeviceHost 192.168.1.53 -ExpectedMac 70:61:be:5b:6d:1e
```

Run one invocation at a time. The reference client is synchronous and is not a Home Assistant component. A Python integration should implement the same protocol with asynchronous I/O rather than launching PowerShell.

## 10. Suggested local API contract

This is a proposed wrapper API, not a native endpoint on the purifier:

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

My tested device is at 192.168.1.53, reports API MAC 70:61:be:5b:6d:1e, model HAP MA-EW85S, and firmware 80.00. Make the host configurable; use the returned MAC for stable identity. Reading status and turning power on/off are verified. Fan speed, modes, louvers, filter life, and air-quality fields remain unverified, so do not invent their mappings.

The device uses HTTP POST /smart with an encrypted ESV XML envelope, AES-128-CBC, key b"unregistered\x00\x00\x00\x00", a random 16-byte IV, ISO/IEC 7816-4 padding, and Base64 of IV followed by ciphertext. No HTTP authentication was needed for /smart. A read-only inner request is exactly <CSV></CSV>. Decrypt the ESV response to LSV XML and find a 22-byte CODE/VALUE frame with prefix fc6201351002. Validate its checksum. Byte 8 is 0x31 for ON and 0x30 for OFF.

For a power write, clone a fresh validated status frame, set indices 1=0x41, 5=0x01, 6=0x01, 7=0x00, and 8 to the desired power value. Preserve other fields and recompute the last byte as (-sum(frame[1:-1])) & 0xff. Encrypt <CSV><CODE><VALUE>{frame.hex()}</VALUE></CODE></CSV>. The initial reply can show the previous state. Confirm success with a separate status request after about five seconds, with bounded retry handling.

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
