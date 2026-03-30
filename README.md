# Tools Source Repository

This repository is a multi-tool source-of-truth.
All tools are stored under `tools/<tool-id>/` and discovered by `tools/*/tool.manifest.json`.

## Tool Index

| Tool ID | Type | Status | Path |
|---|---|---|---|
| `hs2modpackanalyzer` | `dotnet` | Active | `tools/hs2modpackanalyzer` |
| `windows-startmenu-search-slow-fix` | `script` | Active | `tools/windows-startmenu-search-slow-fix` |
| `windows-timer-tweak` | `script` | Active | `tools/windows-timer-tweak` |
| `login-accessibility-cmd-launch` | `script` | Active | `tools/login-accessibility-cmd-launch` |
| `windows-mpo-stability-fix` | `script` | Active | `tools/windows-mpo-stability-fix` |

## Repository Conventions

- This repository stores source code, scripts, docs, and build configuration only.
- Final distribution binaries are produced by CI/CD pipelines, not committed to Git.
- Tool release tags use: `tool/<tool-id>/vX.Y.Z`
- Every tool must include a `tool.manifest.json`.
- Every tool manifest must include `manifestVersion`.

## Quick Validation

```powershell
Get-ChildItem tools -Recurse -Filter tool.manifest.json
```

```powershell
dotnet build .\\tools\\hs2modpackanalyzer\\src\\HS2ModpackAnalyzer.csproj -c Release -p:NuGetAudit=false
```

## Handoff Docs

- `docs/tool-manifest-schema.md`
- `docs/agent-handoff.md`
- `docs/release-runbook.md`
- `docs/script-risk-policy.md`
