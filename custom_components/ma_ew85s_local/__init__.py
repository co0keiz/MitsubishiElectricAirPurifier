"""Local MA-EW85S-E integration."""
from __future__ import annotations

from dataclasses import dataclass
from typing import TYPE_CHECKING

from .protocol import PurifierClient

if TYPE_CHECKING:
    from homeassistant.config_entries import ConfigEntry
    from homeassistant.core import HomeAssistant
    from .coordinator import PurifierCoordinator


@dataclass(slots=True)
class RuntimeData:
    client: PurifierClient
    coordinator: PurifierCoordinator


type PurifierConfigEntry = ConfigEntry[RuntimeData]


async def async_setup_entry(hass: HomeAssistant, entry: PurifierConfigEntry) -> bool:
    from homeassistant.const import CONF_HOST
    from homeassistant.helpers.aiohttp_client import async_get_clientsession
    from .const import PLATFORMS
    from .coordinator import PurifierCoordinator

    client = PurifierClient(async_get_clientsession(hass), entry.data[CONF_HOST],
                            expected_mac=entry.unique_id)
    coordinator = PurifierCoordinator(hass, entry, client)
    await coordinator.async_config_entry_first_refresh()
    entry.runtime_data = RuntimeData(client, coordinator)
    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    return True


async def async_unload_entry(hass: HomeAssistant, entry: PurifierConfigEntry) -> bool:
    from .const import PLATFORMS
    return await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
