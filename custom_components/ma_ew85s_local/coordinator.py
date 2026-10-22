"""Polling and confirmed service updates."""
from collections.abc import Awaitable, Callable
import logging

from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator, UpdateFailed

from .const import DOMAIN, UPDATE_INTERVAL
from .protocol import PurifierError, PurifierErrorCode, PurifierState

LOGGER = logging.getLogger(__name__)


class PurifierCoordinator(DataUpdateCoordinator[PurifierState]):
    def __init__(self, hass, entry, client):
        super().__init__(hass, LOGGER, name=DOMAIN, config_entry=entry,
                         update_interval=UPDATE_INTERVAL)
        self.client = client

    async def _async_update_data(self) -> PurifierState:
        try:
            return await self.client.async_get_status()
        except PurifierError as err:
            raise UpdateFailed(str(err)) from err

    async def async_run(self, op: Callable[[], Awaitable[PurifierState]]) -> PurifierState:
        try:
            state = await op()
        except PurifierError as err:
            if err.code in (PurifierErrorCode.CONNECTION_FAILED, PurifierErrorCode.REQUEST_TIMED_OUT,
                            PurifierErrorCode.PROTOCOL_ERROR, PurifierErrorCode.IDENTITY_MISMATCH):
                self.async_set_update_error(UpdateFailed(str(err)))
            if err.command_may_have_succeeded:
                await self.async_refresh()
            if err.code == PurifierErrorCode.DEVICE_OFF:
                raise ServiceValidationError(str(err)) from err
            raise HomeAssistantError(str(err)) from err
        self.async_set_updated_data(state)
        return state
