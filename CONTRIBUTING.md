# Contributing

## Branch and commit

- Use focused branches and keep commits small.
- Prefer branch naming with `codex/<topic>`.

## Tool requirements

- Every tool must live under `tools/<tool-id>/`.
- Every tool must include `tool.manifest.json`.
- Keep tool README and scripts in sync with implementation changes.

## Validation before PR

```powershell
powershell -ExecutionPolicy Bypass -File .\\scripts\\validate-tools.ps1
```
