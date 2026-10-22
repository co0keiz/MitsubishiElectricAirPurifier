"""Validated state frames and single-setting commands; see protocol specification."""
import re
from datetime import datetime

from .codec import parse_xml
from .errors import PurifierError, PurifierErrorCode, protocol_error
from .models import AutoMode, ManualSpeed, PurifierState

VERIFIED_FIRMWARE = "80.00"
PREFIX = bytes.fromhex("fc6201351002")
AUTO_VALUES = {AutoMode.NORMAL: 0x42, AutoMode.NIGHT: 0x41, AutoMode.TURBO: 0x43}
MANUAL_VALUES = dict(zip(ManualSpeed, range(0x30, 0x35), strict=True))


def checksum(frame: bytes | bytearray) -> int:
    return (-sum(frame[1:-1])) & 0xFF


def validate_frame(frame: bytes) -> None:
    if len(frame) != 22 or frame[:6] != PREFIX:
        raise protocol_error("Unsupported purifier general-state frame.")
    if checksum(frame) != frame[-1]:
        raise protocol_error("Invalid frame checksum.")
    if frame[8] not in (0x30, 0x31):
        raise protocol_error("Unknown purifier power encoding.")


def build_command(status: bytes, *, on: bool | None = None,
                  mode: AutoMode | None = None, manual_speed: ManualSpeed | None = None,
                  start_auto_cleaning: bool = False,
                  smart_search_enabled: bool | None = None) -> bytes:
    validate_frame(status)
    if sum((on is not None, mode is not None, manual_speed is not None,
            start_auto_cleaning, smart_search_enabled is not None)) != 1:
        raise ValueError("Select exactly one setting per command.")
    if on is not None:
        mask, index, value = 0x01, 8, 0x31 if on else 0x30
    elif mode is not None:
        mask, index, value = 0x02, 9, AUTO_VALUES[AutoMode(mode)]
    elif manual_speed is not None:
        mask, index, value = 0x02, 9, MANUAL_VALUES[ManualSpeed(manual_speed)]
    elif smart_search_enabled is not None:
        mask, index, value = 0x20, 13, 0x31 if smart_search_enabled else 0x30
    else:
        mask, index, value = 0x40, 14, 0x31
    frame = bytearray(status)
    frame[1], frame[5], frame[6], frame[7] = 0x41, 0x01, mask, 0
    frame[index] = value
    frame[-1] = checksum(frame)
    return bytes(frame)


def parse_state(xml: str, observed_at: datetime, expected_mac: str | None = None) -> PurifierState:
    root = parse_xml(xml)
    if root.tag != "LSV":
        raise protocol_error("Expected an LSV response.")
    mac = (root.findtext("MAC") or "").strip().lower()
    if not re.fullmatch(r"(?:[0-9a-f]{2}:){5}[0-9a-f]{2}", mac):
        raise protocol_error("Missing or invalid MAC identity.")
    if expected_mac is not None and mac != expected_mac.lower():
        raise PurifierError(PurifierErrorCode.IDENTITY_MISMATCH, "Device identity mismatch.")
    value = next((el.text.strip() for el in root.findall("CODE/VALUE")
                  if el.text and el.text.strip().lower().startswith(PREFIX.hex())), None)
    if value is None:
        raise protocol_error("No supported general-state frame returned.")
    try:
        frame = bytes.fromhex(value)
    except ValueError as err:
        raise protocol_error("Invalid frame hexadecimal encoding.") from err
    validate_frame(frame)
    firmware = (root.findtext("APP_VER") or "").strip()
    known = firmware == VERIFIED_FIRMWARE
    auto = next((mode for mode, code in AUTO_VALUES.items() if code == frame[9]), None) if known else None
    manual = next((speed for speed, code in MANUAL_VALUES.items() if code == frame[9]), None) if known else None
    def boolean(index: int) -> bool | None:
        return {0x30: False, 0x31: True}.get(frame[index]) if known else None
    try:
        rssi = int(root.findtext("RSSI"))
    except (TypeError, ValueError):
        rssi = None
    return PurifierState(mac, firmware, frame[8] == 0x31, auto, manual,
                         boolean(14), boolean(13), rssi, observed_at, frame.hex())
