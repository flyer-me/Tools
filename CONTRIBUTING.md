# Contributing

## Branch and commit

- Use focused branches and keep commits small.
- Prefer branch naming with `codex/<topic>`.

## Tool requirements

- Every tool must live directly under `tools/<tool-id>/`. No grouping directories —
  the validator rejects nested manifests.
- The directory name is the `toolId`. It must be kebab-case and must match the
  `toolId` field inside `tool.manifest.json`.
- Every tool must include `tool.manifest.json` and follow schema `2.2`.
- Declare `risk`: `low` if the tool reads state or writes inside its own directory,
  `elevated` if it needs admin, writes `HKLM`, changes boot configuration, or replaces
  system binaries. `elevated` tools need a `## Risk Notice` section in their README.
- Use the `build/runtime/artifacts` model, not legacy type grouping.
- Keep tool README and scripts in sync with implementation changes.

## Validation before PR

```powershell
pwsh -File .\scripts\validate-tools.ps1
```

If you changed anything under `artifacts` or the build pipeline, also build and
assert the declared outputs exist:

```powershell
pwsh -File .\scripts\build-tool.ps1 -ToolId <tool-id>
pwsh -File .\scripts\assert-artifacts.ps1 -ToolId <tool-id>
```

`validate` proves the manifest is legal; it cannot prove the build produces what the
manifest promises. That is what `assert-artifacts` is for — a green build with an
empty `dist/` is the failure mode this pair exists to catch.
