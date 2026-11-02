# Contributing

## Branch and commit

- Use focused branches and keep commits small.
- Prefer branch naming with `codex/<topic>`.

## Tool requirements

- Every tool must live under `tools/<tool-id>/`.
- Every tool must include `tool.manifest.json` and follow schema `2.0`.
- Use `build/runtime/artifacts` model, not legacy type grouping.
- Keep tool README and scripts in sync with implementation changes.

## Validation before PR

```powershell
powershell -ExecutionPolicy Bypass -File .\\scripts\\validate-tools.ps1
```

If you changed anything under `artifacts` or the build pipeline, also run a build
and assert the declared outputs exist:

```powershell
powershell -ExecutionPolicy Bypass -File .\\scripts\\assert-artifacts.ps1
```
