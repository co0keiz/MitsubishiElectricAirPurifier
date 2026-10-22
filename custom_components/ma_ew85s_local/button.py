"""One-shot pre-filter cleaning start."""
from homeassistant.components.button import ButtonEntity

from .entity import PurifierEntity


async def async_setup_entry(hass, entry, async_add_entities):
    async_add_entities([StartCleaningButton(entry.runtime_data.coordinator)])


class StartCleaningButton(PurifierEntity, ButtonEntity):
    def __init__(self, coordinator):
        super().__init__(coordinator, "start_auto_cleaning")

    async def async_press(self):
        await self.coordinator.async_run(self.coordinator.client.async_start_auto_cleaning)
