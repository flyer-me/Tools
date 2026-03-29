param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot "tools"

if (-not (Test-Path $toolsRoot -PathType Container)) {
    throw "Missing tools directory: $toolsRoot"
}

$manifests = Get-ChildItem -Path $toolsRoot -Recurse -File -Filter "tool.manifest.json" | Sort-Object FullName
if (-not $manifests) {
    throw "No tool manifests found under $toolsRoot"
}

$errors = New-Object System.Collections.Generic.List[string]
$index = New-Object System.Collections.Generic.List[object]

foreach ($manifestPath in $manifests) {
    $toolDir = Split-Path -Parent $manifestPath.FullName
    $tool = Get-Content -Path $manifestPath.FullName -Raw | ConvertFrom-Json

    foreach ($field in @("toolId", "displayName", "type", "entry", "licenseTier")) {
        if (-not ($tool.PSObject.Properties.Name -contains $field) -or [string]::IsNullOrWhiteSpace([string]$tool.$field)) {
            $errors.Add("$($manifestPath.FullName): missing field '$field'")
        }
    }

    if ($tool.type -notin @("dotnet", "script")) {
        $errors.Add("$($manifestPath.FullName): unsupported type '$($tool.type)'")
        continue
    }

    $entryPath = Join-Path $toolDir $tool.entry
    if (-not (Test-Path -LiteralPath $entryPath -PathType Leaf)) {
        $errors.Add("$($manifestPath.FullName): entry not found '$($tool.entry)'")
        continue
    }

    if ($tool.type -eq "script") {
        if ($entryPath.EndsWith(".ps1", [System.StringComparison]::OrdinalIgnoreCase)) {
            $nullTokens = $null
            $nullAst = $null
            $parseErrors = $null
            [System.Management.Automation.Language.Parser]::ParseFile($entryPath, [ref]$nullTokens, [ref]$parseErrors) | Out-Null
            if ($parseErrors.Count -gt 0) {
                $errors.Add("$($manifestPath.FullName): PowerShell parse errors in '$($tool.entry)'")
            }
        }
    }

    $index.Add([pscustomobject]@{
        toolId = $tool.toolId
        type = $tool.type
        manifest = (Resolve-Path -LiteralPath $manifestPath.FullName).Path.Substring($repoRoot.Length + 1)
        entry = $tool.entry
    })
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    throw "Tool validation failed with $($errors.Count) issue(s)."
}

$outDir = Join-Path $repoRoot "out"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$index | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $outDir "tool-index.json") -Encoding UTF8
Write-Host "Validated $($index.Count) tools."
