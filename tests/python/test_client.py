import asyncio

import pytest
from aiohttp import ClientSession
from aiohttp import web

from custom_components.ma_ew85s_local.protocol import AutoMode, ManualSpeed, PurifierClient, PurifierError, PurifierErrorCode as Code
from .fixtures import *

OPERATIONS = [
    ("async_set_power", (False,), NORMAL, OFF),
    ("async_set_power", (True,), OFF, NORMAL),
    *[("async_set_auto_mode", (m,), MANUAL_TURBO, f) for m, f in AUTO_FRAMES.items()],
    *[("async_set_manual_speed", (m,), TURBO, f) for m, f in MANUAL_FRAMES.items()],
    ("async_set_smart_search", (False,), NORMAL, SEARCH_OFF),
    ("async_set_smart_search", (True,), SEARCH_OFF, NORMAL),
    ("async_start_auto_cleaning", (), NORMAL, CLEANING),
]


@pytest.mark.parametrize("method,args,before,after", OPERATIONS)
@pytest.mark.parametrize("ack_target", [False, True])
async def test_confirm_all_operations_and_noop(client, device, method, args, before, after, ack_target):
    device.frame = before
    device.ack_target = ack_target
    delays = []
    async def sleep(delay):
        delays.append(delay)
    client.sleep = sleep
    result = await getattr(client, method)(*args)
    assert result.frame_hex == after
    assert result.observed_at == NOW
    assert len(device.requests) == 3
    assert len(device.writes) == 1
    assert device.requests[-1] == "<CSV></CSV>"
    assert delays == [5.0]
    assert await getattr(client, method)(*args) == result
    assert len(device.requests) == 4
    assert len(device.writes) == 1


@pytest.mark.parametrize("method,args,before,after", OPERATIONS)
async def test_bounded_unconfirmed_never_resends(client, device, method, args, before, after):
    device.frame, device.apply_writes = before, False
    with pytest.raises(PurifierError) as error:
        await getattr(client, method)(*args)
    assert error.value.code == Code.COMMAND_UNCONFIRMED
    assert error.value.command_may_have_succeeded
    assert len(device.requests) == 5
    assert len(device.writes) == 1
    assert (await client.async_get_status()).frame_hex == before


async def test_stale_readback_and_natural_cleaning_completion(client, device):
    device.stale_readbacks = 1
    assert (await client.async_start_auto_cleaning()).is_auto_cleaning
    assert len(device.requests) == 4
    device.frame = NORMAL
    assert (await client.async_get_status()).is_auto_cleaning is False


@pytest.mark.parametrize("method,args,before,after", [op for op in OPERATIONS if op[0] != "async_set_power"])
async def test_off_guards(client, device, method, args, before, after):
    device.frame = OFF
    with pytest.raises(PurifierError) as error:
        await getattr(client, method)(*args)
    assert error.value.code == Code.DEVICE_OFF
    assert not error.value.command_may_have_succeeded
    assert not device.writes


@pytest.mark.parametrize("method,args,before,after", OPERATIONS)
async def test_firmware_guard_including_noops(client, device, method, args, before, after):
    device.frame, device.firmware = after, "81.00"
    assert (await client.async_get_status()).auto_mode is None
    with pytest.raises(PurifierError) as error:
        await getattr(client, method)(*args)
    assert error.value.code == Code.UNSUPPORTED_FIRMWARE
    assert not error.value.command_may_have_succeeded
    assert not device.writes


@pytest.mark.parametrize("index,method,args", [(13, "async_set_smart_search", (False,)), (14, "async_start_auto_cleaning", ())])
async def test_unknown_guard(client, device, index, method, args):
    device.frame = with_byte(NORMAL, index, 0x32)
    with pytest.raises(PurifierError) as error:
        await getattr(client, method)(*args)
    assert error.value.code == Code.PROTOCOL_ERROR
    assert not error.value.command_may_have_succeeded
    assert not device.writes


@pytest.mark.parametrize("at", [0, 1, 2])
@pytest.mark.parametrize("failure,code", [("identity", Code.IDENTITY_MISMATCH), ("timeout", Code.REQUEST_TIMED_OUT),
    ("http", Code.CONNECTION_FAILED), ("envelope", Code.PROTOCOL_ERROR), ("oversize", Code.PROTOCOL_ERROR)])
async def test_failures_before_and_after_write(client, device, at, failure, code):
    client.request_timeout = 0.25 if failure == "timeout" else 5.0
    async def respond(payload, index):
        if index == at:
            if failure == "identity":
                return snapshot(mac="00:00:00:00:00:00")
            if failure == "timeout":
                await asyncio.sleep(0.5)
            if failure == "http":
                return web.Response(status=503)
            if failure == "envelope":
                return web.Response(text="<ESV>bad</ESV>")
            if failure == "oversize":
                return web.Response(body=b"x" * 70000)
        return snapshot()
    device.respond = respond
    with pytest.raises(PurifierError) as error:
        await client.async_set_auto_mode(AutoMode.NIGHT)
    assert error.value.code == code
    assert error.value.command_may_have_succeeded == (at > 0)
    assert len(device.writes) == int(at > 0)


