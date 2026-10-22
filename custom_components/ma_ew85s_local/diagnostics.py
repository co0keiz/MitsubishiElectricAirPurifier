"""Only normalized, redacted diagnostics; no adapter XML is retained."""
from dataclasses import asdict


async def async_get_config_entry_diagnostics(hass, entry):
    state = asdict(entry.runtime_data.coordinator.data)
    state["mac"] = "**REDACTED**"
    state["frame_hex"] = state["frame_hex"][:12] + "**REDACTED**"
    state["observed_at"] = state["observed_at"].isoformat()
    return {"host": "**REDACTED**", "state": state,
            "last_update_success": entry.runtime_data.coordinator.last_update_success}
