"""Shared identity and coordinator availability."""
from homeassistant.helpers.device_registry import CONNECTION_NETWORK_MAC, DeviceInfo
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from .const import DOMAIN, MODEL
from .coordinator import PurifierCoordinator


class PurifierEntity(CoordinatorEntity[PurifierCoordinator]):
    _attr_has_entity_name = True

    def __init__(self, coordinator, key):
        super().__init__(coordinator)
        state = coordinator.data
        self._attr_unique_id = f"{state.mac}_{key}"
        self._attr_translation_key = key
        self._attr_device_info = DeviceInfo(
            identifiers={(DOMAIN, state.mac)}, connections={(CONNECTION_NETWORK_MAC, state.mac)},
            name=f"{MODEL} ({state.mac[-8:]})", model=MODEL,
            manufacturer="Mitsubishi Electric", sw_version=state.firmware,
        )
