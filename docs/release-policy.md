# Release Tag Policy

Tool releases use a tool-scoped SemVer tag:

- `tool/<tool-id>/vX.Y.Z`

Examples:

- `tool/hs2-modpack-analyzer/v1.2.0`
- `tool/windows-timer-tweak/v0.3.1`

Pipelines should resolve `<tool-id>` from the tag and only package the matching tool.

## Hard requirements

`tool-release.yml` enforces these; a violation fails the run before any release is created:

1. `<tool-id>` in the tag must equal the directory name under `tools/` and the
   `toolId` field inside that tool's `tool.manifest.json`.
2. The version comes from the tag itself. Nothing else may declare it: dotnet tools
   receive it as `-p:Version=<ver>`, other kinds use it for the package name only.
3. Release assets are always `<tool-id>-<version>[-win-x64].zip` plus `SHA256SUMS.txt`.
   `-win-x64` is appended for `build.kind = dotnet-msbuild`.

## License tiers and release visibility

`licenseTier` in `tool.manifest.json` declares a tool's distribution license tier:
`free` (unlimited free use), `free-limited` (free with limits), or `paid`. The
validator rejects unknown tiers. Enforcement lives in exactly one place today —
the release channel:

- `free` tools publish a normal public GitHub Release.
- `free-limited` / `paid` tools are built, packaged and uploaded like any other,
  but the release is created with `--draft`: assets exist, the release is
  invisible to the public until a maintainer deliberately converts or moves it.

The tier is metadata everywhere else in the pipeline; this repository does not
build in-tool license checks. If per-tool subscription enforcement is ever added,
the field is already per-tool and ready to drive it.

## Republishing

The workflow refuses to touch an existing release. It does not use `--clobber`, so a
mistaken tag cannot silently overwrite a published binary. To republish, delete the
release first, then the tag, then re-tag — see `release-runbook.md`.

## Known limitation

`hs2-modpack-analyzer` sets `GenerateAssemblyInfo=false` (required by ILRepack with
`Union=true`, otherwise the merged assemblies collide on assembly attributes). As a
result `-p:Version=` does **not** end up in the exe metadata for that tool. Its version
lives in the tag and the package name only.