@pytest.mark.parametrize("method,args", [("async_set_auto_mode", (AutoMode.NIGHT,)),
    ("async_set_manual_speed", (ManualSpeed.LOW,)), ("async_set_smart_search", (False,)), ("async_start_auto_cleaning", ())])
@pytest.mark.parametrize("change", ["firmware", "power", "mode"])
async def test_readback_changes(client, device, method, args, change):
    if change == "mode" and method in ("async_set_auto_mode", "async_set_manual_speed"):
        # Wrong selectors must exhaust confirmation, rather than being accepted.
        expected = Code.COMMAND_UNCONFIRMED
    else:
        expected = Code.PROTOCOL_ERROR
    async def respond(payload, index):
        return snapshot(with_byte(NORMAL, 8, 0x30) if change == "power" and index >= 2 else
                        TURBO if change == "mode" and index >= 2 else NORMAL,
                        firmware="81.00" if change == "firmware" and index >= 2 else "80.00")
    device.respond = respond
    with pytest.raises(PurifierError) as error:
        await getattr(client, method)(*args)
    assert error.value.code == expected
    assert error.value.command_may_have_succeeded
    assert len(device.writes) == 1


async def test_serialization_and_cancellation(client, device):
    started, release = asyncio.Event(), asyncio.Event()
    async def respond(payload, index):
        if index == 1:
            started.set()
            await release.wait()
        return snapshot(NORMAL if index < 2 else NIGHT)
    device.respond = respond
    write = asyncio.create_task(client.async_set_auto_mode(AutoMode.NIGHT))
    await asyncio.wait_for(started.wait(), 2)
    cancelled = asyncio.create_task(client.async_get_status())
    await asyncio.sleep(0)
    cancelled.cancel()
    with pytest.raises(asyncio.CancelledError):
        await cancelled
    read = asyncio.create_task(client.async_get_status())
    await asyncio.sleep(0)
    assert len(device.requests) == 2 and not read.done()
    release.set()
    assert (await write).auto_mode == AutoMode.NIGHT
    assert (await read).auto_mode == AutoMode.NIGHT
    assert len(device.requests) == 4


async def test_invalid_enum_before_io_and_status_only(client, device):
    for method in (client.async_set_auto_mode, client.async_set_manual_speed):
        with pytest.raises(ValueError):
            await method("invalid")
    assert device.requests == []
    await client.async_get_status()
    assert device.requests == ["<CSV></CSV>"]


async def test_power_preserves_manual(client, device):
    device.frame = HIGH
    result = await client.async_set_power(False)
    assert not result.is_on and result.manual_speed == ManualSpeed.HIGH
    assert device.writes[0][9] == 0x33


async def test_proxy_environment_is_bypassed(device, monkeypatch):
    monkeypatch.setenv("HTTP_PROXY", "http://127.0.0.1:1")
    monkeypatch.setenv("http_proxy", "http://127.0.0.1:1")
    monkeypatch.setenv("NO_PROXY", "")
    monkeypatch.setenv("no_proxy", "")
    async with ClientSession(trust_env=True) as session:
        client = PurifierClient(session, device.host)
        assert (await client.async_get_status()).mac == MAC


async def test_lost_write_reply_is_not_retried(client, device):
    async def respond(payload, index):
        if index == 1:
            raise web.HTTPServiceUnavailable()
        return snapshot()
    device.respond = respond
    with pytest.raises(PurifierError) as error:
        await client.async_set_manual_speed(ManualSpeed.LOW)
    assert error.value.code == Code.CONNECTION_FAILED
    assert error.value.command_may_have_succeeded
    assert len(device.writes) == 1
    assert len(device.requests) == 2


async def test_redirect_is_not_followed(client, device):
    async def respond(payload, index):
        return web.Response(status=302, headers={"Location": "/smart"})
    device.respond = respond
    with pytest.raises(PurifierError) as error:
        await client.async_get_status()
    assert error.value.code == Code.CONNECTION_FAILED
    assert len(device.requests) == 1


async def test_wrong_manual_and_auto_readbacks_never_confirm(client, device):
    device.frame = SILENT
    device.frames = [SILENT, SILENT, LOW, TURBO, HIGH]
    with pytest.raises(PurifierError) as error:
        await client.async_set_manual_speed(ManualSpeed.TURBO)
    assert error.value.code == Code.COMMAND_UNCONFIRMED
    assert error.value.command_may_have_succeeded
    assert len(device.writes) == 1


@pytest.mark.parametrize("host", ["http://example.com", "user:pass@example.com", "example.com/path", "example.com?q=x", "", "a b"])
def test_invalid_host(host):
    with pytest.raises(ValueError):
        PurifierClient(None, host)
