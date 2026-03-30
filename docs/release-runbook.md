# Release Runbook

## Versioning

- Use SemVer per tool.
- Tag format: `tool/<tool-id>/vX.Y.Z`

## Pre-release steps

1. Update tool README and changelog entries.
2. Run `powershell -ExecutionPolicy Bypass -File .\\scripts\\validate-tools.ps1`.
3. Build changed `dotnet` tools in Release.
4. Confirm `git status --short` is clean.

## Release steps

1. Commit release metadata updates.
2. Create tag `tool/<tool-id>/vX.Y.Z`.
3. Push commit and tag.
4. Verify CI artifact outputs.

## Post-release

- Record release summary in `CHANGELOG.md`.
- Capture rollback notes if release is reverted.
