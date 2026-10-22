import pytest
pytest.importorskip("homeassistant")
from homeassistant.const import EntityCategory, STATE_UNAVAILABLE, STATE_UNKNOWN
from homeassistant.exceptions import ServiceValidationError
from homeassistant.helpers import device_registry as dr, entity_registry as er

from custom_components.ma_ew85s_local.const import DOMAIN
from .fixtures import *
from .test_fan import entity_id

pytestmark = pytest.mark.ha
HELPERS = [("switch", "smart_search"), ("button", "start_auto_cleaning"),
           ("binary_sensor", "auto_cleaning"), ("sensor", "wifi_signal")]


async def test_states_and_registry(hass, device, ha_entry):
    assert hass.states.get(entity_id(hass, "switch", "smart_search")).state == "on"
    cleaning = entity_id(hass, "binary_sensor", "auto_cleaning")
    assert hass.states.get(cleaning).state == "off"
    assert hass.states.get(cleaning).attributes["device_class"] == "running"
    registry = er.async_get(hass)
    signal = entity_id(hass, "sensor", "wifi_signal")
    registered = registry.async_get(signal)
    assert registered.disabled_by == er.RegistryEntryDisabler.INTEGRATION
    assert registered.entity_category == EntityCategory.DIAGNOSTIC
    assert hass.states.get(signal) is None
    # Enable the optional sensor and reload without creating duplicate entities.
    registry.async_update_entity(signal, disabled_by=None)
    await hass.config_entries.async_reload(ha_entry.entry_id)
    await hass.async_block_till_done()
    state = hass.states.get(signal)
    assert state.state == "-58"
    assert state.attributes["unit_of_measurement"] == "dBm"
    assert state.attributes["device_class"] == "signal_strength"
    devices = dr.async_get(hass).devices
    assert len(devices) == 1
    device_info = next(iter(devices))
    assert device_info.model == "MA-EW85S-E"
    assert device_info.manufacturer == "Mitsubishi Electric"
    assert device_info.sw_version == "80.00"
    assert (dr.CONNECTION_NETWORK_MAC, MAC) in device_info.connections
    assert len(registry.entities) == 5


async def test_smart_search_services_and_noop(hass, device, ha_entry):
    switch = entity_id(hass, "switch", "smart_search")
    for name, expected in [("turn_off", "off"), ("turn_on", "on")]:
        await hass.services.async_call("switch", name, {"entity_id": switch}, blocking=True)
        assert hass.states.get(switch).state == expected
        assert device.writes[-1][6] == 0x20
    writes = len(device.writes)
    await hass.services.async_call("switch", "turn_on", {"entity_id": switch}, blocking=True)
    assert len(device.writes) == writes


async def test_cleaning_button_noop_and_completion(hass, device, ha_entry):
    button = entity_id(hass, "button", "start_auto_cleaning")
    cleaning = entity_id(hass, "binary_sensor", "auto_cleaning")
    await hass.services.async_call("button", "press", {"entity_id": button}, blocking=True)
    assert hass.states.get(cleaning).state == "on"
    assert device.writes[-1][6] == 0x40
    await hass.services.async_call("button", "press", {"entity_id": button}, blocking=True)
    assert len(device.writes) == 1
    device.frame = NORMAL
    await ha_entry.runtime_data.coordinator.async_refresh()
    assert hass.states.get(cleaning).state == "off"


@pytest.mark.parametrize("platform,key,service", [("switch", "smart_search", "turn_on"),
    ("switch", "smart_search", "turn_off"), ("button", "start_auto_cleaning", "press")])
async def test_off_services(hass, device, ha_entry, platform, key, service):
    device.frame = OFF
    with pytest.raises(ServiceValidationError):
        await hass.services.async_call(platform, service, {"entity_id": entity_id(hass, platform, key)}, blocking=True)
    assert not device.writes


@pytest.mark.parametrize("index,platform,key", [(13, "switch", "smart_search"), (14, "binary_sensor", "auto_cleaning")])
async def test_unknown_encoding(hass, device, ha_entry, index, platform, key):
    device.frame = with_byte(NORMAL, index, 0x32)
    await ha_entry.runtime_data.coordinator.async_refresh()
    assert hass.states.get(entity_id(hass, platform, key)).state == STATE_UNKNOWN


async def test_all_unavailable_unload_reload(hass, device, ha_entry):
    registry = er.async_get(hass)
    registry.async_update_entity(entity_id(hass, "sensor", "wifi_signal"), disabled_by=None)
    assert await hass.config_entries.async_reload(ha_entry.entry_id)
    await hass.async_block_till_done()
    entities = [entity_id(hass, platform, key) for platform, key in HELPERS]
    device.fail = True
    await ha_entry.runtime_data.coordinator.async_refresh()
    assert all(hass.states.get(entity).state == STATE_UNAVAILABLE for entity in entities)
    assert await hass.config_entries.async_unload(ha_entry.entry_id)
    device.fail = False
    assert await hass.config_entries.async_setup(ha_entry.entry_id)
    await hass.async_block_till_done()
    assert all(hass.states.get(entity).state != STATE_UNAVAILABLE for entity in entities)
    assert len(registry.entities) == 5
