# Disk Write Tracker

Reads the cumulative bytes-written counter from drive firmware, records one sample per day,
and produces a self-contained offline HTML report plus CSV export.

## Purpose

Total bytes written is the number that actually predicts SSD endurance, but Windows does not
expose it through any built-in UI. This tool reads it directly from the storage stack:

- No SMART utility dependency, no NuGet packages, no network access.
- Read-only: it never writes to drive firmware, partitions, or the registry.
- Portable: the data directory lives next to the executable, so the whole folder can move.

## How it reads

| Drive | Call | Value |
|---|---|---|
| NVMe | `IOCTL_STORAGE_QUERY_PROPERTY`, log page `0x02` | `Data Units Written` x 512000 bytes |
| NVMe identity | same IOCTL, `DataType=Identify` | real serial number, not the synthetic `0000_0000_...` string |
| SATA | `IOCTL_STORAGE_PREDICT_FAILURE`, fallback `SMART_RCV_DRIVE_DATA` | SMART attribute 246 / 241 / 233 x logical sector size |

Neither path requires elevation. A drive that cannot be read (for example one behind a RAID
controller) is marked `missing` rather than estimated.

SATA unit selection is cross-checked by wear consistency: cumulative bytes divided by used
life percentage must land in a plausible implied-TBW range. If the firmware reports bytes
instead of sectors the implied value misses by more than 500x, so the wrong unit is rejected
instead of silently producing an off-by-512 number.

## Usage

Double-click the executable for an 8-item menu, or call it directly:

```powershell
.\src\dist\DiskWriteTracker.exe sample
.\src\dist\DiskWriteTracker.exe history --days 30
.\src\dist\DiskWriteTracker.exe daily show --days 30
.\src\dist\DiskWriteTracker.exe report --days 90
.\src\dist\DiskWriteTracker.exe export csv --days 90      # --all 导出全部历史
.\src\dist\DiskWriteTracker.exe schedule install --time 00:05
```

`history` prints everything unless `--days` is given. `export csv` follows `--days` for both
files; `--all` writes every sample instead.

Add `--json` for machine-readable output. `--data-dir <path>` overrides the data directory.

Exit codes: `0` ok / `1` no physical disk / `2` bad arguments / `4` no data / `5` runtime
error / `7` scheduler failure.

Double-clicking opens the menu. When stdin or stdout is redirected the tool prints help and
exits instead, so scripts, pipelines, and the scheduled task never block on a keypress.

## Build

Requires the .NET 8 SDK on x64 Windows.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\bootstrap-sdk.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

`build.ps1` runs restore, build, publish, smoke checks, and the 87-assertion verification
suite. Output is a single self-contained file at `src/dist/DiskWriteTracker.exe` (~35 MB),
with `PublishSingleFile`, `SelfContained`, `PublishReadyToRun`, and compression enabled.

Publishing is overlay-based: `src/dist/data` is never deleted, but leftover files from older
layouts are removed by name and the build fails if `src/dist` contains anything other than
`DiskWriteTracker.exe`, `README.txt`, and `data`.

## Layout

| Path | Content |
|---|---|
| `src/DiskWriteTracker.Core/` | IOCTL interop, disk discovery, sampling, unit arbitration, daily aggregation, JSONL storage, export |
| `src/DiskWriteTracker.Cli/` | CLI entry point, argument parsing, interactive menu |
| `src/dist/` | Publish output (gitignored) |
| `config/default.json` | Default configuration, embedded into the assembly at build time |
| `scripts/` | Build, verification, SDK bootstrap |
| `docs/` | Design notes and the manual acceptance checklist |
| `assets/README.txt` | End-user readme copied into `src/dist/` on publish |
| `tests/fixtures/data/` | Multi-day samples covering baseline, normal, counter-reset, and gap boundaries |

## Validation

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1    # build + verify
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1   # assertions only
```

Automated coverage: distribution shape, no-arg non-blocking behavior, CLI contract, the four
daily-delta edge cases, offline report self-containment, fixed naming and archiving, CSV
encoding, 8-process concurrent write locking, portable directory resolution, scheduled task
dry-run, and behavior with no data.

`docs/verify-manual.md` lists the 13 checks that need a real console and a human eye,
including the UAC elevation path. Code-level exception handling is not a substitute for
running it once and recording the result.

## Risk Notice

- `schedule install` registers a daily scheduled task and therefore triggers UAC. Normal
  sampling, reporting, and export do not require elevation.
- The tool issues storage IOCTLs only. It does not modify firmware, partitions, or registry
  state beyond the scheduled task it creates.
- Sample data stays in the data directory next to the executable. Nothing is uploaded.
- Reported cumulative writes are host-side writes; they exclude write amplification and are
  not equal to NAND programming volume.
- A drive may occasionally fail to answer while another process is polling it. That sample is
  recorded as `missing` and is not back-filled; the daily delta catches up on the next valid day.
