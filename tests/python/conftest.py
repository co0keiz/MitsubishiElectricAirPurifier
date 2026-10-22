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
