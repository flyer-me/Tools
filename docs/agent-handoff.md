# Agent Handoff Checklist

Use this checklist when handing work to another agent.

## Preconditions

- `git status --short` is clean or intentionally documented.
- Tool metadata validates:
  - `powershell -ExecutionPolicy Bypass -File .\\scripts\\validate-tools.ps1`
- For non-`manual` tools, declared artifacts exist after a build:
  - `powershell -ExecutionPolicy Bypass -File .\\scripts\\assert-artifacts.ps1`
- For `build.kind = dotnet-msbuild`, build succeeds at least once in Release.

## Scope boundaries

- Tools are peers under `tools/`.
- Do not treat standalone scripts as sub-assets of other tools.
- Keep `toolId` stable once published.

## Environment requirements

- Windows environment for current script tools (`.ps1`, `.bat`, `.cmd`).
- .NET SDK available for current dotnet tools.
- Elevated privileges may be required by some system-level tools.

## Independent New-Tool Flow

1. Create `tools/<tool-id>/` and add tool files.
2. Add `tool.manifest.json` with schema `2.1`.
3. Fill `build/runtime/artifacts` fields completely.
4. Add tool `README.md` with run instructions and `Risk Notice`.
5. Run `powershell -ExecutionPolicy Bypass -File .\\scripts\\validate-tools.ps1`.
6. Build the tool, then run `.\\scripts\\assert-artifacts.ps1` to confirm the declared outputs exist.
7. Update root `README.md` tool index.
8. Record change in `CHANGELOG.md` under `Unreleased`.

## Handoff note format

- Changed tool IDs
- Files touched
- Validation commands run
- Remaining risks
