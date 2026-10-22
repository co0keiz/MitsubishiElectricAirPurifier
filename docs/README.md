# Documentation

This project provides direct LAN control of the Mitsubishi Electric MA-EW85S-E on firmware `80.00`. Choose the interface that fits your application; the Home Assistant integration, API, and PowerShell reference can each operate independently.

## Installation and use

| Guide | Contents |
| --- | --- |
| [Project README](../README.md) | Supported features, API examples, PowerShell commands, and verification history |
| [Home Assistant](../custom_components/ma_ew85s_local/README.md) | HACS/manual installation, entities, actions, address changes, and diagnostics |
| [HTTP API](../src/AirPurifier.Api/README.md) | Startup, configuration, API keys, endpoints, and error responses |
| [C# client](../src/AirPurifier.Core/README.md) | Client construction, methods, state, and errors |
| [Python client](../custom_components/ma_ew85s_local/protocol/README.md) | Async usage, session ownership, methods, state, and errors |
| [Troubleshooting](troubleshooting.md) | Connectivity, identity, firmware, unknown states, and uncertain commands |

## Development and protocol

- [Development guide](development.md): prerequisites, environment setup, offline test suites, and change checks.
- [Architecture](architecture.md): component boundaries, command confirmation, concurrency, and Home Assistant state updates.
- [Protocol specification](../MA-EW85S-local-control.md): authoritative transport, encryption, frame definitions, captured vectors, and evidence.
- [Acceptance checklist](../MA-EW85S-local-control.md#12-acceptance-test-checklist): behavior that implementations must preserve.

The C# and PowerShell implementations have live evidence recorded in the specification. Python/Home Assistant validation uses captured fixtures and a simulated encrypted purifier. An offline test pass does not establish support for another model or firmware.
