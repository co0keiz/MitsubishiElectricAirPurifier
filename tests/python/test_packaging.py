"""Cross-file contracts for distribution and protocol independence."""
import ast
import json
from pathlib import Path

ROOT = Path("custom_components/ma_ew85s_local")


def test_protocol_has_no_ha_imports():
    for path in (ROOT / "protocol").glob("*.py"):
        tree = ast.parse(path.read_text(encoding="utf-8"))
        for node in ast.walk(tree):
            if isinstance(node, ast.ImportFrom):
                assert not (node.module or "").startswith("homeassistant")
            elif isinstance(node, ast.Import):
                assert all(not alias.name.startswith("homeassistant") for alias in node.names)


def test_manifest_and_translations():
    manifest = json.loads((ROOT / "manifest.json").read_text())
    assert manifest["domain"] == "ma_ew85s_local"
    assert manifest["config_flow"] is True
    assert manifest["iot_class"] == "local_polling"
    assert manifest["requirements"] == ["cryptography"]
    assert manifest["version"] == "0.1.0"
    assert all(key in manifest for key in ("documentation", "issue_tracker", "codeowners", "name"))
    strings = json.loads((ROOT / "strings.json").read_text())
    assert strings == json.loads((ROOT / "translations/en.json").read_text())
    assert set(strings["entity"]) == {"fan", "switch", "button", "binary_sensor", "sensor"}
    assert set(json.loads((ROOT / "icons.json").read_text())["entity"]) == set(strings["entity"])
