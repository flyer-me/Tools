# Tools Source Repository

This repository is the source of truth for a set of small Windows tools.
Every tool is exactly one directory deep: `tools/<tool-id>/`, and each one carries
a `tool.manifest.json` at its root. Nothing is nested, nothing is grouped.

Source, scripts, docs and build configuration live here. Distribution binaries are
produced by CI and attached to releases — they are not committed.

## Enumerating tools

The tool list is generated, never hand-maintained. A manual index in any document
would drift; the validator is the single source — it prints a per-tool overview and
writes `out/tool-index.json`:

```powershell
pwsh -File .\scripts\validate-tools.ps1
```

`risk` is not `licenseTier`. `low` tools read system state or write inside their own
directory. `elevated` tools need administrator rights, write to `HKLM`, change boot
configuration, or replace system binaries — read the tool's `## Risk Notice` before
running one.

## Repository Conventions

- A tool is a directory directly under `tools/`. The directory name **is** the
  `toolId`, and `toolId` must be kebab-case. Both rules are enforced by the
  validator, not by this document.
- Every tool must include a `tool.manifest.json` following schema `2.1`.
- Release tags use: `tool/<tool-id>/vX.Y.Z`. One tag releases exactly one tool.
- `dist/` directories are build output and are gitignored, with one exception:
  hand-maintained launcher scripts under `dist/` stay in Git.

## Quick Validation

Static check — no SDK needed, runs on a clean clone:

```powershell
pwsh -File .\scripts\validate-tools.ps1
```

Build every tool, then assert every declared artifact actually exists:

```powershell
pwsh -File .\scripts\build-tool.ps1
pwsh -File .\scripts\assert-artifacts.ps1
```

Build a single tool (same code path CI uses):

```powershell
pwsh -File .\scripts\build-tool.ps1 -ToolId windows-disk-write-tracker
```

Package everything into `out/packages` (zips + `SHA256SUMS.txt`):

```powershell
pwsh -File .\scripts\package-tool.ps1 -Version 0.0.0-local
```

## Handoff Docs

- `docs/tool-manifest-schema.md` — what a manifest may contain
- `docs/release-policy.md` — versioning, tag rules, what may ship
- `docs/release-runbook.md` — step-by-step release and rollback
- `docs/script-risk-policy.md` — how risky scripts are classified
- `docs/agent-handoff.md` — context for whoever picks this up next
- `docs/backlog/` — ideas that are not tools (kept out of `tools/` on purpose)
