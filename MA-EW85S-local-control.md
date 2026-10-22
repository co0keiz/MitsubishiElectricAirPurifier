# Mitsubishi Electric MA-EW85S-E: verified local control

Protocol and integration specification for the Mitsubishi Electric MA-EW85S-E air purifier on its local network interface. It is the single reference for the PowerShell reference client, the ASP.NET Core API, the planned Python library, and the Home Assistant integration.

This is an independently tested protocol description, not an official Mitsubishi specification. Findings apply to the tested unit and firmware (`80.00`). Dated live evidence is kept in [section 11](#11-evidence-log-and-references); every other section describes the current, authoritative behavior.

Contents: [1 Status and scope](#1-status-and-scope) · [2 Device and transport](#2-device-and-transport) · [3 Encryption and envelope](#3-encryption-and-envelope) · [4 Reading status](#4-reading-status) · [5 General state frame](#5-general-state-frame) · [6 Writing settings](#6-writing-settings) · [7 Confirmation, errors, and polling](#7-confirmation-errors-and-polling) · [8 Fixtures appendix](#8-fixtures-appendix) · [9 Implementations](#9-implementations) · [10 Diagnostics, ECHONET, reachability, and security](#10-diagnostics-echonet-reachability-and-security) · [11 Evidence log and references](#11-evidence-log-and-references) · [12 Acceptance test checklist](#12-acceptance-test-checklist)

## 1. Status and scope

### Tested device

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

The IP and MAC above are installation-specific. A reusable integration must accept the host/IP and learn/store the device identity (the MAC) from its status response. No Mitsubishi app or account was used for any verified operation.

### Verification matrix

"Verified" means a live command or capture on the owner's unit, confirmed by a separate status request. Dates and timestamps are in the [evidence log](#112-dated-live-verification).

| Feature | Evidence | PowerShell | ASP.NET Core API | Python / Home Assistant |
| --- | --- | --- | --- | --- |
| Read status, identity, firmware, RSSI | Live status requests | Yes | Yes | Planned |
| Power ON / OFF (byte 8) | Live ON → OFF → ON with separate readbacks | Yes | Yes | Planned |
| Auto mode Normal `42` / Night `41` / Turbo `43` (byte 9) | Owner-labeled panel captures and live command readbacks | Yes (learned profile) | Yes | Planned |
| Manual speed Silent `30` … Turbo `34` (byte 9) | Owner-labeled panel captures and live command readbacks | Yes | Yes | Planned |
| Smart Search ON / OFF (byte 13) | Owner-confirmed capture change and live ON/OFF readbacks | Yes | Yes | Planned |
| Pre-filter auto-cleaning start and activity flag (byte 14) | Owner-labeled running cycle and live start readbacks | Yes | Yes | Planned |
| Wi-Fi signal (`RSSI`) | Reported value, for example `-58` dBm | Reported | Reported | Planned (diagnostic) |
| Manual louvre positioning | Not tested | No | No | Out of scope |
| PM2.5 / air-quality interpretation | Not decoded | No | No | Out of scope |
| Filter-life interpretation | Not decoded | No | No | Out of scope |
| Other firmware versions and other models | Not tested | Reads only | Reads only; writes rejected | Setup rejects other firmware |
| ECHONET, cloud, Wi-Fi, reset operations | Deliberately not used | No | No | Out of scope |

**Unverified items must not be guessed.** Do not substitute air-conditioner state mappings from related Mitsubishi projects. The reference client's auto-mode profile is bound to the device MAC and firmware; manual speeds, Smart Search, and cleaning use the confirmed firmware-`80.00` mappings built into the native clients.

The cloud connection setting remained `ON` throughout testing. Local commands went directly to the device, but long-term operation with internet access blocked was not tested, and the manufacturer's cloud connection was never disabled.

## 2. Device and transport

Send an encrypted request to:

```http
POST /smart HTTP/1.1
Host: 192.168.1.53
Content-Type: text/plain; charset=utf-8
Connection: close
User-Agent: KirigamineRemote/5.1.0
```

The headers above reproduce the working clients. Whether the specific User-Agent or `Connection: close` is mandatory was not tested. No `Authorization` header is used.

The HTTP body is:

```xml
<?xml version="1.0" encoding="UTF-8"?><ESV>BASE64_OF_IV_PLUS_CIPHERTEXT</ESV>
```

`BASE64_OF_IV_PLUS_CIPHERTEXT` is a placeholder in this documentation, not literal request text.

Client behavior used by the working implementations:

- Five-second timeout per HTTP request.
- Bypass system HTTP proxies for the LAN address; do not follow redirects.
- Bound the response body (the C# client allows at most 65,536 characters) before decrypting, including chunked responses.
- Send one request at a time per device (see [section 7](#7-confirmation-errors-and-polling)).

## 3. Encryption and envelope

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

```python
padding_length = 16 - (len(plaintext) % 16)
padded = plaintext + b"\x80" + b"\x00" * (padding_length - 1)
ciphertext = aes_cbc_encrypt(KEY, iv, padded)
encoded = base64.b64encode(iv + ciphertext).decode("ascii")
```

The HTTP response is also an `<ESV>` envelope. Parse its text, Base64-decode it, take the first 16 bytes as the response IV, and decrypt the remaining bytes with the same key. Remove the ISO/IEC 7816-4 padding (trailing zero bytes, then a `0x80` within the final 16 bytes), decode UTF-8 strictly, and parse the resulting `<LSV>` XML.

Validate all of the following before trusting state or issuing a write; any failure is a protocol error, never a valid OFF state:

- the envelope root is `ESV` with no child elements, and its Base64 text decodes;
- the decoded length is at least 32 bytes and `(length - 16)` is a multiple of 16;
- the padding and strict UTF-8 are valid;
- the XML parses with DTD processing prohibited, no external resolver, and a size limit;
- the root is `LSV`, and the frame passes the rules in [section 5](#5-general-state-frame).

Automatic key discovery was not implemented or tested.

## 4. Reading status

Inner request XML (read-only):

```xml
<CSV></CSV>
```

Do not add `CONNECT`, `ECHONET`, or `RESET` elements to a normal status request. They can change adapter settings or restart it.

The decrypted response has this structure; the example is shortened and the serial value is omitted:

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

| XML field | Treatment |
| --- | --- |
| `MAC` | Device identity and stable integration identifier. Compare case-insensitively |
| `APP_VER` | Firmware text, observed as `80.00`. Writes are verified only for `80.00` |
| `RSSI` | Reported Wi-Fi signal; observed negative values such as `-58` dBm |
| `CODE/VALUE` | Binary state frames encoded as hexadecimal strings |
| `PROFILECODE/VALUE` | Separate capability/profile frames; their meaning was not decoded |
| `CONNECT` | Adapter cloud connection setting, not appliance power |
| `ECHONET` | Adapter ECHONET setting, not appliance power |
| `STATUS` | Adapter status text. Both `NORMAL` and `ERROR` were seen; do not use it as power or as a decoded appliance fault |
| `DATDATE` | Device timestamp. A date in 2001 appeared after startup, so use the client's receipt time for freshness |

Find the general state frame among the **direct `CODE/VALUE` children**, using the first value that starts with the prefix `fc6201351002` (case-insensitive). Do not select `PROFILECODE/VALUE` or an unrelated sensor/status frame.

## 5. General state frame

Hex-decode the selected frame into **22 bytes**. Indices are zero-based. The same layout is used by status frames (`62`) and command frames (`41`).

### 5.1 Byte map

| Byte | Status value | Command value | Meaning |
| --- | --- | --- | --- |
| 0 | `fc` | `fc` | Frame start |
| 1 | `62` | `41` | Message type: `62` response/status, `41` write |
| 2–3 | `01 35` | `01 35` | Air-purifier family identifier. Do not substitute the air-conditioner identifier `01 30` |
| 4 | `10` | `10` | Payload length: 16 |
| 5 | `02` | `01` | Subtype: `02` status, `01` write |
| 6–7 | `00 00` | mask, see 5.2 | Write mask selecting which single field the command changes |
| 8 | `30` / `31` | preserved or target | **Power: `30` = OFF, `31` = ON.** Not `00/01` |
| 9 | see 5.3 | preserved or target | **Operating-mode selector** shared by auto modes and manual speeds |
| 10–12 | `45 43 00` in every captured fixture | preserved | Other settings; meaning not established |
| 13 | `30` / `31` | preserved or target | **Smart Search: `30` = OFF, `31` = ON** |
| 14 | `30` / `31` | preserved or target | **Pre-filter auto-cleaning activity: `30` = idle, `31` = active** |
| 15–20 | `00` in every captured fixture | preserved | Other settings; meaning not established |
| 21 | checksum | recalculated | See 5.4 |

Treat bytes 10–12 and 15–20 as opaque: preserve whatever the fresh status frame contains. Decode bytes 9, 13, and 14 only when the reported firmware is `80.00`; otherwise leave the decoded values unknown.

### 5.2 Command masks

A command clones a fresh validated status frame, sets bytes 1=`41`, 5=`01`, 7=`00`, sets byte 6 to the feature's mask, and writes the target value into exactly one byte. **Select exactly one setting per command**; combined masks were not tested.

| Feature | Byte 6 mask | Byte 7 | Target byte | Values |
| --- | --- | --- | --- | --- |
| Power | `01` | `00` | 8 | `30` OFF, `31` ON |
| Auto mode / manual speed | `02` | `00` | 9 | see 5.3 |
| Smart Search | `20` | `00` | 13 | `30` OFF, `31` ON |
| Start auto cleaning | `40` | `00` | 14 | `31` (start) |

### 5.3 Byte 9 selector values

| Operation | Name | Byte 9 |
| --- | --- | --- |
| Auto | Normal | `42` |
| Auto | Night | `41` |
| Auto | Turbo | `43` |
| Manual | Silent | `30` |
| Manual | Low | `31` |
| Manual | Mid | `32` |
| Manual | High | `33` |
| Manual | Turbo | `34` |

Auto and manual operation share byte 9. Manual Turbo (`34`) is distinct from Auto Turbo (`43`). Selecting a manual speed switches the unit to manual operation; selecting an auto mode switches back. Any other byte-9 value decodes to neither an auto mode nor a manual speed; expose the raw value and do not guess.

### 5.4 Checksum

The checksum is the two's-complement of the sum of bytes 1 through 20, so bytes 1–21 sum to zero modulo 256:

```python
frame[-1] = (-sum(frame[1:-1])) & 0xFF
```

This excludes the initial `fc` and the old checksum. **Do not calculate `0xFC - sum(frame[1:-1])`.** That was the implementation error in the earlier unsuccessful attempts; correcting it was followed by successful power control. The captured ON/OFF status checksums are `fa` and `fb`; the example command checksums are `1b` and `1c`.

### 5.5 Rejection rules

Reject a frame (protocol error) when it has the wrong length, the wrong 6-byte prefix `fc6201351002`, an invalid checksum, or a power byte (8) other than `30` or `31`. Reject any write built from a frame that was not freshly read and validated.

```python
PREFIX = bytes.fromhex("fc6201351002")


def valid_frame_checksum(frame: bytes) -> bool:
    return (sum(frame[1:]) & 0xFF) == 0


def validate_frame(frame: bytes) -> None:
    if len(frame) != 22 or frame[:6] != PREFIX:
        raise ValueError("Unsupported purifier status frame")
    if not valid_frame_checksum(frame):
        raise ValueError("Invalid frame checksum")
    if frame[8] not in (0x30, 0x31):
        raise ValueError("Unknown power encoding")


def build_command(status_frame: bytes, mask: int, index: int, value: int) -> bytes:
    validate_frame(status_frame)
    frame = bytearray(status_frame)
    frame[1] = 0x41
    frame[5] = 0x01
    frame[6] = mask  # 0x01 power, 0x02 mode/speed, 0x20 Smart Search, 0x40 cleaning
    frame[7] = 0x00
    frame[index] = value  # 8, 9, 13, or 14
    frame[-1] = (-sum(frame[1:-1])) & 0xFF
    return bytes(frame)
```

## 6. Writing settings

### 6.1 Generic write algorithm

1. Read fresh status (`<CSV></CSV>`) and confirm the expected device identity (MAC).
2. Validate the general state frame, and require firmware `80.00`.
3. Apply the preconditions in 6.2 and the no-op shortcut.
4. Build the command from that frame (section 5.2), changing exactly one field.
5. Wrap it in inner XML, then encrypt and POST it ([sections 2–3](#2-device-and-transport)):

   ```xml
   <CSV><CODE><VALUE>COMMAND_HEX</VALUE></CODE></CSV>
   ```

6. Treat the decrypted reply only as an acknowledgement of the HTTP exchange. Do **not** use it as confirmation: it can still show the previous state, and it can also already show the target state.
7. Confirm with separate status requests as described in [section 7](#7-confirmation-errors-and-polling). Never resend the command automatically.

Always build commands from current status rather than replaying a stored complete fixture: that preserves unrelated settings of the actual unit.

### 6.2 Per-feature table

| Feature | Mask (byte 6) | Target byte and values | Preconditions | No-op when | Confirmed when readback shows |
| --- | --- | --- | --- | --- | --- |
| Power | `01` | 8: `31` ON, `30` OFF | Firmware `80.00`. Allowed while OFF | Power already equals the request | Byte 8 equals the request |
| Auto mode | `02` | 9: `42` Normal, `41` Night, `43` Turbo | Firmware `80.00`, power ON | Already in that auto mode | Requested auto mode, power unchanged |
| Manual speed | `02` | 9: `30` Silent … `34` Turbo | Firmware `80.00`, power ON | Already at that manual speed | Requested manual speed, power unchanged |
| Smart Search | `20` | 13: `31` ON, `30` OFF | Firmware `80.00`, power ON, recognized byte 13 | Already at the requested value | Requested value; power and byte 9 unchanged |
| Start auto cleaning | `40` | 14: `31` | Firmware `80.00`, power ON, recognized byte 14 | Cleaning already active | Byte 14 = `31`; power and byte 9 unchanged |

Feature notes:

- **Power.** Byte 8 is not `00/01`. A mode/speed/feature command on an OFF device is rejected, not queued.
- **Auto mode and manual speed.** Both use the same selector and mask; only the byte-9 value differs. Power (byte 8) and bytes 10–20 stay unchanged.
- **Smart Search.** Independent of the operating mode and of the cleaning flag. Use explicit ON/OFF; there is no toggle. An unknown byte-13 encoding must not be treated as OFF: send no write.
- **Auto cleaning.** A one-shot action, not a persistent mode. The command confirms that a cycle **started**, not that it completed; the device ends the cycle on its own and byte 14 returns to `30`. No stop, reset, scheduling, or filter-counter operation is known. An unknown byte-14 encoding must not be treated as idle: send no write.

Exact captured status and command frames for every feature are in [section 8](#8-fixtures-appendix).

## 7. Confirmation, errors, and polling

### 7.1 Readback semantics

An HTTP 200 response confirms an HTTP exchange, not completed control. In the successful tests the immediate decrypted command response still contained the **previous** state.

The verified sequence for every write is: fresh identity-checked status → one write → wait about five seconds → separate status request → compare the target byte. The native clients perform up to **three** readbacks, **five seconds** apart, each with a five-second request timeout. With defaults, a write can therefore take up to about **40 seconds** (initial read, write, and three delayed reads), plus any time waiting for another operation.

Defaults are a starting point, not measured hardware guarantees: request timeout 5 s (C# accepts more than 0 and at most 60 s), readback delay 5 s (0–30 s), readback attempts 3 (1–10).

On every readback, in this order:

1. Firmware must equal the firmware seen before the write, or it is a protocol error ("Firmware changed during command readback").
2. For writes other than power, power must equal the pre-write power, or it is a protocol error ("Power changed during command readback").
3. For Smart Search and cleaning, byte 9 must equal the pre-write byte 9, or it is a protocol error ("Operating mode changed during feature readback").
4. If the target condition from the table in 6.2 holds, return that readback state as the confirmed state.

Identity (MAC) is checked on every read, including readbacks. If all attempts are exhausted without confirmation, raise `CommandUnconfirmed`.

### 7.2 Error taxonomy

All implementations use the same codes. `commandMayHaveSucceeded` is `true` on any error raised **after** a write was sent (including `CommandUnconfirmed`); then read fresh status before deciding whether to retry, because the command might already have been applied. A caller that disconnects can also leave the outcome unknown; the next request always reads fresh status.

| Code | Meaning | Raised |
| --- | --- | --- |
| `ConnectionFailed` | TCP/HTTP failure or unreadable response stream | Any exchange |
| `RequestTimedOut` | A device request exceeded the request timeout | Any exchange |
| `ProtocolError` | Bad envelope, padding, XML, frame, checksum, size, unknown required encoding, or state change during readback | Any exchange or guard |
| `IdentityMismatch` | Reported MAC differs from the expected MAC | Any read, including readbacks |
| `UnsupportedFirmware` | Write requested while firmware is not `80.00` | Before a write is sent |
| `DeviceOff` | Mode/speed/Smart Search/cleaning requested while power is OFF | Before a write is sent |
| `CommandUnconfirmed` | Requested setting not seen within the readback attempts | After a write |

Guards run in this order after the first fresh read: firmware → `DeviceOff` (all writes except power) → unknown byte 14 (cleaning) → unknown byte 13 (Smart Search) → no-op shortcut → write. Status reads need no firmware match.

### 7.3 Concurrency, retries, and polling

- Serialize each device's status and write operations with one lock, including the readback phase. Run one client instance per purifier and avoid driving the same device from several clients at once.
- Use explicit desired states for retries, never toggles. After a timeout, read current state before considering a resend.
- Preserve the last confirmed state and its receipt time; report **unavailable** on failure, never an assumed OFF or assumed success.
- A 30-second idle polling interval is a proposed default. Maximum request rate and long-term reliability were not characterized.

## 8. Fixtures appendix

All frames are 22 bytes (44 hex characters) captured from, or built for, the tested unit (firmware `80.00`) and are directly usable as test vectors. All command fixtures below equal `build_command` applied to the **ON/Normal status frame** `fc6201351002000031424543003130000000000000fa` with the stated change.

### 8.1 Status frames (general state, message type `62`)

| Name | Frame | Decoded |
| --- | --- | --- |
| ON, Auto Normal, Smart Search ON, cleaning idle (also named "ON status", "Normal status", "Idle status", "Smart Search ON status") | `fc6201351002000031424543003130000000000000fa` | power ON, byte 9=`42`, byte 13=`31`, byte 14=`30` |
| OFF | `fc6201351002000030424543003130000000000000fb` | power OFF |
| Auto Night | `fc6201351002000031414543003130000000000000fb` | byte 9=`41` |
| Auto Turbo | `fc6201351002000031434543003130000000000000f9` | byte 9=`43` |
| Manual Silent | `fc62013510020000313045430031300000000000000c` | byte 9=`30` |
| Manual Low | `fc62013510020000313145430031300000000000000b` | byte 9=`31` |
| Manual Mid | `fc62013510020000313245430031300000000000000a` | byte 9=`32` |
| Manual High | `fc620135100200003133454300313000000000000009` | byte 9=`33` |
| Manual Turbo | `fc620135100200003134454300313000000000000008` | byte 9=`34` |
| Smart Search OFF | `fc6201351002000031424543003030000000000000fb` | byte 13=`30` |
| Auto cleaning active ("Active readback") | `fc6201351002000031424543003131000000000000f9` | byte 14=`31` |

### 8.2 Command frames (message type `41`)

| Name | Change applied to the ON/Normal frame | Frame |
| --- | --- | --- |
| Power ON | byte 6=`01`, byte 8=`31` | `fc41013510010100314245430031300000000000001b` |
| Power OFF | byte 6=`01`, byte 8=`30` | `fc41013510010100304245430031300000000000001c` |
| Auto Normal | byte 6=`02`, byte 9=`42` | `fc41013510010200314245430031300000000000001a` |
| Auto Night | byte 6=`02`, byte 9=`41` | `fc41013510010200314145430031300000000000001b` |
| Auto Turbo | byte 6=`02`, byte 9=`43` | `fc410135100102003143454300313000000000000019` |
| Manual Silent | byte 6=`02`, byte 9=`30` | `fc41013510010200313045430031300000000000002c` |
| Manual Low | byte 6=`02`, byte 9=`31` | `fc41013510010200313145430031300000000000002b` |
| Manual Mid | byte 6=`02`, byte 9=`32` | `fc41013510010200313245430031300000000000002a` |
| Manual High | byte 6=`02`, byte 9=`33` | `fc410135100102003133454300313000000000000029` |
| Manual Turbo | byte 6=`02`, byte 9=`34` | `fc410135100102003134454300313000000000000028` |
| Smart Search ON | byte 6=`20`, byte 13=`31` | `fc4101351001200031424543003130000000000000fc` |
| Smart Search OFF | byte 6=`20`, byte 13=`30` | `fc4101351001200031424543003030000000000000fd` |
| Start auto cleaning | byte 6=`40`, byte 14=`31` | `fc4101351001400031424543003131000000000000db` |

Plaintext requests that were used successfully for power:

```xml
<CSV><CODE><VALUE>fc41013510010100314245430031300000000000001b</VALUE></CODE></CSV>
```

```xml
<CSV><CODE><VALUE>fc41013510010100304245430031300000000000001c</VALUE></CODE></CSV>
```

### 8.3 Raw mode-field probe sequence (before the labels were known)

```text
Before:   fc6201351002000031424543003130000000000000fa
Command:  fc410135100102003143454300313000000000000019
Readback: fc6201351002000031434543003130000000000000f9
Restore:  fc41013510010200314245430031300000000000001a
Readback: fc6201351002000031424543003130000000000000fa
```

### 8.4 Profile frames (`PROFILECODE/VALUE`, not decoded)

```text
fc7b013510c9040018000f010000000000000000004a
fc7b013510cd4d0000000000005201000000070000cb
```

These use a different prefix (`fc7b…`) and must never be selected as the general state frame.

### 8.5 Checks a test suite should reproduce

- Parsing the ON and OFF status frames yields power true and false.
- Checksums: status `fa` / `fb`; power command `1b` / `1c`; the other checksums are in the tables above.
- Every command frame in 8.2 equals the build from the ON/Normal frame with the stated change (the Power OFF command also equals the build from the OFF frame with byte 8 preserved as `30`).
- Building from a frame with altered unrelated bytes and a corrected checksum preserves those bytes.

## 9. Implementations

The repository contains three clients of this protocol. They share the fixtures above and the error taxonomy in section 7.

### 9.1 PowerShell reference client

`ma-ew85s-reference.ps1` is a standalone reference implementation. It uses **PowerShell 7+** and built-in .NET libraries; the original live tests ran on PowerShell `7.6.5`. It is synchronous and is not a Home Assistant component; a Python integration implements the protocol with asynchronous I/O rather than launching PowerShell.

```powershell
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action Status
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action On
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action Off
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SetAutoMode -AutoMode Normal    # or Night, Turbo
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SetManualSpeed -ManualSpeed Low # Silent, Low, Mid, High, Turbo
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SetSmartSearch -SmartSearch On  # or Off
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action StartAutoCleaning
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SelfTest                         # offline, never contacts the purifier
```

It defaults to this installation's IP and expected MAC. Override those only for a deliberately selected compatible device:

```powershell
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action Status -DeviceHost 192.168.1.53 -ExpectedMac 70:61:be:5b:6d:1e
```

Run one invocation at a time. `Status` and write results include `OperatingMode`, `AutoMode`, `ManualSpeed`, `AutoModeRaw`, `IsAutoCleaning`, and `IsSmartSearchEnabled`. Writes require power ON (except power itself) and firmware `80.00`, skip a write when the setting already matches, and confirm with up to three separate readbacks five seconds apart. Unconfirmed commands are not resent.

**Auto-mode profile (PowerShell only).** The PowerShell client selects auto modes through a learned profile, `ma-ew85s-auto-modes.json`, bound to the expected MAC and reported firmware. This installation's profile was saved from the three owner-labeled captures (Normal=`42`, Night=`41`, Turbo=`43`); the file is ignored by Git, so a fresh checkout needs its own. `AutoMode` is null before learning, for values absent from the profile, when manual operation is selected, or when the saved profile is invalid; `AutoModeRaw` always carries the raw selector byte. A mismatched or missing profile prevents auto-mode writes. Relearn after changing the device or firmware. Manual speeds, Smart Search, and cleaning do not use the profile. The native clients do not use it either; they use the confirmed mappings in 5.3.

To create or replace the profile, run the one-time interactive action in a PowerShell 7 terminal with the purifier ON:

```powershell
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action LearnAutoModes
```

At each prompt, use the purifier's physical **Auto** button to select the requested **Normal**, **Night**, or **Turbo** lamp, then press Enter. Do not use the Manual fan-speed button. The script waits five seconds before each capture; type `q` to cancel. Learning sends only status requests, never mode or power commands, and leaves the device in the last mode you selected (Turbo after all three prompts). Three distinct validated captures are saved beside the script; an incomplete, duplicate, or cancelled run does not overwrite an existing profile. `-ModeProfilePath <path>` selects another location (use the same path for learning, status, and mode selection; the parent directory must already exist).

### 9.2 ASP.NET Core API

`src/AirPurifier.Core` is the native asynchronous C# protocol client, `src/AirPurifier.Api` the ASP.NET Core 10 API, and `tests/AirPurifier.Tests` the offline protocol and HTTP integration tests. The API does not launch PowerShell or depend on its profile, and rejects writes on unsupported firmware.

Run `dotnet run --project src/AirPurifier.Api` from the repository root (default `http://localhost:5080`). Endpoints, request bodies, error statuses, configuration, and API-key access are documented in [README.md](README.md):

| Method | Path | Body |
| --- | --- | --- |
| GET | `/api/purifier/status` | — |
| PUT | `/api/purifier/power` | `{"on":true}` |
| PUT | `/api/purifier/auto-mode` | `{"mode":"normal"}` (`night`, `turbo`) |
| PUT | `/api/purifier/manual-speed` | `{"speed":"low"}` (`silent`, `low`, `mid`, `high`, `turbo`) |
| PUT | `/api/purifier/smart-search` | `{"enabled":true}` |
| POST | `/api/purifier/auto-cleaning` | — |

Status includes `operatingMode`, `autoMode`, `manualSpeed`, `autoModeRaw`, nullable `isAutoCleaning` and `isSmartSearchEnabled` (`false` for `30`, `true` for `31`, null for unknown encodings or unsupported firmware), `generalFrameHex`, and the adapter's raw `rssiReported`, `adapterStatusRaw`, `cloudConnectionRaw`, and `echonetRaw`. Failures use `application/problem+json` with `code`, `confirmed: false`, and `commandMayHaveSucceeded`.

The API is not part of the device protocol and is not required by the Home Assistant integration.

### 9.3 Home Assistant integration (design)

Domain `ma_ew85s_local`, a HACS custom repository under `custom_components/ma_ew85s_local/`, `iot_class: local_polling`. It talks to the purifier directly through a native asynchronous Python port of the C# client placed in the subpackage `protocol/`, which imports nothing from Home Assistant. It does not depend on the ASP.NET Core API or PowerShell. Recheck current Home Assistant developer documentation when implementing; the links are in [section 11.3](#113-references).

**Entities**

| Entity | Platform | Source | Behavior |
| --- | --- | --- | --- |
| Purifier | `fan` | bytes 8, 9 | `TURN_ON`/`TURN_OFF`; `SET_SPEED` with five manual speeds mapped to 20/40/60/80/100 % (`ordered_list_item_to_percentage`, `speed_count=5`); `PRESET_MODE` `auto_normal`, `auto_night`, `auto_turbo`. In auto mode `percentage` is `None` and `preset_mode` is set; in manual mode the reverse |
| Smart Search | `switch` | byte 13 | Explicit ON/OFF; unknown encoding → unknown state |
| Start auto cleaning | `button` | mask `40` | One-shot start; no-op if already active |
| Auto cleaning | `binary_sensor` (`running`) | byte 14 | Unknown encoding → state unknown |
| Wi-Fi signal | `sensor` (`signal_strength`, diagnostic, disabled by default) | `RSSI` | dBm |

Mode, speed, Smart Search, and cleaning writes require power ON and raise `ServiceValidationError` when OFF. `fan.turn_on` with a percentage or preset powers on first, then applies the setting; what the unit resumes after power-on is not verified, so the fan reports whatever the readback says. Manual louvre, PM2.5/air quality, filter life, discovery (DHCP/zeroconf), ECHONET/cloud/Wi-Fi/reset operations, and raw command services are not offered.

**Device registry:** model `MA-EW85S-E`, manufacturer Mitsubishi Electric, `sw_version` from `APP_VER`, connection `(mac, <MAC>)`.

**Config flow**

- User step: ask for the host → read status → require firmware `80.00` → set the unique ID to the MAC → abort if already configured. Title `MA-EW85S-E (<MAC suffix>)`.
- Reconfigure step: validate the new host against the stored MAC (abort on mismatch) and update the entry, so an IP change does not require re-adding the device.
- Errors: connection failure, timeout, protocol error, or identity mismatch → `cannot_connect`; `UnsupportedFirmware` → `unsupported_firmware`.

**Coordinator and state**

- One `DataUpdateCoordinator` and one client (with one `asyncio.Lock`) per device, using Home Assistant's shared `aiohttp` session; no blocking I/O in the event loop and no I/O in entity properties.
- Poll every 30 seconds (proposed default). Poll failures raise `UpdateFailed`, so entities become **unavailable**, never a false OFF.
- Writes use the sequence in sections 6–7 (fresh identity-checked status, one write, up to three separate readbacks five seconds apart, never auto-resent). The confirmed returned state is published with `async_set_updated_data`. An unconfirmed command raises `HomeAssistantError` and triggers a coordinator refresh.

**Error mapping**

| Protocol code | During polling | On a write / service call | Config flow |
| --- | --- | --- | --- |
| `ConnectionFailed`, `RequestTimedOut`, `ProtocolError`, `IdentityMismatch` | `UpdateFailed` | `HomeAssistantError` | `cannot_connect` |
| `CommandUnconfirmed` | — | `HomeAssistantError`, then refresh | — |
| `DeviceOff` | — | `ServiceValidationError` | — |
| `UnsupportedFirmware` | — | `HomeAssistantError` | `unsupported_firmware` |

**Diagnostics:** partially redact host, MAC, and the frame hex. Default setup never alters cloud, ECHONET, or Wi-Fi settings; unloading cleans up timers and sessions.

Planned package layout:

```text
custom_components/ma_ew85s_local/
  __init__.py  manifest.json  const.py  config_flow.py  coordinator.py
  entity.py  fan.py  switch.py  button.py  binary_sensor.py  sensor.py
  diagnostics.py  strings.json  translations/en.json  icons.json
  protocol/  __init__.py  client.py  codec.py  frame.py  models.py  errors.py
```

The Python client is planned as `PurifierClient(session, host, expected_mac=None, request_timeout=5.0, readback_delay=5.0, readback_attempts=3)` with `async_get_status`, `async_set_power`, `async_set_auto_mode`, `async_set_manual_speed`, `async_start_auto_cleaning`, and `async_set_smart_search`, raising `PurifierError(code, command_may_have_succeeded)` with the codes in 7.2. These file names and signatures are a design; this layout is not yet an existing package.

## 10. Diagnostics, ECHONET, reachability, and security

### Optional browser diagnostics

`GET /unitinfo` returned model, firmware, and communication information; the root page `/` is a network-configuration form. A browser login was verified on this firmware (it is the adapter's diagnostic login, not a secret to rely on, and must not be copied into integration configuration, logs, or diagnostics output):

```text
Username: admin
Password: me1debug@0567
```

These are optional diagnostics; `/smart` status and write requests did not need HTTP credentials. Do not make integration setup depend on `/unitinfo` or the diagnostic login: other firmware may restrict them. Keep network setup, debug toggles, factory reset, firmware update, and arbitrary raw commands outside integration scope.

### ECHONET

An ECHONET setting was exposed and briefly enabled during investigation. The attempted UDP queries did not produce a usable response, and the setting was restored to OFF. Working control uses `/smart` with ECHONET OFF. Enabling ECHONET is not a prerequisite for any verified feature.

### Temporary loss of reachability

During testing the device temporarily stopped responding at its IP. The cause was not established; it became reachable again at `192.168.1.53`, after which the corrected power commands succeeded.

Diagnostics used ping, HTTP port 80, and local address-resolution checks. A steady Wi-Fi light did not by itself establish reachability from the computer. A client should report a connection failure separately from a protocol/decryption error and allow the configured host/IP to be updated. Routine recovery must not automatically reset or reconfigure the purifier.

### Access boundary

The control endpoint accepts requests without HTTP authentication and uses a shared, publicly known application key. Treat it as a trusted-LAN interface. A separate API service should bind to loopback by default and require appropriate access controls before being exposed to other hosts. Do not expose the purifier directly to the internet.

## 11. Evidence log and references

### 11.1 Evidence basis

The power values, frame construction, packet examples, and all feature results in this document come from direct tests on the owner's unit. They are not inferred from a product listing or advertised air-conditioner compatibility.

### 11.2 Dated live verification

All entries are 21 October 2026. Times are UTC. Each transition was confirmed by a separate status request, not by the command reply.

| Feature | Evidence |
| --- | --- |
| Power | Reading status and switching ON → OFF → ON, each confirmed by a separate status request about five seconds after the command. Left **ON** |
| Auto modes | Owner-labeled panel captures: Normal `19:01:09Z`, Night `19:02:28Z`, Turbo `19:03:18Z`. The reference client then switched Turbo → Normal → Night → Turbo (command readbacks Normal `19:03:54Z`, Night `19:03:59Z`, Turbo `19:04:04Z`) with the same fresh-status/mode-only-command/readback procedure; power and bytes 10–20 unchanged. The raw mode-field probe sequence is in 8.3. Left **ON in Turbo**. A later live API check read status, confirmed a power no-op, and switched Normal → Night → Normal with separate readbacks, leaving **ON in Normal**; no power cycle |
| Manual speeds | Owner-labeled captures using the Manual button: Silent `19:27:35Z`, Low `19:34:04Z`, Mid `19:37:43Z`, High `19:38:56Z`, Turbo `19:40:03Z`. PowerShell selected Silent → Low → Mid → High → Turbo, each confirmed between `19:40:35Z` and `19:40:56Z`, then Auto Normal at `19:41:01Z`. The native API independently selected all five between about `19:41:32Z` and `19:41:53Z`, then confirmed **ON in Auto Normal** at about `19:41:58Z`, as requested by the owner. Power and bytes 10–20 unchanged; no power cycle |
| Auto cleaning | Owner-labeled active cycle captured at `19:46:38Z`: relative to idle Auto Normal only byte 14 changed `30` → `31` (plus checksum). A read-only observation saw the cycle finish and byte 14 return to `30` at `19:49:08Z`. The reference client sent the start command once and confirmed active cleaning through a separate status request at `19:49:52Z`; power and bytes 9–13 and 15–20 unchanged. After that cycle the native API independently changed idle → active and confirmed **ON, Auto Normal, cleaning active** at `19:52:44Z`. PowerShell `StartAutoCleaning` then returned the active state without a second start. The API server was stopped without interrupting the cycle |
| Smart Search | The owner confirmed Smart Search was ON during the earlier captures, then switched it OFF. A capture at `19:58:54Z` returned the Smart Search OFF status frame; relative to idle Auto Normal only byte 13 changed `31` → `30` (plus checksum). The probe confirmed ON at `20:00:07Z` and OFF at `20:00:12Z`; PowerShell independently passed ON/OFF live checks; the native API confirmed ON at `20:04:16Z` and OFF at `20:04:21Z`. Power, mode, and other fields unchanged. Final state **Smart Search OFF, power ON, Auto Normal** |

Offline test history (informational only; current counts are reported by the test runs): the C# suite grew from 55 to 82, 93, and finally 109 passing tests across the API, manual-speed, cleaning, and Smart Search work. PowerShell's expanded self-test passes. C# and PowerShell tests cover the exact command fixtures, field preservation, delayed readback, no-op, bounded timeout without resend, rejection of OFF/unknown/unsupported states, changed power/mode during readback, identity and firmware mismatches, HTTP validation, API keys, and OpenAPI authentication metadata.

### 11.3 References

Related primary implementation sources consulted during investigation:

- [PyMitsubishi communication implementation](https://github.com/pymitsubishi/pymitsubishi/blob/main/pymitsubishi/mitsubishi_api.py): related Mitsubishi transport/encryption and diagnostic-login implementation. Its supported air-conditioner hardware is different from this purifier.
- [PyMitsubishi packet parser and writer](https://github.com/pymitsubishi/pymitsubishi/blob/main/pymitsubishi/mitsubishi_parser.py): related framing and checksum implementation. Do not reuse its air-conditioner state mappings for the purifier.
- [Original Mitsubishi Wi-Fi adapter investigation](https://blog.dest-unreach.be/2022/06/04/mitsubishi-wifi-adapter/): background on the related adapter interface.

Home Assistant developer documentation (links return HTTP 200 as of 21 October 2026; follow current versions when implementing):

- [Fan entity](https://developers.home-assistant.io/docs/core/entity/fan/)
- [Fetching data and `DataUpdateCoordinator`](https://developers.home-assistant.io/docs/integration_fetching_data/)
- [Config flow, unique IDs, and reconfigure](https://developers.home-assistant.io/docs/config_entries_config_flow_handler/)
- [Integration manifest](https://developers.home-assistant.io/docs/creating_integration_manifest/)

## 12. Acceptance test checklist

Before live testing, demonstrate all of the following offline, using the fixtures in section 8. Where an item applies to every feature, cover each of power, auto mode, manual speed, Smart Search, and cleaning.

**Codec and frame**

1. Parse the captured ON/OFF frames as true/false, respectively, and decode every status frame in 8.1 (mode, speed, Smart Search, cleaning).
2. Validate the checksums of all captured status frames and all command frames.
3. Build every exact command frame in 8.2 from the ON/Normal frame (and the exact power commands from the opposite-power frame).
4. Preserve all bytes other than the written target (and bytes 1, 5–7 and the checksum) when constructing a write from any validated frame, including a fixture with altered unrelated settings and a corrected checksum.
5. Reject invalid lengths, a changed checksum, an air-conditioner `0130` frame, a wrong prefix, and an unknown power byte. Do not select a `PROFILECODE/VALUE` frame.
6. Round-trip UTF-8 request XML with random IVs, including block-aligned input and ISO/IEC 7816-4 padding. Fixed IVs may be used for deterministic unit tests only.
7. Reject malformed encrypted envelopes, Base64, lengths, padding, and malformed or unexpected response XML, including oversized responses.
8. Leave decoded values unknown for unrecognized byte 9, 13, or 14 values and for non-`80.00` firmware.

**Client behavior**

9. Simulate an initial write response that still shows the previous state, and one that already shows the target; confirm only after the separate delayed readback.
10. Return a no-op without writing when the setting already matches (power, mode, speed, Smart Search, cleaning).
11. Reject writes while OFF (all except power), on unsupported firmware, and for unknown byte 13 (Smart Search) or byte 14 (cleaning), without sending a command.
12. Simulate timeouts, stale state, and identity mismatches before and after writing, without displaying false success or writing to a replacement device. After a sent write, errors report `command_may_have_succeeded` and the command is never resent.
13. Handle firmware, power, or mode changing during readback as a protocol error.
14. Exhaust the readback attempts and report `CommandUnconfirmed`.
15. Serialize concurrent operations on one device.

**Home Assistant integration**

16. Config flow: success, unreachable host, protocol error, unsupported firmware, duplicate abort by MAC, and reconfigure with a MAC mismatch.
17. Fan: percentage/preset mapping (20/40/60/80/100 and the three presets), `percentage` is `None` in auto mode and `preset_mode` is `None` in manual mode, power-on-then-set, and `ServiceValidationError` when OFF.
18. Switch, button, binary sensor, and sensor states and service calls, with unknown cleaning/Smart Search encodings reported as unknown.
19. All entities become unavailable when the coordinator fails; setup, unload, and reload are clean.
20. No test or default code path alters cloud, ECHONET, Wi-Fi, or reset settings, and no live-device commands are sent. Do not run live power-cycle tests until the offline checks are ready for review.
