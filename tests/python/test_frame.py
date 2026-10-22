import re
from pathlib import Path

import pytest

from custom_components.ma_ew85s_local.protocol import AutoMode, ManualSpeed, PurifierError
from custom_components.ma_ew85s_local.protocol.frame import build_command, parse_state, validate_frame
from .fixtures import *


@pytest.mark.parametrize("frame", STATUSES)
def test_captured_states(frame):
    state = parse_state(snapshot(frame), NOW)
    raw = bytes.fromhex(frame)
    assert state.is_on == (frame != OFF)
    assert state.auto_mode == next((m for m, f in AUTO_FRAMES.items() if f == frame),
                                   AutoMode.NORMAL if frame in (OFF, SEARCH_OFF, CLEANING) else None)
    assert state.manual_speed == next((m for m, f in MANUAL_FRAMES.items() if f == frame), None)
    assert state.is_smart_search_enabled == (frame != SEARCH_OFF)
    assert state.is_auto_cleaning == (frame == CLEANING)
    assert state.observed_at == NOW
    assert state.rssi == -58
    assert state.frame_hex == frame
    assert sum(raw[1:]) % 256 == 0


@pytest.mark.parametrize("setting,expected", COMMANDS)
def test_exact_commands_and_preservation(setting, expected):
    assert build_command(bytes.fromhex(NORMAL), **setting).hex() == expected
    if "on" in setting:
        assert build_command(bytes.fromhex(OFF), **setting).hex() == expected
    source = bytes.fromhex(with_byte(with_byte(CLEANING, 11, 0x88), 19, 0x45))
    original = bytes(source)
    command = build_command(source, **setting)
    target = 8 if "on" in setting else 13 if "smart_search_enabled" in setting else 14 if "start_auto_cleaning" in setting else 9
    for i in range(22):
        if i not in (1, 5, 6, 7, target, 21):
            assert command[i] == source[i]
    assert source == original
    assert sum(command[1:]) % 256 == 0


def test_fixture_inventory_matches_spec():
    text = Path("MA-EW85S-local-control.md").read_text(encoding="utf-8").split("## 8. Fixtures appendix")[1].split("## 9.")[0]
    assert set(re.findall(r"\bfc[0-9a-f]{42}\b", text)) == set(STATUSES + [f for _, f in COMMANDS] + PROFILES)


@pytest.mark.parametrize("frame", [b"", bytes.fromhex(NORMAL)[:-1], bytes.fromhex(NORMAL + "00"),
    bytes.fromhex(NORMAL[:-2] + "00"), bytes.fromhex(with_byte(NORMAL, 3, 0x30)),
    bytes.fromhex(with_byte(NORMAL, 0, 0xfd)), bytes.fromhex(with_byte(NORMAL, 8, 0x32))])
def test_invalid_frames(frame):
    with pytest.raises(PurifierError):
        validate_frame(frame)
    with pytest.raises(PurifierError):
        build_command(frame, on=True)


@pytest.mark.parametrize("setting", [{}, {"on": True, "mode": AutoMode.NORMAL},
    {"mode": AutoMode.NORMAL, "manual_speed": ManualSpeed.LOW},
    {"start_auto_cleaning": True, "smart_search_enabled": False}])
def test_reject_combined_or_empty_commands(setting):
    with pytest.raises(ValueError):
        build_command(bytes.fromhex(NORMAL), **setting)


@pytest.mark.parametrize("index,field", [(9, "auto_mode"), (13, "is_smart_search_enabled"), (14, "is_auto_cleaning")])
def test_unknown_values(index, field):
    state = parse_state(snapshot(with_byte(NORMAL, index, 0x99)), NOW)
    assert getattr(state, field) is None
    if index == 9:
        assert state.manual_speed is None


def test_unsupported_firmware_and_bad_rssi():
    state = parse_state(snapshot(firmware="81.00", rssi="invalid"), NOW)
    assert all(getattr(state, f) is None for f in ("auto_mode", "manual_speed", "is_smart_search_enabled", "is_auto_cleaning", "rssi"))


@pytest.mark.parametrize("xml", ["<CSV/>", f"<LSV><MAC>{MAC}</MAC><PROFILECODE><VALUE>{NORMAL}</VALUE></PROFILECODE></LSV>",
    snapshot().replace("<CODE>", "<WRAPPER><CODE>").replace("</CODE>", "</CODE></WRAPPER>"),
    snapshot().replace(MAC, ""), snapshot().replace(NORMAL, NORMAL[:12] + "g" * 32)])
def test_invalid_status_xml(xml):
    with pytest.raises(PurifierError):
        parse_state(xml, NOW)


def test_other_commands_preserve_search_off_and_active_cleaning():
    source = bytes.fromhex(with_byte(CLEANING, 13, 0x30))
    for setting in ({"on": False}, {"mode": AutoMode.NIGHT}, {"manual_speed": ManualSpeed.LOW},
                    {"start_auto_cleaning": True}):
        assert build_command(source, **setting)[13] == 0x30
    assert build_command(source, smart_search_enabled=True)[14] == 0x31


def test_profile_lookalike_is_not_selected():
    xml = snapshot(NIGHT).replace(PROFILES[0], OFF)
    assert parse_state(xml, NOW).frame_hex == NIGHT
