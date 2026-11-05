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

1. Create `tools/<tool-id>/` (kebab-case, matches the `toolId` you are about to declare).
2. Add `tool.manifest.json` with schema `2.1`.
3. Fill `build/runtime/artifacts` and `risk` completely.
4. Add tool `README.md` with run instructions and a `## Risk Notice` section.
5. Run `pwsh -File .\scripts\validate-tools.ps1`.
6. Build the tool, then run `.\scripts\assert-artifacts.ps1` to confirm the declared outputs exist.
7. Record change in `CHANGELOG.md` under `Unreleased`.

The tool list is not maintained anywhere by hand: `validate-tools.ps1` prints the
index and writes `out/tool-index.json`.

## Handoff note format

- Changed tool IDs
- Files touched
- Validation commands run
- Remaining risks
