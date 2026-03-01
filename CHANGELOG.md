# Changelog

## [0.1.0] - 2026-02-28

### Added
- Runner configuration via `appsettings.json` / `appsettings.Development.json` + `AWM_*` env overrides.
- Runtime safety timeouts for agent and tool execution.
- Secure logging helper with payload redaction by default.
- Initial operational README (setup, config, MCP, troubleshooting).
- Timeout behavior test for workflow tool invocation.
- `global.json` to pin .NET SDK patch line.

### Changed
- Runner no longer hardcodes core runtime parameters (model/turns/retry/timeouts).

### Security
- Full request/response payload logs are now masked unless explicitly enabled.
