param(
    [Parameter(Mandatory = $true)]
    [string]$ToolId
)

# 新工具脚手架：一条命令生成"绿色基线"——目录、合法 manifest（schema 2.2、manual、
# low 占位）、README 骨架（含 Risk Notice 模板）、scripts/run.ps1 桩。
# 生成结果立即能过 validate-tools.ps1；把桩与占位字段替换成真实现即可。
# 本脚本是纯生成器：无依赖、不改既有行为、不认识任何具体工具。

$ErrorActionPreference = "Stop"

$toolIdPattern = '^[a-z0-9]+(-[a-z0-9]+)*$'
if ($ToolId -notmatch $toolIdPattern) {
    throw "ToolId must be kebab-case matching $toolIdPattern, got '$ToolId'"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$toolDir = Join-Path $repoRoot "tools\$ToolId"
if (Test-Path -LiteralPath $toolDir) {
    throw "tools/$ToolId already exists"
}

$scriptsDir = Join-Path $toolDir "scripts"
New-Item -ItemType Directory -Path $scriptsDir | Out-Null

$utf8bom = New-Object System.Text.UTF8Encoding $true

$manifest = [ordered]@{
    manifestVersion = "2.2"
    toolId = $ToolId
    displayName = $ToolId
    licenseTier = "free"
    risk = "low"
    build = [ordered]@{
        kind = "manual"
    }
    runtime = [ordered]@{
        kind = "script"
        entry = "scripts/run.ps1"
    }
    artifacts = @(
        [ordered]@{ kind = "script"; pattern = "scripts/*.ps1" }
    )
}
$manifestPath = Join-Path $toolDir "tool.manifest.json"
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), $utf8bom)

$stubScript = @'
# TODO: replace this stub with the real implementation.
Write-Host "stub entry: <tool-id> is not implemented yet."
'@ -replace '<tool-id>', $ToolId
[IO.File]::WriteAllText((Join-Path $scriptsDir "run.ps1"), $stubScript, $utf8bom)

$readmeText = @'
# TODO: <ToolId>

One paragraph: what this tool does, on which Windows versions it applies, and how
to run it.

## Usage

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run.ps1
```

## Risk Notice

<!-- Required when risk = elevated; adjust the manifest accordingly. -->
- What this tool changes:
- What is not reversible:
- How to roll back:
'@ -replace '<ToolId>', $ToolId
[IO.File]::WriteAllText((Join-Path $toolDir "README.md"), $readmeText, $utf8bom)

Write-Host "Created tools/$ToolId (schema 2.2, manual script tool) - green baseline."
Write-Host "Next steps:"
Write-Host "  1. Replace scripts/run.ps1 with the real implementation."
Write-Host "  2. Adjust the manifest fields (displayName / licenseTier / risk / build / runtime / artifacts)."
Write-Host "  3. Run: pwsh -File .\scripts\validate-tools.ps1"
Write-Host "  4. For buildable tools: pwsh -File .\scripts\build-tool.ps1 -ToolId $ToolId"
