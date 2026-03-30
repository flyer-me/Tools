# Changelog

All notable changes to this repository will be documented in this file.

## [Unreleased]

### Added
- Added handoff documentation: manifest schema, agent checklist, release runbook, and script risk policy.
- Added new script tool `windows-mpo-stability-fix` to disable MPO by setting `OverlayTestMode=5` and provide restore/status scripts.

### Changed
- Enforced `manifestVersion` in all tool manifests.
- Extended `scripts/validate-tools.ps1` with manifest-version checks and basic `bat/cmd` validation.
- Added risky-command warnings for privileged batch operations.

## [0.1.0] - 2026-03-29

### Changed
- Restructured repository into `tools/<tool-id>/` multi-tool layout.
- Migrated a C# tool path to `tools/hs2modpackanalyzer`.
- Promoted standalone `ps1` and `bat` scripts into peer tool directories.
- Added CI manifest discovery workflow and repository governance files.
