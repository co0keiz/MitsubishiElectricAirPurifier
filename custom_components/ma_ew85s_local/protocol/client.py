"""Serialized native async client. Writes are never automatically resent."""
import asyncio
from collections.abc import Awaitable, Callable
from datetime import UTC, datetime

import aiohttp
from yarl import URL

from .codec import MAX_XML_LENGTH, decrypt, encrypt
from .errors import PurifierError, PurifierErrorCode as Code, protocol_error
from .frame import VERIFIED_FIRMWARE, build_command, parse_state
from .models import AutoMode, ManualSpeed, PurifierState


class PurifierClient:
    def __init__(self, session: aiohttp.ClientSession, host: str, *, expected_mac=None,
                 request_timeout=5.0, readback_delay=5.0, readback_attempts=3,
                 sleep: Callable[[float], Awaitable[None]] = asyncio.sleep,
                 clock: Callable[[], datetime] = lambda: datetime.now(UTC)):
        # Accept host[:port] for local simulators; never accept paths or credentials.
        url = URL(f"http://{host}")
        if (not url.host or url.user is not None or url.password is not None
                or url.path not in ("", "/") or url.query_string or url.fragment
                or any(c.isspace() for c in host)):
            raise ValueError("Enter a host name or IP address.")
        if not 0 < request_timeout <= 60 or not 0 <= readback_delay <= 30:
            raise ValueError("Invalid timeout or readback delay.")
        if not isinstance(readback_attempts, int) or not 1 <= readback_attempts <= 10:
            raise ValueError("Invalid readback attempt count.")
        self.session, self.host = session, host
        self.expected_mac = expected_mac
        self.endpoint = url.with_path("/smart")
        self.request_timeout = request_timeout
        self.readback_delay, self.readback_attempts = readback_delay, readback_attempts
        self.sleep, self.clock = sleep, clock
        self._lock = asyncio.Lock()

    async def _exchange(self, plaintext: str) -> PurifierState:
        try:
            async with asyncio.timeout(self.request_timeout):
                async with self.session.post(
                    self.endpoint, data=encrypt(plaintext).encode("utf-8"),
                    headers={"Content-Type": "text/plain; charset=utf-8",
                             "Connection": "close", "User-Agent": "KirigamineRemote/5.1.0"},
                    allow_redirects=False, proxy="",
                ) as response:
                    if not 200 <= response.status < 300:
                        raise PurifierError(Code.CONNECTION_FAILED, "Purifier HTTP exchange failed.")
                    body = bytearray()
                    async for chunk in response.content.iter_chunked(4096):
                        body.extend(chunk)
                        if len(body) > MAX_XML_LENGTH:
                            raise protocol_error("Response exceeded expected size.")
                    xml = decrypt(body.decode("utf-8", errors="strict"))
                    return parse_state(xml, self.clock(), self.expected_mac)
        except TimeoutError as err:
            raise PurifierError(Code.REQUEST_TIMED_OUT, "Purifier request timed out.") from err
        except (aiohttp.ClientError, OSError) as err:
            raise PurifierError(Code.CONNECTION_FAILED, "Could not communicate with purifier.") from err
        except UnicodeError as err:
            raise protocol_error("Invalid response encoding.") from err

    async def _read(self) -> PurifierState:
        return await self._exchange("<CSV></CSV>")

    async def async_get_status(self) -> PurifierState:
        async with self._lock:
            return await self._read()

    async def async_set_power(self, on: bool) -> PurifierState:
        return await self._set(on=on)

    async def async_set_auto_mode(self, mode: AutoMode) -> PurifierState:
        return await self._set(mode=AutoMode(mode))

    async def async_set_manual_speed(self, speed: ManualSpeed) -> PurifierState:
        return await self._set(manual_speed=ManualSpeed(speed))

    async def async_start_auto_cleaning(self) -> PurifierState:
        return await self._set(start_auto_cleaning=True)

    async def async_set_smart_search(self, enabled: bool) -> PurifierState:
        return await self._set(smart_search_enabled=enabled)

    @staticmethod
    def _matches(state: PurifierState, *, on=None, mode=None, manual_speed=None,
                 start_auto_cleaning=False, smart_search_enabled=None) -> bool:
        return ((on is not None and state.is_on == on)
                or (mode is not None and state.auto_mode == mode)
                or (manual_speed is not None and state.manual_speed == manual_speed)
                or (start_auto_cleaning and state.is_auto_cleaning is True)
                or (smart_search_enabled is not None
                    and state.is_smart_search_enabled == smart_search_enabled))

    async def _set(self, **setting) -> PurifierState:
        async with self._lock:
            write_attempted = False
            try:
                before = await self._read()
                if before.firmware != VERIFIED_FIRMWARE:
                    raise PurifierError(Code.UNSUPPORTED_FIRMWARE, "Writes require firmware 80.00.")
                if "on" not in setting and not before.is_on:
                    raise PurifierError(Code.DEVICE_OFF, "Turn the purifier on first.")
                if setting.get("start_auto_cleaning") and before.is_auto_cleaning is None:
                    raise protocol_error("Current auto-cleaning state is unknown.")
                if "smart_search_enabled" in setting and before.is_smart_search_enabled is None:
                    raise protocol_error("Current Smart Search state is unknown.")
                if self._matches(before, **setting):
                    return before
                command = build_command(bytes.fromhex(before.frame_hex), **setting)
                write_attempted = True
                await self._exchange(f"<CSV><CODE><VALUE>{command.hex()}</VALUE></CODE></CSV>")
                for _ in range(self.readback_attempts):
                    await self.sleep(self.readback_delay)
                    after = await self._read()
                    if after.firmware != before.firmware:
                        raise protocol_error("Firmware changed during command readback.")
                    if "on" not in setting and after.is_on != before.is_on:
                        raise protocol_error("Power changed during command readback.")
                    if (setting.get("start_auto_cleaning") or "smart_search_enabled" in setting):
                        if after.frame_hex[18:20] != before.frame_hex[18:20]:
                            raise protocol_error("Operating mode changed during feature readback.")
                    if self._matches(after, **setting):
                        return after
                raise PurifierError(Code.COMMAND_UNCONFIRMED, "Requested setting not confirmed by readback.")
            except PurifierError as err:
                if write_attempted:
                    raise PurifierError(err.code, f"{err} Command may have succeeded; read status before retrying.",
                                        command_may_have_succeeded=True) from err
                raise
