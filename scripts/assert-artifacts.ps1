param(
    [string[]]$ToolId = @()
)

# 构建后断言：manifest 声明的产物必须真的存在。
# 静态校验（validate-tools.ps1）只保证路径契约合法，因为干净 clone 上
# dist 目录本来就不存在。产物存在性只能等构建之后才验得了。

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot "tools"

# 枚举口径与 validate-tools.ps1 一致：只看 tools/ 下一级目录里的 manifest。
$manifests = @(Get-ChildItem -Path $toolsRoot -Directory | Sort-Object Name | ForEach-Object {
        $candidate = Join-Path $_.FullName "tool.manifest.json"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { Get-Item -LiteralPath $candidate }
    })
if ($manifests.Count -eq 0) {
    throw "No tool manifests found under $toolsRoot"
}

$failures = New-Object System.Collections.Generic.List[string]
$checked = 0

foreach ($manifestPath in $manifests) {
    $toolDir = Split-Path -Parent $manifestPath.FullName
    $tool = Get-Content -Path $manifestPath.FullName -Raw | ConvertFrom-Json

    $id = [string]$tool.toolId
    if ($ToolId.Count -gt 0 -and $ToolId -notcontains $id) { continue }

    $checked++
    $buildKind = if ($tool.build.PSObject.Properties.Name -contains "kind") { [string]$tool.build.kind } else { "" }
    $entry = if ($tool.runtime.PSObject.Properties.Name -contains "entry") { [string]$tool.runtime.entry } else { "" }

    # manual 的 entry 在 validate 阶段已经要求存在，这里只补齐非 manual 的。
    if ($buildKind -ne "manual" -and -not [string]::IsNullOrWhiteSpace($entry)) {
        $entryPath = Join-Path $toolDir $entry
        if (-not (Test-Path -LiteralPath $entryPath -PathType Leaf)) {
            $failures.Add(("{0}: runtime.entry not found '{1}' - run the build first" -f $id, $entry))
        }
    }

    foreach ($artifact in $tool.artifacts) {
        $pattern = [string]$artifact.pattern
        if ([string]::IsNullOrWhiteSpace($pattern)) { continue }

        $full = Join-Path $toolDir $pattern
        $hits = @(Get-Item -Path $full -ErrorAction SilentlyContinue)
        if ($hits.Count -eq 0) {
            $failures.Add(("{0}: artifact pattern '{1}' matched 0 files" -f $id, $pattern))
        }
    }
}

if ($ToolId.Count -gt 0 -and $checked -eq 0) {
    throw "No tool found with toolId(s) '$($ToolId -join ', ')'"
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Host "  [FAIL] $failure" -ForegroundColor Red
    }
    throw "Artifact assertion failed with $($failures.Count) issue(s)."
}

Write-Host ("Artifact assertion passed for {0} tool(s)." -f $checked)
