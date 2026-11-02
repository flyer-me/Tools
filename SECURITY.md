# Security Policy

## Supported versions

Only the latest tagged release of each tool is supported. Every tool has its own
version line; there is no repository-wide version.

Releases are published at https://github.com/<owner>/<repo>/releases with tags of the
form `tool/<tool-id>/vX.Y.Z`. A tool with no tag yet has no supported release.

## Verifying a download

Each release ships `<tool-id>-<version>[-win-x64].zip` and `SHA256SUMS.txt`.
Check the hash before running anything:

```powershell
Get-FileHash .\<package>.zip -Algorithm SHA256
```

## Reporting

Report security issues privately to repository maintainers before public disclosure.
Do not include exploit details in public issues.
