param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot "tools"

$supportedManifestVersion = "2.1"
$supportedRuntimeKinds = @("script", "exe", "container", "library")
$supportedBuildKinds = @("dotnet-msbuild", "powershell", "batch", "node", "python", "go", "rust", "manual")
$supportedScriptExtensions = @(".ps1", ".bat", ".cmd")
# licenseTier 说的是授权，risk 说的是这台机器被改动的程度，两者不能互相顶替。
$supportedRiskLevels = @("low", "elevated")

# "这台机器被改了"的强信号，不是完整的危险命令库。
# 刻意不收 format / Remove-Item 这类词：它们在没有上下文时误报太多，
# 一个总在报警的检查很快就会被无视。
$riskyCommandPatterns = @(
    "hklm:",
    "hkey_local_machine",
    "bcdedit",
    "reg add ",
    "reg delete",
    "diskpart",
    "utilman.exe",
    "takeown ",
    "icacls ",
    "sc config ",
    "shutdown /r"
)

if (-not (Test-Path $toolsRoot -PathType Container)) {
    throw "Missing tools directory: $toolsRoot"
}

$manifestName = "tool.manifest.json"
$toolDirs = @(Get-ChildItem -Path $toolsRoot -Directory | Sort-Object Name)
if ($toolDirs.Count -eq 0) {
    throw "No tool directories found under $toolsRoot"
}

$errors = New-Object System.Collections.Generic.List[string]
$index = New-Object System.Collections.Generic.List[object]
$seenToolIds = @{}
$toolIdPattern = '^[a-z0-9]+(-[a-z0-9]+)*$'

