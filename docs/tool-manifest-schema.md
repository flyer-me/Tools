# Tool Manifest Schema

Current schema version: `1.0`

## Required fields

- `manifestVersion`: string, must be `1.0`
- `toolId`: stable lowercase id, kebab-case
- `displayName`: human-readable name
- `type`: `dotnet` or `script`
- `entry`: path relative to tool directory
- `licenseTier`: `free` or `restricted` (or future approved tiers)

## Optional fields

- `buildCommand`: string; empty for script-only tools

## Example

```json
{
  "manifestVersion": "1.0",
  "toolId": "hs2modpackanalyzer",
  "displayName": "HS2 Modpack Analyzer",
  "type": "dotnet",
  "entry": "src/HS2ModpackAnalyzer.csproj",
  "buildCommand": "dotnet build src/HS2ModpackAnalyzer.csproj -c Release -p:NuGetAudit=false",
  "licenseTier": "free"
}
```
