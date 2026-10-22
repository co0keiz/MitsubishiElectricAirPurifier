# Async Python protocol client

This package implements local MA-EW85S-E communication using `aiohttp` and `cryptography`. It has no Home Assistant imports. Python **3.13+** is required; the package is currently distributed inside the custom integration, with no separate PyPI release.

Run examples from the repository root, or install the repository's Python project with `python -m pip install .`. That installs the Python packages and dependencies; install the HA custom integration using its [own instructions](../README.md).

## Read status

Replace the illustrative host and MAC with your device's values. This example performs a device status read when executed.

```python
import asyncio

import aiohttp

from custom_components.ma_ew85s_local.protocol import PurifierClient


async def main():
    async with aiohttp.ClientSession(trust_env=False) as session:
        client = PurifierClient(
            session,
            "192.0.2.10",
            expected_mac="00:11:22:33:44:55",
        )
        state = await client.async_get_status()
        print(state.firmware, state.is_on, state.auto_mode, state.manual_speed)


asyncio.run(main())
```

The caller owns the session and closes it. Home Assistant supplies its shared session instead; the integration does not close it. Requests bypass proxy settings and do not follow redirects.

`expected_mac=None` permits reading an unknown device during setup. Bind subsequent control to the learned MAC; an expected identity is checked on every response, including command replies and readbacks.

## Operations

All methods are async and return an immutable `PurifierState` on success.

| Method | Argument | Behavior |
| --- | --- | --- |
| `async_get_status()` | None | Read fresh state |
| `async_set_power(on)` | Boolean | Set power; allowed while OFF |
| `async_set_auto_mode(mode)` | `AutoMode.NORMAL`, `.NIGHT`, `.TURBO` | Select automatic operation |
| `async_set_manual_speed(speed)` | `ManualSpeed.SILENT`, `.LOW`, `.MID`, `.HIGH`, `.TURBO` | Select manual operation |
| `async_set_smart_search(enabled)` | Boolean | Set the explicit Smart Search state |
| `async_start_auto_cleaning()` | None | Start cleaning, or return its state if already active |

Writes require firmware `80.00`. All setting methods except power require power ON. Smart Search and cleaning reject an unknown current flag. A matching requested setting is a no-op after the fresh read.

Each client has one `asyncio.Lock`, held across the entire operation including readback. Reuse one instance per purifier. Separate instances do not share a lock.

## State and errors

`PurifierState` contains `mac`, `firmware`, `is_on`, `auto_mode`, `manual_speed`, `is_auto_cleaning`, `is_smart_search_enabled`, `rssi`, `observed_at`, and `frame_hex`.

`observed_at` comes from the client's receipt clock, not the adapter's timestamp. Unknown selectors leave both mode fields `None`; unknown feature encodings leave their flags `None`. Unsupported firmware leaves the decoded mode and feature fields unknown. Missing or invalid RSSI becomes `None`. Invalid frames or power encodings raise an error rather than producing an OFF state.

Failures raise `PurifierError` with a `PurifierErrorCode`:

- `CONNECTION_FAILED` or `REQUEST_TIMED_OUT`: the HTTP exchange failed or exceeded its timeout.
- `PROTOCOL_ERROR`: envelope, XML, frame, size, encoding, required-state, or readback-preservation validation failed.
- `IDENTITY_MISMATCH`: the response MAC differs from the expected device.
- `UNSUPPORTED_FIRMWARE`: a write was requested on unsupported firmware.
- `DEVICE_OFF`: the requested setting requires power ON.
- `COMMAND_UNCONFIRMED`: the readbacks did not confirm the requested state.

Check `error.command_may_have_succeeded`. When true, read current state before choosing whether to retry. The client sends a command only once; the bounded readback loop repeats status reads. Cancellation propagates as `asyncio.CancelledError`; cancelling after a write can also leave its outcome uncertain.

## Timing and modules

Constructor options are keyword-only: `expected_mac=None`, `request_timeout=5.0` seconds, `readback_delay=5.0` seconds, `readback_attempts=3`, plus injectable `sleep` and `clock` functions. Bounds are `(0, 60]` seconds for the timeout, `[0, 30]` seconds for the delay, and 1–10 readbacks. The host is a name or IP; an explicit port is also accepted for simulators. URLs, paths, credentials, and query strings are rejected.

| Module | Responsibility |
| --- | --- |
| `client.py` | HTTP transport, serialization, guards, and confirmation |
| `codec.py` | AES-CBC, padding, and bounded XML parsing |
| `frame.py` | Frame validation, decoding, and command building |
| `models.py` | Enums and immutable state |
| `errors.py` | Error codes and exception contract |

Use the [specification](../../../MA-EW85S-local-control.md) for protocol details and the [development guide](../../../docs/development.md) for offline tests. This Python implementation has simulator verification; live evidence belongs to the C#/PowerShell implementations documented in the specification.
