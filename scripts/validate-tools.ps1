param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot "tools"
$supportedManifestVersion = "1.0"

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

    foreach ($field in @("manifestVersion", "toolId", "displayName", "type", "entry", "licenseTier")) {
        if (-not ($tool.PSObject.Properties.Name -contains $field) -or [string]::IsNullOrWhiteSpace([string]$tool.$field)) {
            $errors.Add("$($manifestPath.FullName): missing field '$field'")
        }
    }

    if ($tool.manifestVersion -ne $supportedManifestVersion) {
        $errors.Add("$($manifestPath.FullName): unsupported manifestVersion '$($tool.manifestVersion)' (expected '$supportedManifestVersion')")
        continue
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
        $scriptExt = [System.IO.Path]::GetExtension($entryPath).ToLowerInvariant()
        if ($scriptExt -notin @(".ps1", ".bat", ".cmd")) {
            $errors.Add("$($manifestPath.FullName): unsupported script extension '$scriptExt'")
            continue
        }

        if ($scriptExt -eq ".ps1") {
            $nullTokens = $null
            $nullAst = $null
            $parseErrors = $null
            [System.Management.Automation.Language.Parser]::ParseFile($entryPath, [ref]$nullTokens, [ref]$parseErrors) | Out-Null
            if ($parseErrors.Count -gt 0) {
                $errors.Add("$($manifestPath.FullName): PowerShell parse errors in '$($tool.entry)'")
            }
        }

        if ($scriptExt -in @(".bat", ".cmd")) {
            $lines = Get-Content -Path $entryPath
            $commandLines = $lines | Where-Object {
                $line = $_.Trim()
                $line -ne "" -and
                -not $line.StartsWith("::") -and
                -not $line.StartsWith("rem ", [System.StringComparison]::OrdinalIgnoreCase) -and
                -not $line.StartsWith("@rem ", [System.StringComparison]::OrdinalIgnoreCase)
            }
            if (-not $commandLines -or $commandLines.Count -eq 0) {
                $errors.Add("$($manifestPath.FullName): batch entry '$($tool.entry)' has no executable command lines")
            }

            $riskyPatterns = @(
                "format ",
                "diskpart",
                "reg delete",
                "bcdedit ",
                "move c:\windows\system32\utilman.exe",
                "copy c:\windows\system32\cmd.exe c:\windows\system32\utilman.exe"
            )
            foreach ($line in $commandLines) {
                $normalized = $line.ToLowerInvariant()
                foreach ($pattern in $riskyPatterns) {
                    if ($normalized.Contains($pattern)) {
                        Write-Warning "$($manifestPath.FullName): risky batch command detected '$line'"
                    }
                }
            }
        }
    }

    $index.Add([pscustomobject]@{
        manifestVersion = $tool.manifestVersion
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
