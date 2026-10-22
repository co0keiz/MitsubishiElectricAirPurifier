"""Power, automatic presets, and five manual speeds."""
from functools import partial

from homeassistant.components.fan import FanEntity, FanEntityFeature
from homeassistant.exceptions import ServiceValidationError
from homeassistant.util.percentage import ordered_list_item_to_percentage, percentage_to_ordered_list_item

from .entity import PurifierEntity
from .protocol import AutoMode, ManualSpeed

SPEEDS = list(ManualSpeed)
PRESETS = {f"auto_{mode.value}": mode for mode in AutoMode}


async def async_setup_entry(hass, entry, async_add_entities):
    async_add_entities([PurifierFan(entry.runtime_data.coordinator)])


class PurifierFan(PurifierEntity, FanEntity):
    _attr_name = None
    _attr_speed_count = 5
    _attr_preset_modes = list(PRESETS)
    _attr_supported_features = (FanEntityFeature.TURN_ON | FanEntityFeature.TURN_OFF
                                | FanEntityFeature.SET_SPEED | FanEntityFeature.PRESET_MODE)

    def __init__(self, coordinator):
        super().__init__(coordinator, "purifier")

    @property
    def is_on(self):
        return self.coordinator.data.is_on

    @property
    def percentage(self):
        speed = self.coordinator.data.manual_speed
        return ordered_list_item_to_percentage(SPEEDS, speed) if speed is not None else None

    @property
    def preset_mode(self):
        mode = self.coordinator.data.auto_mode
        return f"auto_{mode.value}" if mode is not None else None

    async def async_turn_on(self, percentage=None, preset_mode=None, **kwargs):
        if preset_mode is not None and preset_mode not in PRESETS:
            raise ServiceValidationError("Unknown automatic preset.")
        await self.coordinator.async_run(partial(self.coordinator.client.async_set_power, True))
        if preset_mode is not None:
            await self.async_set_preset_mode(preset_mode)
        elif percentage is not None:
            await self.async_set_percentage(percentage)

    async def async_turn_off(self, **kwargs):
        await self.coordinator.async_run(partial(self.coordinator.client.async_set_power, False))

    async def async_set_percentage(self, percentage):
        if percentage == 0:
            await self.async_turn_off()
            return
        speed = percentage_to_ordered_list_item(SPEEDS, percentage)
        await self.coordinator.async_run(partial(self.coordinator.client.async_set_manual_speed, speed))

    async def async_set_preset_mode(self, preset_mode):
        if preset_mode not in PRESETS:
            raise ServiceValidationError("Unknown automatic preset.")
        await self.coordinator.async_run(partial(self.coordinator.client.async_set_auto_mode, PRESETS[preset_mode]))
