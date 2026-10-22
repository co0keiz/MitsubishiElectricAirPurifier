"""Pre-filter cleaning activity, including unknown encodings."""
from homeassistant.components.binary_sensor import BinarySensorDeviceClass, BinarySensorEntity

from .entity import PurifierEntity


async def async_setup_entry(hass, entry, async_add_entities):
    async_add_entities([CleaningSensor(entry.runtime_data.coordinator)])


class CleaningSensor(PurifierEntity, BinarySensorEntity):
    _attr_device_class = BinarySensorDeviceClass.RUNNING

    def __init__(self, coordinator):
        super().__init__(coordinator, "auto_cleaning")

    @property
    def is_on(self):
        return self.coordinator.data.is_auto_cleaning
