"""Immutable device state."""
from dataclasses import dataclass
from datetime import datetime
from enum import StrEnum


class AutoMode(StrEnum):
    NORMAL = "normal"
    NIGHT = "night"
    TURBO = "turbo"


class ManualSpeed(StrEnum):
    SILENT = "silent"
    LOW = "low"
    MID = "mid"
    HIGH = "high"
    TURBO = "turbo"


@dataclass(frozen=True, slots=True)
class PurifierState:
    mac: str
    firmware: str
    is_on: bool
    auto_mode: AutoMode | None
    manual_speed: ManualSpeed | None
    is_auto_cleaning: bool | None
    is_smart_search_enabled: bool | None
    rssi: int | None
    observed_at: datetime
    frame_hex: str
