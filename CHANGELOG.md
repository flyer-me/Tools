# Changelog

All notable changes to this repository will be documented in this file.

## [Unreleased]

### Added
- Added `risk` to every manifest: `low` reads state or writes inside its own directory, `elevated` needs admin / writes `HKLM` / changes boot configuration / replaces system binaries. `licenseTier` no longer has to carry that meaning.
- The validator now cross-checks the claim: a `low` tool that touches `HKLM`, `bcdedit`, `diskpart`, `utilman.exe`, `takeown`, `icacls` or `sc config` fails, and an `elevated` tool without a `## Risk Notice` in its README fails.
- Added `hs2-missing-mods-downloader` as the eighth tool (Python source, PyInstaller build).
- Added `scripts/build-tool.ps1`: the single build dispatcher driven by manifests, shared by local runs and CI.
- Added `scripts/assert-artifacts.ps1`: asserts after a build that every declared artifact really exists.
- Added `scripts/package-tool.ps1`: collects artifacts per manifest, writes zips plus `SHA256SUMS.txt`, and preserves empty directories.
- Added `.github/workflows/tool-release.yml`: `tool/<tool-id>/vX.Y.Z` tags release exactly one tool.
- Added `windows-disk-write-tracker`: reads cumulative bytes written from NVMe/SATA firmware through storage IOCTLs, records a daily delta, and exports an offline HTML report plus CSV.
- Added handoff documentation: manifest schema, agent checklist, release runbook, and script risk policy.
- Added new script tool `windows-mpo-stability-fix` to disable MPO by setting `OverlayTestMode=5` and provide restore/status scripts.
- Added `windows-remove-org-restrictions` to the index; it existed on disk but was never documented.

### Changed
- Tool layout is now strictly flat: `tools/<tool-id>/tool.manifest.json`, exactly one level. The validator rejects nested manifests, requires `toolId` to equal the directory name, and requires kebab-case.
- Renamed `hs2modpackanalyzer` to `hs2-modpack-analyzer` so ids read the same way across the repo.
- Upgraded tool manifest schema to `2.1`: `runtime.entry` is a path contract rather than a "must already exist" requirement, and `build.script` / `build.outputDir` describe non-dotnet builds.
- `validate-tools.ps1` now reports every problem at once instead of aborting on the first one.
- `build-tool.ps1` picks a `dotnet.exe` that actually has an SDK, so a runtime-only host no longer fails with a misleading message deep inside MSBuild.
- Moved single-file publish properties (RID, self-contained, compression, ReadyToRun) into the project files; a bare `dotnet publish` now produces the shipped artifact.
- CI builds through `build-tool.ps1` and fails loudly when artifacts are missing, instead of uploading an empty artifact directory.
- `.gitignore` ignores `**/dist/**` but explicitly keeps hand-maintained launchers (`!**/dist/*.bat`); `.vs` is ignored at any depth.
- `.gitattributes` marks `exe/dll/zip/pdb` as binary so line-ending normalisation and text diffs stay away from them.
- `.editorconfig` now says 4-space indent for scripts, which is what they actually use; the previous `tab` rule was fiction.
- `CODEOWNERS` names a real owner instead of `tools@example.com`.
- Fixed stale `tools\` paths in the disk-write-tracker docs and scripts: the scripts live in `scripts\` and output in `src\dist\` since the layout move.
- Extended `scripts/validate-tools.ps1` to enforce the schema strictly.
- Updated CI workflow to mainstream split pipeline (`validate` -> `build`) with least privilege and dotnet cache.
- Added risky-command warnings for privileged batch operations.

### Removed
- Removed committed binaries from the repository: `windows-disk-write-tracker/src/dist/DiskWriteTracker.exe` (36 MB) and `hs2-missing-mods-downloader/dist/download_missing_mods.exe` (9.4 MB) are build output now.
- Removed the `tools/ModpackManager/` grouping layer; its design notes moved to `docs/backlog/modpackmanager/`.
- Removed local build residue that had leaked into the tree (`bin/`, `obj/`, `.vs/`, `.csproj.user`) and a hardcoded HS2 install path in the deploy script.

## [0.1.0] - 2026-03-29

### Changed
- Restructured repository into `tools/<tool-id>/` multi-tool layout.
- Migrated a C# tool path to `tools/hs2modpackanalyzer` (now `hs2-modpack-analyzer`).
- Promoted standalone `ps1` and `bat` scripts into peer tool directories.
- Added CI manifest discovery workflow and repository governance files.