# 平级是硬约束：tools/<toolId>/tool.manifest.json，深度恰好一层。
# 只靠文档写"别嵌套"守不住，违规必须在校验阶段就红。
$strayManifests = @(Get-ChildItem -Path $toolsRoot -Recurse -File -Filter $manifestName | Where-Object {
        $relative = $_.FullName.Substring($toolsRoot.Length + 1)
        $depth = @($relative.ToCharArray() | Where-Object { $_ -eq '\' -or $_ -eq '/' }).Count
        $depth -ne 1
    })
foreach ($stray in $strayManifests) {
    $errors.Add("$($stray.FullName): nested tool manifest; a tool must be exactly tools/<toolId>/$manifestName")
}

foreach ($toolDirInfo in $toolDirs) {
    $toolDir = $toolDirInfo.FullName
    $manifestPath = Join-Path $toolDir $manifestName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        $errors.Add("$($manifestPath): missing $manifestName (every directory under tools/ is a tool)")
        continue
    }
    $tool = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json

    foreach ($field in @("manifestVersion", "toolId", "displayName", "licenseTier", "risk", "build", "runtime", "artifacts")) {
        if (-not ($tool.PSObject.Properties.Name -contains $field) -or $null -eq $tool.$field) {
            $errors.Add("$($manifestPath): missing field '$field'")
        }
    }

    if ($tool.manifestVersion -ne $supportedManifestVersion) {
        $errors.Add("$($manifestPath): unsupported manifestVersion '$($tool.manifestVersion)' (expected '$supportedManifestVersion')")
        continue
    }

    $buildKind = if ($tool.build.PSObject.Properties.Name -contains "kind") { [string]$tool.build.kind } else { "" }
    $runtimeKind = if ($tool.runtime.PSObject.Properties.Name -contains "kind") { [string]$tool.runtime.kind } else { "" }
    $runtimeEntry = if ($tool.runtime.PSObject.Properties.Name -contains "entry") { [string]$tool.runtime.entry } else { "" }

    if ([string]::IsNullOrWhiteSpace([string]$tool.toolId)) {
        $errors.Add("$($manifestPath): toolId cannot be empty")
    }
    else {
        # toolId 是 tag、产物命名与索引的主键，重复会让下游静默指错工具
        $toolIdKey = [string]$tool.toolId
        $dirName = $toolDirInfo.Name

        # toolId 同时是目录名、tag 段和产物名。大小写混用或与目录不符，
        # 会让 tag 与路径指向两个不同的东西，且失败点离原因很远。
        if ($toolIdKey -cnotmatch $toolIdPattern) {
            $errors.Add("$($manifestPath): toolId '$toolIdKey' must be kebab-case, matching $toolIdPattern")
        }
        if ($toolIdKey -cne $dirName) {
            $errors.Add("$($manifestPath): toolId '$toolIdKey' must equal the directory name '$dirName'")
        }

        if ($seenToolIds.ContainsKey($toolIdKey)) {
            $errors.Add("$($manifestPath): duplicate toolId '$toolIdKey' (first declared at $($seenToolIds[$toolIdKey]))")
        }
        else {
            $seenToolIds[$toolIdKey] = (Resolve-Path -LiteralPath $manifestPath).Path.Substring($repoRoot.Length + 1)
        }
    }
    if ([string]::IsNullOrWhiteSpace([string]$tool.displayName)) {
        $errors.Add("$($manifestPath): displayName cannot be empty")
    }
    if ([string]::IsNullOrWhiteSpace([string]$tool.licenseTier)) {
        $errors.Add("$($manifestPath): licenseTier cannot be empty")
    }

    $riskLevel = if ($tool.PSObject.Properties.Name -contains "risk") { [string]$tool.risk } else { "" }
    if ($riskLevel -notin $supportedRiskLevels) {
        $errors.Add("$($manifestPath): unsupported risk '$riskLevel' (expected: $($supportedRiskLevels -join ', '))")
    }

    if ($buildKind -notin $supportedBuildKinds) {
        $errors.Add("$($manifestPath): unsupported build.kind '$buildKind'")
    }

    if ($runtimeKind -notin $supportedRuntimeKinds) {
        $errors.Add("$($manifestPath): unsupported runtime.kind '$runtimeKind'")
        continue
    }

    if ([string]::IsNullOrWhiteSpace($runtimeEntry)) {
        $errors.Add("$($manifestPath): runtime.entry is required")
        continue
    }

    # runtime.entry 描述「最终用户双击什么」，必须是工具目录内的相对路径。
    # 产物存在性只在 build.kind = manual 时静态校验：manual 没有构建步骤，
    # 文件不在就是真的坏了。其余 kind 的产物由构建产出，静态阶段不要求它存在，
    # 否则干净 clone 上 dist 永远不在（**/dist/ 被忽略），校验器必然失败。
    # 产物的存在性断言搬到构建之后：scripts/assert-artifacts.ps1。
    if ([System.IO.Path]::IsPathRooted($runtimeEntry)) {
        $errors.Add("$($manifestPath): runtime.entry must be a relative path, got '$runtimeEntry'")
        continue
    }

    $runtimeEntryPath = [System.IO.Path]::GetFullPath((Join-Path $toolDir $runtimeEntry))
    $toolDirFull = [System.IO.Path]::GetFullPath($toolDir).TrimEnd('\', '/')
    if (-not $runtimeEntryPath.StartsWith($toolDirFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        $errors.Add("$($manifestPath): runtime.entry escapes the tool directory '$runtimeEntry'")
        continue
    }

    if ($runtimeKind -eq "exe") {
        $exeExt = [System.IO.Path]::GetExtension($runtimeEntry).ToLowerInvariant()
        if ($exeExt -ne ".exe") {
            $errors.Add("$($manifestPath): runtime.kind 'exe' requires a .exe entry, got '$runtimeEntry'")
            continue
        }
    }

    if ($runtimeKind -in @("script", "exe") -and $buildKind -eq "manual") {
        if (-not (Test-Path -LiteralPath $runtimeEntryPath -PathType Leaf)) {
            $errors.Add("$($manifestPath): runtime entry not found '$runtimeEntry' (build.kind 'manual' has no build step to produce it)")
            continue
        }
    }

    if ($buildKind -eq "dotnet-msbuild") {
        if (-not ($tool.build.PSObject.Properties.Name -contains "project") -or [string]::IsNullOrWhiteSpace([string]$tool.build.project)) {
            $errors.Add("$($manifestPath): build.project is required for build.kind 'dotnet-msbuild'")
        }
        else {
            $projectRel = [string]$tool.build.project
            if ([System.IO.Path]::IsPathRooted($projectRel)) {
                $errors.Add("$($manifestPath): build.project must be a relative path, got '$projectRel'")
            }
            else {
                $projectPath = [System.IO.Path]::GetFullPath((Join-Path $toolDir $projectRel))
                if (-not $projectPath.StartsWith($toolDirFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $errors.Add("$($manifestPath): build.project escapes the tool directory '$projectRel'")
                }
                elseif (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
                    $errors.Add("$($manifestPath): build.project not found '$projectRel'")
                }
            }
        }
    }

    # build.script 是构建驱动脚本（源文件，不是产物），静态就能验它存在。
    # 它是非 dotnet 工具在 CI 上唯一的构建入口，写错路径等于该工具永远构建不出来。
    if ($tool.build.PSObject.Properties.Name -contains "script" -and -not [string]::IsNullOrWhiteSpace([string]$tool.build.script)) {
        $buildScriptRel = [string]$tool.build.script
        if ([System.IO.Path]::IsPathRooted($buildScriptRel)) {
            $errors.Add("$($manifestPath): build.script must be a relative path, got '$buildScriptRel'")
        }
        else {
            $buildScriptPath = [System.IO.Path]::GetFullPath((Join-Path $toolDir $buildScriptRel))
            if (-not $buildScriptPath.StartsWith($toolDirFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
                $errors.Add("$($manifestPath): build.script escapes the tool directory '$buildScriptRel'")
            }
            elseif (-not (Test-Path -LiteralPath $buildScriptPath -PathType Leaf)) {
                $errors.Add("$($manifestPath): build.script not found '$buildScriptRel'")
            }
        }
    }
    elseif ($buildKind -notin @("dotnet-msbuild", "manual")) {
        $errors.Add("$($manifestPath): build.kind '$buildKind' requires build.script")
    }

    if ($runtimeKind -eq "script") {
        $scriptExt = [System.IO.Path]::GetExtension($runtimeEntryPath).ToLowerInvariant()
        if ($scriptExt -notin $supportedScriptExtensions) {
            $errors.Add("$($manifestPath): unsupported script extension '$scriptExt'")
            continue
        }

        if ($scriptExt -eq ".ps1") {
            $nullTokens = $null
            $nullAst = $null
            $parseErrors = $null
            [System.Management.Automation.Language.Parser]::ParseFile($runtimeEntryPath, [ref]$nullTokens, [ref]$parseErrors) | Out-Null
            if ($parseErrors.Count -gt 0) {
                $errors.Add("$($manifestPath): PowerShell parse errors in '$runtimeEntry'")
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
                $errors.Add("$($manifestPath): batch entry '$runtimeEntry' has no executable command lines")
            }
        }
    }

    # 危险操作扫描必须和 risk 声明对得上。声明 low 却在改 HKLM、改启动配置、
    # 替换系统二进制 —— 不是声明写错了，就是代码写错了，两种都得停下来看。
    # 声明 elevated 的，README 必须把风险写在用户眼前，不能只藏在脚本里。
    $scanTargets = New-Object System.Collections.Generic.List[string]
    if ($runtimeKind -eq "script" -and $runtimeEntryPath -and (Test-Path -LiteralPath $runtimeEntryPath -PathType Leaf)) {
        $scanTargets.Add($runtimeEntryPath)
    }
    $scriptsDir = Join-Path $toolDir "scripts"
    if (Test-Path -LiteralPath $scriptsDir -PathType Container) {
        foreach ($scriptFile in (Get-ChildItem -LiteralPath $scriptsDir -Recurse -File -ErrorAction SilentlyContinue)) {
            if ($scriptFile.Extension.ToLowerInvariant() -in @(".ps1", ".bat", ".cmd")) {
                $scanTargets.Add($scriptFile.FullName)
            }
        }
    }

    $riskyHits = New-Object System.Collections.Generic.List[string]
    foreach ($target in ($scanTargets | Select-Object -Unique)) {
        $lineNumber = 0
        foreach ($line in (Get-Content -LiteralPath $target -ErrorAction SilentlyContinue)) {
            $lineNumber++
            $trimmed = $line.Trim()
            if ($trimmed -eq "" -or $trimmed.StartsWith("::") -or $trimmed.StartsWith("#")) { continue }
            if ($trimmed.StartsWith("rem ", [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            if ($trimmed.StartsWith("@rem ", [System.StringComparison]::OrdinalIgnoreCase)) { continue }

            $normalized = $line.ToLowerInvariant()
            foreach ($pattern in $riskyCommandPatterns) {
                if ($normalized.Contains($pattern)) {
                    $relative = if ($target.StartsWith($toolDir, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $target.Substring($toolDir.Length + 1)
                    }
                    else { Split-Path -Leaf $target }
                    $riskyHits.Add(("{0}:{1} {2}" -f $relative, $lineNumber, $trimmed))
                    break
                }
            }
        }
    }

    if ($riskLevel -eq "elevated") {
        $readmePath = Join-Path $toolDir "README.md"
        if (-not (Test-Path -LiteralPath $readmePath -PathType Leaf)) {
            $errors.Add("$($manifestPath): risk 'elevated' requires README.md with a '## Risk Notice' section")
        }
        else {
            $readmeText = Get-Content -LiteralPath $readmePath -Raw
            if ($readmeText -notmatch '(?m)^##\s+Risk Notice\s*$') {
                $errors.Add("$($manifestPath): risk 'elevated' requires a '## Risk Notice' section in README.md")
            }
        }
        # elevated 工具命中这些是它的本职，不需要逐条刷屏；只报"确认过了"。
        if ($riskyHits.Count -gt 0) {
            $fileCount = ($riskyHits | ForEach-Object { ($_ -split ':')[0] } | Select-Object -Unique).Count
            Write-Host ("  [elevated] {0}: {1} privileged operation(s) in {2} file(s)" -f $tool.toolId, $riskyHits.Count, $fileCount)
        }
    }
    elseif ($riskyHits.Count -gt 0) {
        foreach ($hit in $riskyHits) {
            $errors.Add("$($manifestPath): declared risk 'low' but contains a privileged operation -> $hit")
        }
        $errors.Add("$($manifestPath): either remove the operation or raise risk to 'elevated' and document it")
    }

    if (-not ($tool.artifacts -is [System.Array]) -or $tool.artifacts.Count -eq 0) {
        $errors.Add("$($manifestPath): artifacts must be a non-empty array")
    }
    else {
        foreach ($artifact in $tool.artifacts) {
            if (-not ($artifact.PSObject.Properties.Name -contains "kind") -or [string]::IsNullOrWhiteSpace([string]$artifact.kind)) {
                $errors.Add("$($manifestPath): each artifact requires kind")
            }
            if (-not ($artifact.PSObject.Properties.Name -contains "pattern") -or [string]::IsNullOrWhiteSpace([string]$artifact.pattern)) {
                $errors.Add("$($manifestPath): each artifact requires pattern")
            }
            elseif ([System.IO.Path]::IsPathRooted([string]$artifact.pattern) -or ([string]$artifact.pattern) -match '(^|[\\/])\.\.([\\/]|$)') {
                $errors.Add("$($manifestPath): artifact pattern must stay inside the tool directory, got '$($artifact.pattern)'")
            }
        }
    }

    $index.Add([pscustomobject]@{
        manifestVersion = $tool.manifestVersion
        toolId = $tool.toolId
        licenseTier = $tool.licenseTier
        risk = $riskLevel
        build = $tool.build
        runtime = $tool.runtime
        artifacts = $tool.artifacts
        entryExists = (Test-Path -LiteralPath $runtimeEntryPath -PathType Leaf)
        manifest = (Resolve-Path -LiteralPath $manifestPath).Path.Substring($repoRoot.Length + 1)
    })
}

if ($errors.Count -gt 0) {
    # $ErrorActionPreference = "Stop" 下逐条 Write-Error 会在第一条就抛出，
    # 用户只看到一个毛病、修一个跑一次。先列全，最后统一失败。
    $errors | ForEach-Object { Write-Host "ERROR: $_" }
    throw "Tool validation failed with $($errors.Count) issue(s)."
}

$outDir = Join-Path $repoRoot "out"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$index | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $outDir "tool-index.json") -Encoding UTF8
Write-Host "Validated $($index.Count) tools with manifest schema $supportedManifestVersion."
