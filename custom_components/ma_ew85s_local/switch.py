"""Explicit Smart Search setting."""
from functools import partial

from homeassistant.components.switch import SwitchEntity

from .entity import PurifierEntity


async def async_setup_entry(hass, entry, async_add_entities):
    async_add_entities([SmartSearchSwitch(entry.runtime_data.coordinator)])


class SmartSearchSwitch(PurifierEntity, SwitchEntity):
    def __init__(self, coordinator):
        super().__init__(coordinator, "smart_search")

    @property
    def is_on(self):
        return self.coordinator.data.is_smart_search_enabled

    async def async_turn_on(self, **kwargs):
        await self.coordinator.async_run(partial(self.coordinator.client.async_set_smart_search, True))

    async def async_turn_off(self, **kwargs):
        await self.coordinator.async_run(partial(self.coordinator.client.async_set_smart_search, False))
