"""All device traffic goes to an encrypted loopback simulator."""
import pytest
from importlib.util import find_spec
from aiohttp import ClientSession, web

from custom_components.ma_ew85s_local.protocol import PurifierClient
from .fake_device import FakeDevice, instant_sleep
from .fixtures import MAC, NOW


@pytest.fixture(autouse=True)
def loopback_only(request):
    if find_spec("pytest_homeassistant_custom_component") is not None:
        request.getfixturevalue("socket_enabled")
        import pytest_socket
        pytest_socket.socket_allow_hosts(["127.0.0.1", "::1"], allow_unix_socket=True)


def pytest_make_parametrize_id(config, val, argname):
    if isinstance(val, str) and len(val) > 80:
        return f"{argname}-oversize"


@pytest.fixture
async def device(aiohttp_server):
    device = FakeDevice()
    app = web.Application()
    app.router.add_post("/smart", device.handle)
    server = await aiohttp_server(app)
    device.host = f"127.0.0.1:{server.port}"
    return device


@pytest.fixture
async def client(device):
    async with ClientSession(trust_env=False) as session:
        yield PurifierClient(session, device.host, expected_mac=MAC, sleep=instant_sleep,
                             clock=lambda: NOW)


@pytest.fixture
async def ha_entry(hass, device, enable_custom_integrations, monkeypatch):
    from homeassistant.const import CONF_HOST
    from pytest_homeassistant_custom_component.common import MockConfigEntry
    from custom_components.ma_ew85s_local.const import DOMAIN

    # Keep the real encrypted transport, replace only the five-second wait.
    original_init = PurifierClient.__init__
    def init(self, *args, **kwargs):
        original_init(self, *args, **kwargs, sleep=instant_sleep)
    monkeypatch.setattr(PurifierClient, "__init__", init)
    entry = MockConfigEntry(domain=DOMAIN, unique_id=MAC, title="Test purifier",
                            data={CONF_HOST: device.host})
    entry.add_to_hass(hass)
    assert await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()
    return entry
