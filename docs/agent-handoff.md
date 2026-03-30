# Agent Handoff Checklist

Use this checklist when handing work to another agent.

## Preconditions

- `git status --short` is clean or intentionally documented.
- Tool metadata validates:
  - `powershell -ExecutionPolicy Bypass -File .\\scripts\\validate-tools.ps1`
- For .NET tools, build succeeds at least once in Release.

## Scope boundaries

- Tools are peers under `tools/`.
- Do not treat standalone `ps1/bat` tools as sub-assets of other tools.
- Keep `toolId` stable once published.

## Environment requirements

- Windows environment for script tools (`.ps1`, `.bat`, `.cmd`).
- .NET SDK available for `dotnet` tools.
- Elevated privileges may be required by some system-level tools.

## Independent New-Tool Flow

Any agent can independently add a new tool by following this sequence:

1. Create `tools/<tool-id>/` and add tool files.
2. Add `tool.manifest.json` with `manifestVersion: "1.0"`.
3. Add tool `README.md` with run instructions and `Risk Notice`.
4. Run `powershell -ExecutionPolicy Bypass -File .\\scripts\\validate-tools.ps1`.
5. Update root `README.md` tool index.
6. Record change in `CHANGELOG.md` under `Unreleased`.

## Handoff note format

- Changed tool IDs
- Files touched
- Validation commands run
- Remaining risks
