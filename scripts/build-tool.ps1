param(
    [string[]]$ToolId = @(),
    [string]$Configuration = "Release",
    [string]$Version
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot "tools"

# 构建分派的唯一入口：本地与 CI 都调用它，参数只来自 tool.manifest.json。
# 任何"CI 里再写一遍构建命令"的诱惑都会让两边慢慢漂移。

# 机器上"有 dotnet.exe"和"有 SDK"是两件事：只装运行时的 host 会给出
# "No .NET SDKs were found" 这种离原因很远的报错。先挑一个真有 SDK 的 host，
# 找不到就直接说清楚，而不是让 dotnet 自己绕一圈再失败。
$script:DotnetExe = $null

function Resolve-DotnetExe {
    if ($script:DotnetExe) { return $script:DotnetExe }

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_EXE)) { $candidates += $env:DOTNET_EXE }
    $candidates += "dotnet"
    $candidates += (Join-Path $repoRoot ".dotnet\dotnet.exe")
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe") }

    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        foreach ($candidate in $candidates) {
            $exe = $candidate
            if ([System.IO.Path]::IsPathRooted($exe)) {
                if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { continue }
            }
            else {
                $resolved = Get-Command $exe -ErrorAction SilentlyContinue
                if (-not $resolved) { continue }
                $exe = $resolved.Source
            }

            $sdks = @(& $exe --list-sdks 2>$null)
            if ($sdks.Count -gt 0) {
                $script:DotnetExe = $exe
                Write-Host "  using dotnet: $exe ($($sdks.Count) SDK)"
                return $exe
            }
        }
    }
    finally { $ErrorActionPreference = $previousEap }

    throw "No .NET SDK found. Tried: $($candidates -join ', '). Install one, or point DOTNET_EXE at a dotnet.exe that has an SDK."
}

function Invoke-DotnetBuild {
    param([string]$ToolDir, $Build, [string]$Id)

    $projectRel = [string]$Build.project
    if ([string]::IsNullOrWhiteSpace($projectRel)) {
        throw "build.project is required for build.kind 'dotnet-msbuild' ($Id)"
    }

    $buildConfiguration = $Configuration
    if ($Build.PSObject.Properties.Name -contains "configuration" -and $Build.configuration) {
        $buildConfiguration = [string]$Build.configuration
    }

    $extra = @()
    if ($Build.PSObject.Properties.Name -contains "args" -and $Build.args) {
        foreach ($arg in $Build.args) { $extra += [string]$arg }
    }

    # 版本号由 tag 决定；只有显式传入时才覆盖项目文件里的默认值。
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        $extra += "-p:Version=$Version"
    }

    $dotnetExe = Resolve-DotnetExe

    Push-Location $ToolDir
    # 构建器（dotnet / pyinstaller）会往 stderr 打进度与警告。脚本级
    # $ErrorActionPreference = "Stop" 会把第一条 stderr 当成致命错误直接中断，
    # 于是"构建成功但脚本退出"这种最难查的假失败就出现了。这里只按退出码判定。
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        # RID / 单文件 / 压缩等发布属性固化在项目文件里，
        # 这里只传 configuration 与 manifest 声明的额外参数。
        Write-Host "  dotnet publish $projectRel -c $buildConfiguration"
        & $dotnetExe publish $projectRel -c $buildConfiguration @extra 2>&1 | ForEach-Object { Write-Host "    $_" }
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousEap
        Pop-Location
    }

    if ($exitCode -ne 0) { throw "dotnet publish failed for $Id (exit $exitCode)" }
}

