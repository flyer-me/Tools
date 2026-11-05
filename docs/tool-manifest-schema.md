# Tool Manifest Schema

Current schema version: `2.2`

## Layout

`tools/<tool-id>/tool.manifest.json` — exactly one level deep.

A tool is never grouped. `tools/windows/foo` and `tools/foo/v2` are both rejected by
`validate-tools.ps1`, so the repository cannot slowly regrow a taxonomy that the
validator and the release tag format would then disagree with.

## Required fields

- `manifestVersion`: string, must be `2.1`
- `toolId`: string, kebab-case (`^[a-z0-9]+(-[a-z0-9]+)*$`), must equal the tool directory name
- `displayName`: human-readable name
- `licenseTier`: `free` / `free-limited` / `paid` — the distribution license tier.
  Enforced at exactly one place: the release channel (non-`free` tools release as
  drafts — see `docs/release-policy.md`).
- `risk`: `low` / `elevated` — how much the tool changes the machine (see below)
- `build`: object with `kind`
- `runtime`: object with `kind` and `entry`
- `artifacts`: non-empty array, each item includes `kind` and `pattern`

## Build model

Supported `build.kind` values:

- `dotnet-msbuild`
- `powershell`
- `batch`
- `node`
- `python`
- `go`
- `rust`
- `manual`

For `dotnet-msbuild`, `build.project` is required.
Optional fields:

- `build.configuration` (default in CI: `Release`)
- `build.args` (array of extra arguments)
- `build.script`: build driver for non-dotnet kinds, relative to the tool directory.
  `validate-tools.ps1` requires it to exist for every kind except `dotnet-msbuild`
  and `manual`. `scripts/build-tool.ps1` runs it and nothing else, so packaging
  parameters live in exactly one place and cannot drift between local and CI.
  A `.bat` / `.cmd` driver is invoked with `--no-pause` so an interactive build
  script can still be run by CI; a driver that ignores the argument is fine.
- `build.outputDir`: artifact root, relative to the tool directory. Default `src/dist`.
- `build.verify`: acceptance script run by `scripts/build-tool.ps1` **after a
  successful build** (also for `manual` tools, which have no build step).
  This is the uniform-test-control seam: the governance layer decides *when*
  tests run (after build, before packaging/release, same entry locally and in CI),
  each tool decides *what* its assertions are. Rules: a relative path to a `.ps1`
  inside the tool directory; it must exist (validated statically like
  `build.script`); and it must be **safe to run on a clean CI machine** —
  read-only or offline. Destructive checks belong to manual acceptance, not here.
  A non-zero exit code fails the build. Optional; a tool without `verify` has no
  post-build gate.

`build.kind: manual` means there is no build step; the runtime entry must already
exist in the repository.

## Runtime model

Supported `runtime.kind` values:

- `script`
- `exe`
- `container`
- `library`

`runtime.entry` is always required and describes what the end user launches.

It must be a **relative path inside the tool directory**. Absolute paths and `..`
escapes are rejected. Extension must match the kind: `exe` → `.exe`,
`script` → `.ps1` / `.bat` / `.cmd`.

**File existence is only enforced by `validate-tools.ps1` when `build.kind = manual`.**
For every other kind the entry is produced by a build step, and `artifacts` output
directories are gitignored, so a clean clone legitimately has no `dist/`. Existence is
asserted after the build by `scripts/assert-artifacts.ps1`.

## Risk model

`risk` describes what the tool does to the host; `licenseTier` describes who may use
it. They are separate on purpose — a free tool can still rewrite `bcdedit` settings.

- `low` — reads system state, or writes only inside its own directory.
- `elevated` — needs administrator rights, writes to `HKLM`, changes boot
  configuration, or replaces system binaries.

## Artifacts model

- `pattern` is relative to the tool directory; absolute paths and `..` escapes are rejected.
- After a build, every pattern must match at least one file. `assert-artifacts.ps1` enforces this.
- Directory patterns are allowed where an empty directory is part of the distribution
  (for example a `data/` folder that switches a tool into portable mode).
- Packaging collects files by these patterns only; anything not declared is not shipped.

## Example

```json
{
  "manifestVersion": "2.1",
  "toolId": "hs2-modpack-analyzer",
  "displayName": "HS2 Modpack Analyzer",
  "licenseTier": "free",
  "build": {
    "kind": "dotnet-msbuild",
    "project": "src/HS2ModpackAnalyzer.csproj",
    "configuration": "Release",
    "args": ["-p:NuGetAudit=false"]
  },
  "runtime": {
    "kind": "exe",
    "entry": "src/dist/HS2ModpackAnalyzer.exe"
  },
  "artifacts": [
    {
      "kind": "exe",
      "pattern": "src/dist/*.exe"
    }
  ]
}
```

## Version history

- `2.2`: added optional `build.verify` (uniform test control: tool-specific
  acceptance script, run by the shared build dispatcher after a successful build);
  redefined `licenseTier` as `free` / `free-limited` / `paid` with release-channel
  enforcement (non-`free` tools publish as draft releases).
- `2.1`: `runtime.entry` is a path contract, existence is asserted after the build;
  added `build.script`, `build.outputDir` and `risk`; `toolId` must equal the
  directory name and be kebab-case; a tool must sit exactly at `tools/<tool-id>/`.
- `2.0`: `build` / `runtime` / `artifacts` model, replacing the legacy `type` field.
