# Changelog

All notable changes to this repository will be documented in this file.

## [Unreleased]

### Added
- Schema `2.2`: optional `build.verify` — a tool-specific acceptance script that `build-tool.ps1` runs after a successful build (also for `manual` tools). Uniform test control: the governance layer decides when tests run, each tool decides what its assertions are; both CI workflows get the gate with zero workflow changes. `windows-disk-write-tracker` now declares `tools/verify.ps1` (114 assertions) as its gate.
- `licenseTier` redefined as a three-tier distribution license: `free` / `free-limited` / `paid` (previously `free` / `restricted`, unenforced). The validator rejects unknown tiers; `tool-release.yml` publishes non-`free` tools as draft releases (assets uploaded, invisible to the public). `login-accessibility-cmd-launch` migrated from `restricted` to `free-limited`.
- Added `scripts/new-tool.ps1`: scaffolds `tools/<tool-id>/` — schema-2.2 manifest, README skeleton with a Risk Notice template, and a stub entry script — so a new tool starts from a validating baseline with one command.
- Added `build.kind: binary` for closed-source / externally provisioned binaries: optional `build.script` provisioning (download or copy into `dist/`), entry and artifacts asserted only after provisioning, binaries never enter git. Delivery channels documented in `docs/release-policy.md` (draft release + tier gating / private mirror repo / signed-URL storage).
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
- Publish-property ownership for `windows-disk-write-tracker`: `RuntimeIdentifier` and `SelfContained` now live in the project file (completing the migration the changelog announced earlier); the manifest `build.args` keeps only `-o dist`, which is the artifact-location contract the `artifacts` patterns reference. Output verified identical (48 files, same entry-shell size) with the full 114-assertion gate green.
- Removed the hand-maintained tool index from the root README. The tool list is now generated only by `validate-tools.ps1` (console overview plus `out/tool-index.json`); no document keeps a manual tool list.
- `windows-disk-write-tracker` `tools/verify.ps1` section 4 no longer runs `report` against the real `dist\data`: it copies the fixtures into a temp data directory first. Same assertions, but hermetic — a clean CI machine no longer fails for lacking user data.
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
- Updated CI workflow to mainstream split pipeline (`validate` -> `build`) with least privilege.
- Disabled `setup-dotnet` package caching: it requires `packages.lock.json`, and this repo restores without a lock file, so the step failed before anything was built. Caching comes back when lock files do.
- `windows-disk-write-tracker` now targets `net10.0-windows`; CI installs the .NET 10 SDK.
- `windows-disk-write-tracker` ships as a self-contained multi-file layout (~21 MB, was a 35 MB single file): the manifest declares the runtime DLLs and config files so the packaged zip stays runnable.
- `windows-disk-write-tracker` data layout: preferences moved to `data/config/settings.json`, logs are pruned by age and rolled at 1 MB, and a windowed read opens only the files inside the window plus one predecessor per drive.
- Added risky-command warnings for privileged batch operations.

### Removed
- Removed committed binaries from the repository: `windows-disk-write-tracker/src/dist/DiskWriteTracker.exe` (36 MB) and `hs2-missing-mods-downloader/dist/download_missing_mods.exe` (9.4 MB) are build output now.
- Removed the `tools/ModpackManager/` grouping layer; its design notes moved to `docs/backlog/modpackmanager/`.
- Removed `history`, `export --all`, and the `daily.json` rebuild path from `windows-disk-write-tracker`: the real usage log showed none of them were ever invoked.
- Removed `windows-disk-write-tracker/docs/spike-sqlite.md`, a measurement report for a database option that was not adopted.
- Removed local build residue that had leaked into the tree (`bin/`, `obj/`, `.vs/`, `.csproj.user`) and a hardcoded HS2 install path in the deploy script.

## [0.1.0] - 2026-03-29

### Changed
- Restructured repository into `tools/<tool-id>/` multi-tool layout.
- Migrated a C# tool path to `tools/hs2modpackanalyzer` (now `hs2-modpack-analyzer`).
- Promoted standalone `ps1` and `bat` scripts into peer tool directories.
- Added CI manifest discovery workflow and repository governance files.
