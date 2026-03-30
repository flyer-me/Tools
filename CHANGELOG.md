# Changelog

All notable changes to this repository will be documented in this file.

## [Unreleased]

### Added
- Added handoff documentation: manifest schema, agent checklist, release runbook, and script risk policy.
- Added new script tool `windows-mpo-stability-fix` to disable MPO by setting `OverlayTestMode=5` and provide restore/status scripts.

### Changed
- Upgraded tool manifest schema to `2.0` with `build/runtime/artifacts` model.
- Removed reliance on legacy `type`-only modeling for CI and validation.
- Extended `scripts/validate-tools.ps1` to enforce schema `2.0` strictly.
- Updated CI workflow to mainstream split pipeline (`validate` -> `build`) with least privilege and dotnet cache.
- Added risky-command warnings for privileged batch operations.

## [0.1.0] - 2026-03-29

### Changed
- Restructured repository into `tools/<tool-id>/` multi-tool layout.
- Migrated a C# tool path to `tools/hs2modpackanalyzer`.
- Promoted standalone `ps1` and `bat` scripts into peer tool directories.
- Added CI manifest discovery workflow and repository governance files.
