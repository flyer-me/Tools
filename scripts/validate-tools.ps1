param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot "tools"

$supportedManifestVersion = "2.1"
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
$seenToolIds = @{}

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
    else {
        # toolId 是 tag、产物命名与索引的主键，重复会让下游静默指错工具
        $toolIdKey = [string]$tool.toolId
        if ($seenToolIds.ContainsKey($toolIdKey)) {
            $errors.Add("$($manifestPath.FullName): duplicate toolId '$toolIdKey' (first declared at $($seenToolIds[$toolIdKey]))")
        }
        else {
            $seenToolIds[$toolIdKey] = (Resolve-Path -LiteralPath $manifestPath.FullName).Path.Substring($repoRoot.Length + 1)
        }
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

    # runtime.entry 描述「最终用户双击什么」，必须是工具目录内的相对路径。
    # 产物存在性只在 build.kind = manual 时静态校验：manual 没有构建步骤，
    # 文件不在就是真的坏了。其余 kind 的产物由构建产出，静态阶段不要求它存在，
    # 否则干净 clone 上 dist 永远不在（**/dist/ 被忽略），校验器必然失败。
    # 产物的存在性断言搬到构建之后：scripts/assert-artifacts.ps1。
    if ([System.IO.Path]::IsPathRooted($runtimeEntry)) {
        $errors.Add("$($manifestPath.FullName): runtime.entry must be a relative path, got '$runtimeEntry'")
        continue
    }

    $runtimeEntryPath = [System.IO.Path]::GetFullPath((Join-Path $toolDir $runtimeEntry))
    $toolDirFull = [System.IO.Path]::GetFullPath($toolDir).TrimEnd('\', '/')
    if (-not $runtimeEntryPath.StartsWith($toolDirFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        $errors.Add("$($manifestPath.FullName): runtime.entry escapes the tool directory '$runtimeEntry'")
        continue
    }

    if ($runtimeKind -eq "exe") {
        $exeExt = [System.IO.Path]::GetExtension($runtimeEntry).ToLowerInvariant()
        if ($exeExt -ne ".exe") {
            $errors.Add("$($manifestPath.FullName): runtime.kind 'exe' requires a .exe entry, got '$runtimeEntry'")
            continue
        }
    }

    if ($runtimeKind -in @("script", "exe") -and $buildKind -eq "manual") {
        if (-not (Test-Path -LiteralPath $runtimeEntryPath -PathType Leaf)) {
            $errors.Add("$($manifestPath.FullName): runtime entry not found '$runtimeEntry' (build.kind 'manual' has no build step to produce it)")
            continue
        }
    }

    if ($buildKind -eq "dotnet-msbuild") {
        if (-not ($tool.build.PSObject.Properties.Name -contains "project") -or [string]::IsNullOrWhiteSpace([string]$tool.build.project)) {
            $errors.Add("$($manifestPath.FullName): build.project is required for build.kind 'dotnet-msbuild'")
        }
        else {
            $projectRel = [string]$tool.build.project
            if ([System.IO.Path]::IsPathRooted($projectRel)) {
                $errors.Add("$($manifestPath.FullName): build.project must be a relative path, got '$projectRel'")
            }
            else {
                $projectPath = [System.IO.Path]::GetFullPath((Join-Path $toolDir $projectRel))
                if (-not $projectPath.StartsWith($toolDirFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $errors.Add("$($manifestPath.FullName): build.project escapes the tool directory '$projectRel'")
                }
                elseif (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
                    $errors.Add("$($manifestPath.FullName): build.project not found '$projectRel'")
                }
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
            elseif ([System.IO.Path]::IsPathRooted([string]$artifact.pattern) -or ([string]$artifact.pattern) -match '(^|[\\/])\.\.([\\/]|$)') {
                $errors.Add("$($manifestPath.FullName): artifact pattern must stay inside the tool directory, got '$($artifact.pattern)'")
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
        entryExists = (Test-Path -LiteralPath $runtimeEntryPath -PathType Leaf)
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
