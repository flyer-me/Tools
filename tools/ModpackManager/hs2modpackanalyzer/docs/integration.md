# Integration Notes

## Responsibilities

This tool scans configured HS2 user data folders, extracts required sideloader GUIDs,
and compares them with target modpack zipmods.

## Outputs

- Text report (`ModpackAnalysisReport.txt` by default)
- Zipmod size statistics (`Used`, `Unused`, `Invalid`)
- Optional unused zipmod move operation when `--move-unused` is enabled

## Build artifact

Release build generates a single-file executable:

- `src/dist/HS2ModpackAnalyzer.exe`
