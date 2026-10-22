"""Optional Wi-Fi signal diagnostic."""
from homeassistant.components.sensor import SensorDeviceClass, SensorEntity, SensorStateClass
from homeassistant.const import SIGNAL_STRENGTH_DECIBELS_MILLIWATT, EntityCategory

from .entity import PurifierEntity


async def async_setup_entry(hass, entry, async_add_entities):
    async_add_entities([SignalSensor(entry.runtime_data.coordinator)])


class SignalSensor(PurifierEntity, SensorEntity):
    _attr_device_class = SensorDeviceClass.SIGNAL_STRENGTH
    _attr_native_unit_of_measurement = SIGNAL_STRENGTH_DECIBELS_MILLIWATT
    _attr_state_class = SensorStateClass.MEASUREMENT
    _attr_entity_category = EntityCategory.DIAGNOSTIC
    _attr_entity_registry_enabled_default = False

    def __init__(self, coordinator):
        super().__init__(coordinator, "wifi_signal")

    @property
    def native_value(self):
        return self.coordinator.data.rssi