function Invoke-ScriptBuild {
    param([string]$ToolDir, [string]$ScriptRel, [string]$Id)

    if ([string]::IsNullOrWhiteSpace($ScriptRel)) {
        throw "build.script is required for this build.kind ($Id)"
    }

    $scriptPath = [System.IO.Path]::GetFullPath((Join-Path $ToolDir $ScriptRel))
    $toolDirFull = [System.IO.Path]::GetFullPath($ToolDir).TrimEnd('\', '/')
    if (-not $scriptPath.StartsWith($toolDirFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "build.script escapes the tool directory ($Id): $ScriptRel"
    }
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw "build.script not found ($Id): $ScriptRel"
    }

    $extension = [System.IO.Path]::GetExtension($scriptPath).ToLowerInvariant()

    # 非 dotnet 工具拿不到 -p:Version，用环境变量传同样的信息。
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        $env:TOOL_VERSION = $Version
    }

    Push-Location $ToolDir
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        switch ($extension) {
            ".ps1" {
                & pwsh -NoProfile -File $scriptPath 2>&1 | ForEach-Object { Write-Host "    $_" }
            }
            ".bat" {
                # --no-pause 让交互式构建脚本在自动化里不阻塞；脚本可选择忽略它。
                & cmd /c ('"' + $scriptPath + '" --no-pause') 2>&1 | ForEach-Object { Write-Host "    $_" }
            }
            ".cmd" {
                & cmd /c ('"' + $scriptPath + '" --no-pause') 2>&1 | ForEach-Object { Write-Host "    $_" }
            }
            default {
                throw "unsupported build.script extension '$extension' ($Id)"
            }
        }
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousEap
        Pop-Location
    }

    if ($exitCode -ne 0) { throw "build script failed for $Id (exit $exitCode)" }
}

$toolDirs = @()
if ($ToolId.Count -gt 0) {
    # 增量构建：一次传入多个 toolId，顺序即执行顺序；空数组 = 全量。
    foreach ($id in $ToolId) {
        $singleDir = Join-Path $toolsRoot $id
        if (-not (Test-Path -LiteralPath $singleDir -PathType Container)) {
            throw "Unknown tool id '$id' (no such directory under tools/)"
        }
        $toolDirs += @(Get-Item -LiteralPath $singleDir)
    }
}
else {
    $toolDirs = @(Get-ChildItem -Path $toolsRoot -Directory | Sort-Object Name)
}

if ($toolDirs.Count -eq 0) { throw "No tool directories found under $toolsRoot" }

$built = 0
foreach ($toolDirInfo in $toolDirs) {
    $manifestPath = Join-Path $toolDirInfo.FullName "tool.manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Missing tool.manifest.json: $manifestPath"
    }

    $tool = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $id = [string]$tool.toolId
    $kind = [string]$tool.build.kind
    Write-Host "== $id ($kind)"

    $hasBuildScript = ($tool.build.PSObject.Properties.Name -contains "script") -and -not [string]::IsNullOrWhiteSpace([string]$tool.build.script)

    if ($kind -eq "manual" -or ($kind -eq "binary" -and -not $hasBuildScript)) {
        # manual 没有构建；binary 未声明装配脚本时产物由外部供给，同样没有构建步骤。
        # binary 声明了 build.script 则走脚本装配（下载/复制进 dist/）。
        Write-Host "  skip: no build step"
    }
    else {
        if ($kind -eq "dotnet-msbuild") {
            Invoke-DotnetBuild -ToolDir $toolDirInfo.FullName -Build $tool.build -Id $id
        }
        else {
            $scriptRel = ""
            if ($hasBuildScript) { $scriptRel = [string]$tool.build.script }
            Invoke-ScriptBuild -ToolDir $toolDirInfo.FullName -ScriptRel $scriptRel -Id $id
        }
        $built++
    }

    # 统一测试控制：构建（或 manual 跳过）之后，声明了 build.verify 的工具必须通过其验收断言。
    # 断言脚本由工具自己提供，且必须能在干净 CI 机器上安全运行；非零退出码即失败。
    if (($tool.build.PSObject.Properties.Name -contains "verify") -and -not [string]::IsNullOrWhiteSpace([string]$tool.build.verify)) {
        $verifyRel = [string]$tool.build.verify
        $verifyPath = [System.IO.Path]::GetFullPath((Join-Path $toolDirInfo.FullName $verifyRel))
        $toolDirFull = [System.IO.Path]::GetFullPath($toolDirInfo.FullName).TrimEnd('\', '/')
        if (-not $verifyPath.StartsWith($toolDirFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "build.verify escapes the tool directory ($Id): $verifyRel"
        }
        if (-not (Test-Path -LiteralPath $verifyPath -PathType Leaf)) {
            throw "build.verify not found ($Id): $verifyRel"
        }
        Write-Host "  verify: $verifyRel"
        # 与构建相同：验收脚本可能往 stderr 打进度，只按退出码判定。
        $previousEap = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            & pwsh -NoProfile -File $verifyPath 2>&1 | ForEach-Object { Write-Host "    $_" }
            $verifyExit = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $previousEap }
        if ($verifyExit -ne 0) { throw "verify failed for $Id (exit $verifyExit)" }
    }
}

Write-Host "Built $built tool(s)."
