"""Encrypted loopback-only purifier, with optional scripted exchange failures."""
import asyncio
from xml.etree import ElementTree as ET

from aiohttp import web

from custom_components.ma_ew85s_local.protocol.codec import decrypt, encrypt
from .fixtures import MAC, NORMAL, snapshot


class FakeDevice:
    def __init__(self):
        self.frame = NORMAL
        self.mac = MAC
        self.firmware = "80.00"
        self.requests = []
        self.writes = []
        self.frames = []
        self.respond = None
        self.apply_writes = True
        self.ack_target = False
        self.stale_readbacks = 0
        self._pending = None
        self.fail = False

    async def handle(self, request):
        assert request.remote in ("127.0.0.1", "::1")
        assert request.headers["User-Agent"] == "KirigamineRemote/5.1.0"
        assert request.headers["Connection"].lower() == "close"
        payload = decrypt(await request.text())
        root = ET.fromstring(payload)
        assert root.tag == "CSV"
        assert all(child.tag == "CODE" for child in root)
        self.requests.append(payload)
        index = len(self.requests) - 1
        value = root.findtext("CODE/VALUE")
        if value:
            command = bytes.fromhex(value)
            assert command[:6] == bytes.fromhex("fc4101351001")
            assert sum(command[1:]) % 256 == 0
            assert command[7] == 0
            target_index = {1: 8, 2: 9, 0x20: 13, 0x40: 14}[command[6]]
            source = bytes.fromhex(self.frame)
            assert all(command[i] == source[i] for i in range(8, 21) if i != target_index)
            self.writes.append(command)
        if self.fail:
            return web.Response(status=503)
        if self.respond:
            result = await self.respond(payload, index)
            if isinstance(result, web.StreamResponse):
                return result
            return web.Response(text=encrypt(result))
        if self.frames:
            self.frame = self.frames[min(index, len(self.frames) - 1)]
        elif value and self.apply_writes:
            data = bytearray.fromhex(self.frame)
            data[target_index] = command[target_index]
            data[-1] = (-sum(data[1:-1])) & 255
            self._pending = data.hex()
            if self.ack_target:
                self.frame, self._pending = self._pending, None
        elif not value and self._pending:
            if self.stale_readbacks:
                self.stale_readbacks -= 1
            else:
                self.frame, self._pending = self._pending, None
        return web.Response(text=encrypt(snapshot(self.frame, self.mac, self.firmware)))


async def instant_sleep(_delay):
    await asyncio.sleep(0)
