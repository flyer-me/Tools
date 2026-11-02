# Release Runbook

## Versioning

- Use SemVer per tool.
- Tag format: `tool/<tool-id>/vX.Y.Z`

## Pre-release steps

1. Update tool README and changelog entries.
2. Run `powershell -ExecutionPolicy Bypass -File .\\scripts\\validate-tools.ps1`.
3. Build the changed tool, then run
   `powershell -ExecutionPolicy Bypass -File .\\scripts\\assert-artifacts.ps1 -ToolId <tool-id>`.
4. Confirm `git status --short` is clean.

## Release steps

1. Commit release metadata updates and push to `master`. Wait for `tools-ci` to pass —
   if it fails, the tag would fail too, just later and with less context.
2. Create the tag: `git tag tool/<tool-id>/vX.Y.Z`.
3. Push it: `git push origin tool/<tool-id>/vX.Y.Z`.
4. `tool-release.yml` resolves the tag, publishes that one tool, asserts its artifacts,
   packages a zip plus `SHA256SUMS.txt`, and creates the GitHub Release.
5. Verify the result:

```powershell
gh run watch
gh release view tool/<tool-id>/vX.Y.Z
```

Expect exactly two assets: the zip and `SHA256SUMS.txt`.

## Post-release

- Record the release summary in `CHANGELOG.md`.
- Verify the downloaded zip before announcing it:

```powershell
gh release download tool/<tool-id>/vX.Y.Z
Get-FileHash .\<tool>-<ver>.zip -Algorithm SHA256
```

The hash must match the corresponding line in `SHA256SUMS.txt`.

## Rollback

Order matters — deleting the tag first and leaving the release behind lets a re-run
recreate it:

```powershell
gh release delete tool/<tool-id>/vX.Y.Z --yes
git push --delete origin tool/<tool-id>/vX.Y.Z
git tag --delete tool/<tool-id>/vX.Y.Z
```

Then fix the issue and re-tag. The workflow refuses to run when a release already
exists, so a half-deleted state fails loudly instead of overwriting silently.
