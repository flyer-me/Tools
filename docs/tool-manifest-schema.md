# Tool Manifest Schema

Current schema version: `2.0`

## Required fields

- `manifestVersion`: string, must be `2.0`
- `toolId`: stable lowercase id, kebab-case
- `displayName`: human-readable name
- `licenseTier`: `free` / `restricted` / future approved tiers
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

## Runtime model

Supported `runtime.kind` values:

- `script`
- `exe`
- `container`
- `library`

`runtime.entry` is always required.

## Example

```json
{
  "manifestVersion": "2.0",
  "toolId": "hs2modpackanalyzer",
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
