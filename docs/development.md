# Development and testing

Run the commands below from the repository root. The test suites use simulated devices; no purifier or cloud account is needed.

## Prerequisites

| Work | Environment |
| --- | --- |
| C# client and API | .NET 10 SDK |
| Python protocol and packaging tests | Python 3.13+ on Windows or Linux |
| Full Home Assistant suite | Linux and Python 3.14, with the test dependencies pinned in `pyproject.toml` |
| PowerShell reference self-test | PowerShell 7+ (`pwsh`) |

The current HA test extra pins `pytest-homeassistant-custom-component` to `0.13.373`, which selects Home Assistant `2026.10.2`. Keep the Python version and HA test dependency compatible when updating them.

## Python setup on Windows

```powershell
py -3.14 -m venv .venv
.venv\Scripts\Activate.ps1
python -m pip install ".[test]"
python -m pytest -q
```

Python 3.13 can also run the protocol suite. If you use it, select `py -3.13` when creating the environment. With Home Assistant absent, its three test modules are skipped; use the Linux setup below for complete integration validation.

## Full Python and Home Assistant suite on Linux

```bash
python3.14 -m venv .venv
source .venv/bin/activate
python -m pip install ".[test,ha-test]"
python -m pytest -q
```

Use a separate Linux virtual environment if the checkout is shared with Windows. Virtual environments are specific to their operating system.

Run a focused subset while changing a component:

```text
python -m pytest -q tests/python/test_client.py
python -m pytest -q tests/python/test_config_flow.py tests/python/test_fan.py
python -m pytest -q tests/python/test_helpers_entities.py
```

The protocol fixtures inject a fixed observation clock and a replacement for the five-second readback wait. Timeout tests still exercise bounded real request timeouts. With the HA plugin installed, test sockets are restricted to loopback. The fake purifier decrypts requests, validates commands and preserved fields, and encrypts its responses.

## C# regression suite

```powershell
dotnet test MitsubishiElectricAirPurifier.slnx
```

If MSBuild worker startup stalls in a restricted Windows environment, the command used successfully during validation was:

```powershell
dotnet test MitsubishiElectricAirPurifier.slnx --disable-build-servers -m:1
```

The solution targets .NET 10 and treats compiler warnings as errors. API tests use an in-process application host and a simulated device transport.

## PowerShell self-test

```powershell
pwsh -NoProfile -File ./ma-ew85s-reference.ps1 -Action SelfTest
```

`SelfTest` runs offline. The script's default action is `Status`, which contacts the configured device; pass the self-test action explicitly when validating without hardware.

## Before submitting a change

- Use [the protocol specification](../MA-EW85S-local-control.md) as the source for encodings, guards, error semantics, and captured fixtures.
- Preserve fresh-state command construction, the single-write rule, separate readback, identity checks, and unrelated settings.
- Run the affected offline suites. Changes to shared protocol behavior should pass both Python and C# regressions.
- Keep `strings.json` and `translations/en.json` in sync when changing entity or flow text.
- Check whitespace with `git diff --check` and review the files included in the change.
- Keep virtual environments, caches, learned device profiles, local IDE folders, and credentials out of commits. Do not reproduce the specification's diagnostic login details in examples or logs.

Live hardware experiments are separate from routine validation and require explicit authorization. These documentation and test commands do not authorize appliance commands or power cycling.

## CI and recorded baseline

[The Python workflow](../.github/workflows/python-tests.yml) installs `.[test,ha-test]` on Ubuntu/Python 3.14 and runs the full Python suite. The C# regression suite is currently run separately.

The recorded 22 October 2026 baseline is **217 Python/HA tests** and **109 C# tests** passing. The Python protocol/packaging subset passed **174 tests** on Windows with three HA modules skipped. Counts describe that validation run; the next test run is the authority for the current checkout.
