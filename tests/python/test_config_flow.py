from unittest.mock import AsyncMock, patch

import pytest
pytest.importorskip("homeassistant")
from homeassistant import config_entries
from homeassistant.const import CONF_HOST
from homeassistant.data_entry_flow import FlowResultType
from pytest_homeassistant_custom_component.common import MockConfigEntry

from custom_components.ma_ew85s_local.const import DOMAIN
from .fixtures import MAC

pytestmark = pytest.mark.ha


async def test_user_success(hass, device, enable_custom_integrations):
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": "user"})
    assert result["type"] == FlowResultType.FORM
    with patch("custom_components.ma_ew85s_local.async_setup_entry", return_value=True):
        result = await hass.config_entries.flow.async_configure(result["flow_id"], {CONF_HOST: device.host})
        await hass.async_block_till_done()
    assert result["type"] == FlowResultType.CREATE_ENTRY
    assert result["data"] == {CONF_HOST: device.host}
    assert result["result"].unique_id == MAC
    assert result["title"] == "MA-EW85S-E (5b:6d:1e)"


@pytest.mark.parametrize("kind,error", [("http", "cannot_connect"), ("protocol", "cannot_connect"),
                                      ("firmware", "unsupported_firmware"), ("host", "cannot_connect")])
async def test_errors(hass, device, enable_custom_integrations, kind, error):
    if kind == "http":
        device.fail = True
    elif kind == "protocol":
        device.frame = "fc6201351002" + "00" * 16
    elif kind == "firmware":
        device.firmware = "81.00"
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": "user"},
                data={CONF_HOST: "bad/path" if kind == "host" else device.host})
    assert result["type"] == FlowResultType.FORM
    assert result["errors"] == {"base": error}
    assert not device.writes


async def test_unreachable(hass, unused_tcp_port, enable_custom_integrations):
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": "user"},
                                                      data={CONF_HOST: f"127.0.0.1:{unused_tcp_port}"})
    assert result["errors"] == {"base": "cannot_connect"}


async def test_duplicate(hass, device, enable_custom_integrations):
    entry = MockConfigEntry(domain=DOMAIN, unique_id=MAC, data={CONF_HOST: device.host})
    entry.add_to_hass(hass)
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": "user"}, data={CONF_HOST: device.host})
    assert result["type"] == FlowResultType.ABORT
    assert result["reason"] == "already_configured"


@pytest.mark.parametrize("mismatch", [False, True])
async def test_reconfigure(hass, device, enable_custom_integrations, mismatch):
    entry = MockConfigEntry(domain=DOMAIN, unique_id=MAC, data={CONF_HOST: "old.local"})
    entry.add_to_hass(hass)
    if mismatch:
        device.mac = "00:00:00:00:00:01"
    result = await hass.config_entries.flow.async_init(DOMAIN,
        context={"source": config_entries.SOURCE_RECONFIGURE, "entry_id": entry.entry_id})
    assert result["step_id"] == "reconfigure"
    with patch.object(hass.config_entries, "async_reload", AsyncMock(return_value=True)) as reload:
        result = await hass.config_entries.flow.async_configure(result["flow_id"], {CONF_HOST: device.host})
        await hass.async_block_till_done()
    assert result["type"] == FlowResultType.ABORT
    assert result["reason"] == ("unique_id_mismatch" if mismatch else "reconfigure_successful")
    assert entry.data[CONF_HOST] == ("old.local" if mismatch else device.host)
    assert reload.call_count == int(not mismatch)
    assert len(hass.config_entries.async_entries(DOMAIN)) == 1
