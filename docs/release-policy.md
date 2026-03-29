# Release Tag Policy

Tool releases use a tool-scoped SemVer tag:

- `tool/<tool-id>/vX.Y.Z`

Examples:

- `tool/hs2modpackanalyzer/v1.2.0`
- `tool/windows-timer-tweak/v0.3.1`

Pipelines should resolve `<tool-id>` from the tag and only package the matching tool.
