import pytest
pytest.importorskip("homeassistant")
from homeassistant.const import STATE_UNAVAILABLE
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers import entity_registry as er
from homeassistant.helpers.update_coordinator import UpdateFailed

from custom_components.ma_ew85s_local.const import DOMAIN
from custom_components.ma_ew85s_local.diagnostics import async_get_config_entry_diagnostics
from .fixtures import *

pytestmark = pytest.mark.ha


def entity_id(hass, platform, key):
    return er.async_get(hass).async_get_entity_id(platform, DOMAIN, f"{MAC}_{key}")


async def service(hass, entity, name, **data):
    await hass.services.async_call("fan", name, {"entity_id": entity, **data}, blocking=True)


@pytest.mark.parametrize("frame,percentage,preset", [
    *[(f, None, f"auto_{m.value}") for m, f in AUTO_FRAMES.items()],
    *[(f, n * 20, None) for n, f in enumerate(MANUAL_FRAMES.values(), 1)]])
async def test_fan_mapping(hass, device, ha_entry, frame, percentage, preset):
    device.frame = frame
    await ha_entry.runtime_data.coordinator.async_refresh()
    state = hass.states.get(entity_id(hass, "fan", "purifier"))
    assert state.state == "on"
    assert state.attributes["percentage"] == percentage
    assert state.attributes["preset_mode"] == preset
    assert state.attributes["preset_modes"] == ["auto_normal", "auto_night", "auto_turbo"]


@pytest.mark.parametrize("percentage,expected", list(enumerate(MANUAL_FRAMES.values(), 1)))
async def test_set_percentage(hass, device, ha_entry, percentage, expected):
    fan = entity_id(hass, "fan", "purifier")
    await service(hass, fan, "set_percentage", percentage=percentage * 20)
    assert device.frame == expected
    assert len(device.writes) == 1
    assert hass.states.get(fan).attributes["percentage"] == percentage * 20


@pytest.mark.parametrize("mode,frame", AUTO_FRAMES.items())
async def test_set_preset(hass, device, ha_entry, mode, frame):
    device.frame = MANUAL_TURBO
    await service(hass, entity_id(hass, "fan", "purifier"), "set_preset_mode", preset_mode=f"auto_{mode}")
    assert device.frame == frame


@pytest.mark.parametrize("setting,expected", [({"percentage": 60}, MID), ({"preset_mode": "auto_night"}, NIGHT), ({}, NORMAL)])
async def test_turn_on_then_set(hass, device, ha_entry, setting, expected):
    device.frame = OFF
    fan = entity_id(hass, "fan", "purifier")
    await service(hass, fan, "turn_on", **setting)
    assert device.frame == expected
    assert device.writes[0][6] == 1
    if setting:
        assert [w[6] for w in device.writes] == [1, 2]
    await service(hass, fan, "turn_off")
    assert hass.states.get(fan).state == "off"


@pytest.mark.parametrize("name,data", [("set_percentage", {"percentage": 40}), ("set_preset_mode", {"preset_mode": "auto_night"})])
async def test_off_errors(hass, device, ha_entry, name, data):
    device.frame = OFF
    with pytest.raises(ServiceValidationError):
        await service(hass, entity_id(hass, "fan", "purifier"), name, **data)
    assert not device.writes


async def test_unavailable_and_recovery(hass, device, ha_entry):
    fan = entity_id(hass, "fan", "purifier")
    coordinator = ha_entry.runtime_data.coordinator
    device.fail = True
    await coordinator.async_refresh()
    assert isinstance(coordinator.last_exception, UpdateFailed)
    assert hass.states.get(fan).state == STATE_UNAVAILABLE
    with pytest.raises(HomeAssistantError):
        await coordinator.async_run(lambda: coordinator.client.async_set_power(False))
    device.fail = False
    await coordinator.async_refresh()
    assert hass.states.get(fan).state == "on"


async def test_failed_write_marks_unavailable(hass, device, ha_entry):
    fan = entity_id(hass, "fan", "purifier")
    device.fail = True
    with pytest.raises(HomeAssistantError):
        await service(hass, fan, "turn_off")
    assert hass.states.get(fan).state == STATE_UNAVAILABLE
    assert not device.writes


async def test_unconfirmed_refresh_and_diagnostics(hass, device, ha_entry):
    device.apply_writes = False
    coordinator = ha_entry.runtime_data.coordinator
    fan = entity_id(hass, "fan", "purifier")
    count = len(device.requests)
    with pytest.raises(HomeAssistantError, match="may have succeeded"):
        await service(hass, fan, "set_preset_mode", preset_mode="auto_night")
    assert len(device.requests) - count == 6  # pre-read, write, 3 readbacks, refresh
    assert len(device.writes) == 1
    assert hass.states.get(fan).attributes["preset_mode"] == "auto_normal"
    diagnostic = await async_get_config_entry_diagnostics(hass, ha_entry)
    assert MAC not in str(diagnostic)
    assert device.host not in str(diagnostic)
    assert NORMAL not in str(diagnostic)


async def test_unload_reload(hass, device, ha_entry):
    fan = entity_id(hass, "fan", "purifier")
    old_coordinator = ha_entry.runtime_data.coordinator
    assert await hass.config_entries.async_unload(ha_entry.entry_id)
    assert hass.states.get(fan).state == STATE_UNAVAILABLE
    assert not old_coordinator.async_update_listeners()
    assert await hass.config_entries.async_setup(ha_entry.entry_id)
    await hass.async_block_till_done()
    assert hass.states.get(fan).state == "on"
    assert len(er.async_get(hass).entities) == 5
