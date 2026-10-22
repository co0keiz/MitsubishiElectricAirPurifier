"""Manual setup and identity-checked host reconfiguration."""
from homeassistant import config_entries
from homeassistant.const import CONF_HOST
from homeassistant.helpers.aiohttp_client import async_get_clientsession
import probatio as vol

from .const import DOMAIN, MODEL
from .protocol import PurifierClient, PurifierError, PurifierErrorCode
from .protocol.frame import VERIFIED_FIRMWARE


class PurifierFlow(config_entries.ConfigFlow, domain=DOMAIN):
    VERSION = 1

    async def async_step_user(self, user_input=None):
        return await self._async_host_step("user", user_input)

    async def async_step_reconfigure(self, user_input=None):
        return await self._async_host_step("reconfigure", user_input)

    async def _async_host_step(self, step_id, user_input):
        errors = {}
        if user_input is not None:
            host = user_input[CONF_HOST].strip()
            try:
                client = PurifierClient(async_get_clientsession(self.hass), host)
                state = await client.async_get_status()
                if state.firmware != VERIFIED_FIRMWARE:
                    raise PurifierError(PurifierErrorCode.UNSUPPORTED_FIRMWARE, "Unsupported firmware.")
            except PurifierError as err:
                errors["base"] = ("unsupported_firmware" if err.code == PurifierErrorCode.UNSUPPORTED_FIRMWARE
                                  else "cannot_connect")
            except ValueError:
                errors["base"] = "cannot_connect"
            else:
                await self.async_set_unique_id(state.mac)
                if step_id == "reconfigure":
                    self._abort_if_unique_id_mismatch()
                    return self.async_update_reload_and_abort(
                        self._get_reconfigure_entry(), data_updates={CONF_HOST: host})
                self._abort_if_unique_id_configured()
                return self.async_create_entry(title=f"{MODEL} ({state.mac[-8:]})", data={CONF_HOST: host})
        default = ""
        if step_id == "reconfigure":
            default = self._get_reconfigure_entry().data[CONF_HOST]
        return self.async_show_form(step_id=step_id,
                                   data_schema=vol.Schema({vol.Required(CONF_HOST, default=default): str}),
                                   errors=errors)
