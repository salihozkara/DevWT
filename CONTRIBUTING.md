# Contributing

DevWT is a Windows-first networking/runtime project. Changes that affect the hook runtime, installer, service lifecycle, or machine-wide state must be treated as high risk and verified in a VM before they are proposed for daily-machine use.

## Development Setup

Use [docs/development.md](docs/development.md) for toolchain setup, build commands, tests, and VM validation.

## Pull Request Checklist

- Keep process, path, browser, Git, and policy decisions in user mode.
- Keep all listeners localhost-only by default.
- Do not commit generated installer bundles, native build artifacts, certificates, logs, or private lab material.
- Run `dotnet format Devwt.slnx --verify-no-changes --no-restore`.
- Run `dotnet test Devwt.slnx --no-restore`.
- If hook runtime or installer behavior changed, build the installer bundle and validate install/uninstall in a disposable Windows VM.

## Hook Runtime Rules

- Rewrite only loopback bind/connect calls.
- Keep policy decisions in service/CLI code where possible.
- Keep injected code small, deterministic, and fail-open.
- Keep recovery paths documented and reversible.
