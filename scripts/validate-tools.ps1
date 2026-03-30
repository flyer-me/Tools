param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot "tools"

$supportedManifestVersion = "2.0"
$supportedRuntimeKinds = @("script", "exe", "container", "library")
$supportedBuildKinds = @("dotnet-msbuild", "powershell", "batch", "node", "python", "go", "rust", "manual")
$supportedScriptExtensions = @(".ps1", ".bat", ".cmd")

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

    foreach ($field in @("manifestVersion", "toolId", "displayName", "licenseTier", "build", "runtime", "artifacts")) {
        if (-not ($tool.PSObject.Properties.Name -contains $field) -or $null -eq $tool.$field) {
            $errors.Add("$($manifestPath.FullName): missing field '$field'")
        }
    }

    if ($tool.manifestVersion -ne $supportedManifestVersion) {
        $errors.Add("$($manifestPath.FullName): unsupported manifestVersion '$($tool.manifestVersion)' (expected '$supportedManifestVersion')")
        continue
    }

    $buildKind = if ($tool.build.PSObject.Properties.Name -contains "kind") { [string]$tool.build.kind } else { "" }
    $runtimeKind = if ($tool.runtime.PSObject.Properties.Name -contains "kind") { [string]$tool.runtime.kind } else { "" }
    $runtimeEntry = if ($tool.runtime.PSObject.Properties.Name -contains "entry") { [string]$tool.runtime.entry } else { "" }

    if ([string]::IsNullOrWhiteSpace([string]$tool.toolId)) {
        $errors.Add("$($manifestPath.FullName): toolId cannot be empty")
    }
    if ([string]::IsNullOrWhiteSpace([string]$tool.displayName)) {
        $errors.Add("$($manifestPath.FullName): displayName cannot be empty")
    }
    if ([string]::IsNullOrWhiteSpace([string]$tool.licenseTier)) {
        $errors.Add("$($manifestPath.FullName): licenseTier cannot be empty")
    }

    if ($buildKind -notin $supportedBuildKinds) {
        $errors.Add("$($manifestPath.FullName): unsupported build.kind '$buildKind'")
    }

    if ($runtimeKind -notin $supportedRuntimeKinds) {
        $errors.Add("$($manifestPath.FullName): unsupported runtime.kind '$runtimeKind'")
        continue
    }

    if ([string]::IsNullOrWhiteSpace($runtimeEntry)) {
        $errors.Add("$($manifestPath.FullName): runtime.entry is required")
        continue
    }

    $runtimeEntryPath = Join-Path $toolDir $runtimeEntry

    if ($runtimeKind -in @("script", "exe")) {
        if (-not (Test-Path -LiteralPath $runtimeEntryPath -PathType Leaf)) {
            $errors.Add("$($manifestPath.FullName): runtime entry not found '$runtimeEntry'")
            continue
        }
    }

    if ($buildKind -eq "dotnet-msbuild") {
        if (-not ($tool.build.PSObject.Properties.Name -contains "project") -or [string]::IsNullOrWhiteSpace([string]$tool.build.project)) {
            $errors.Add("$($manifestPath.FullName): build.project is required for build.kind 'dotnet-msbuild'")
        }
        else {
            $projectPath = Join-Path $toolDir ([string]$tool.build.project)
            if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
                $errors.Add("$($manifestPath.FullName): build.project not found '$($tool.build.project)'")
            }
        }
    }

    if ($runtimeKind -eq "script") {
        $scriptExt = [System.IO.Path]::GetExtension($runtimeEntryPath).ToLowerInvariant()
        if ($scriptExt -notin $supportedScriptExtensions) {
            $errors.Add("$($manifestPath.FullName): unsupported script extension '$scriptExt'")
            continue
        }

        if ($scriptExt -eq ".ps1") {
            $nullTokens = $null
            $nullAst = $null
            $parseErrors = $null
            [System.Management.Automation.Language.Parser]::ParseFile($runtimeEntryPath, [ref]$nullTokens, [ref]$parseErrors) | Out-Null
            if ($parseErrors.Count -gt 0) {
                $errors.Add("$($manifestPath.FullName): PowerShell parse errors in '$runtimeEntry'")
            }
        }

        if ($scriptExt -in @(".bat", ".cmd")) {
            $lines = Get-Content -Path $runtimeEntryPath
            $commandLines = $lines | Where-Object {
                $line = $_.Trim()
                $line -ne "" -and
                -not $line.StartsWith("::") -and
                -not $line.StartsWith("rem ", [System.StringComparison]::OrdinalIgnoreCase) -and
                -not $line.StartsWith("@rem ", [System.StringComparison]::OrdinalIgnoreCase)
            }
            if (-not $commandLines -or $commandLines.Count -eq 0) {
                $errors.Add("$($manifestPath.FullName): batch entry '$runtimeEntry' has no executable command lines")
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

    if (-not ($tool.artifacts -is [System.Array]) -or $tool.artifacts.Count -eq 0) {
        $errors.Add("$($manifestPath.FullName): artifacts must be a non-empty array")
    }
    else {
        foreach ($artifact in $tool.artifacts) {
            if (-not ($artifact.PSObject.Properties.Name -contains "kind") -or [string]::IsNullOrWhiteSpace([string]$artifact.kind)) {
                $errors.Add("$($manifestPath.FullName): each artifact requires kind")
            }
            if (-not ($artifact.PSObject.Properties.Name -contains "pattern") -or [string]::IsNullOrWhiteSpace([string]$artifact.pattern)) {
                $errors.Add("$($manifestPath.FullName): each artifact requires pattern")
            }
        }
    }

    $index.Add([pscustomobject]@{
        manifestVersion = $tool.manifestVersion
        toolId = $tool.toolId
        licenseTier = $tool.licenseTier
        build = $tool.build
        runtime = $tool.runtime
        artifacts = $tool.artifacts
        manifest = (Resolve-Path -LiteralPath $manifestPath.FullName).Path.Substring($repoRoot.Length + 1)
    })
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    throw "Tool validation failed with $($errors.Count) issue(s)."
}

$outDir = Join-Path $repoRoot "out"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$index | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $outDir "tool-index.json") -Encoding UTF8
Write-Host "Validated $($index.Count) tools with manifest schema $supportedManifestVersion."
