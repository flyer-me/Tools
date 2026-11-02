# HS2 Modpack Analyzer

## Purpose

Analyze HS2 sideloader modpack usage and report used/unused/invalid zipmods.

## Structure

- `src/`: .NET source and local runtime dependencies
- `scripts/`: operational helper scripts
- `docs/`: integration and usage notes

## Build

```powershell
dotnet build .\\src\\HS2ModpackAnalyzer.csproj -c Release -p:NuGetAudit=false
```

## Run

```powershell
dotnet run --project .\\src\\HS2ModpackAnalyzer.csproj -- --help
```

## Risk Notice

Read-only with respect to the game installation: the tool walks the modpack
directory, reads archive metadata, and writes a report. It does not modify, move, or
delete modpack files. No administrator rights and no registry access are required.

## Deploy single-file build to HS2 directory

```powershell
powershell -ExecutionPolicy Bypass -File .\\scripts\\deploy-to-hs2.ps1 -Hs2InstallDir "D:\\ProgramData\\HS2_DXBR_R16"
```
