# Agent Handoff Checklist

Use this checklist when handing work to another agent.

## Preconditions

- `git status --short` is clean or intentionally documented.
- Tool metadata validates:
  - `pwsh -File .\scripts\validate-tools.ps1`
- Everything the manifests promise actually exists after a build:
  - `pwsh -File .\scripts\build-tool.ps1`
  - `pwsh -File .\scripts\assert-artifacts.ps1`
- For `build.kind = dotnet-msbuild`, build succeeds at least once in Release.

## Scope boundaries

- Tools are peers under `tools/`, one level deep. Grouping directories are rejected.
- Do not treat standalone scripts as sub-assets of other tools.
- Keep `toolId` stable once published — it is the directory name, the tag segment, and
  the package name at the same time.

## Environment requirements

- Windows environment for current script tools (`.ps1`, `.bat`, `.cmd`).
- A .NET SDK for dotnet tools. `build-tool.ps1` probes for one and says so plainly
  when it cannot find any; set `DOTNET_EXE` to pin a specific host.
- Python 3.8+ with PyInstaller for `build.kind = python`.
- Elevated privileges may be required by some system-level tools — check `risk`.

## Independent New-Tool Flow

1. `pwsh -File .\scripts\new-tool.ps1 -ToolId <tool-id>` — scaffolds the directory,
   a schema-2.2 manifest, a README skeleton and a stub entry script. The baseline
   validates immediately.
2. Replace the stub (`scripts/run.ps1`) and fill in the manifest fields
   (`displayName` / `licenseTier` / `risk` / `build` / `runtime` / `artifacts`).
3. Run `pwsh -File .\scripts\validate-tools.ps1`.
4. For buildable tools: `pwsh -File .\scripts\build-tool.ps1 -ToolId <tool-id>`,
   then `.\scripts\assert-artifacts.ps1 -ToolId <tool-id>`.
5. Record change in `CHANGELOG.md` under `Unreleased`.

The tool list is not maintained anywhere by hand: `validate-tools.ps1` prints the
index and writes `out/tool-index.json`.

## Handoff note format

- Changed tool IDs
- Files touched
- Validation commands run
- Remaining risks
