<#
.SYNOPSIS
    构建 DiskWriteTracker。

.DESCRIPTION
    本机可能没有全局 SDK（C:\Program Files\dotnet\sdk 为空壳），
    因此脚本显式解析 dotnet 路径，不依赖 PATH。

.PARAMETER SkipTests
    跳过测试（M0 阶段尚无测试项目）。

.PARAMETER DotnetRoot
    指定 dotnet 根目录。默认依次尝试：
      1) 参数
      2) %LOCALAPPDATA%\Microsoft\dotnet（免提权安装位置）
      3) PATH 上的 dotnet

.PARAMETER NoPublish
    只编译不发布。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$DotnetRoot,
    [switch]$SkipTests,
    [switch]$NoPublish
)

$ErrorActionPreference = 'Stop'
# 引擎固定输出 UTF-8。PS 5.1 默认按控制台代码页解码子进程 stdout，
# 不显式声明会把中文断言判成乱码（假失败），本脚本与 verify.ps1 统一到 UTF-8。
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Invoke-Engine {
    # 用 ProcessStartInfo 而不是 `& $exe`：解码编码可控，且 $ErrorActionPreference='Stop'
    # 下原生命令写 stderr 不会抛 NativeCommandError。
    param([string]$Path, [string[]]$Argv)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Path
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $psi.StandardOutputEncoding = $utf8
    $psi.StandardErrorEncoding = $utf8

    $quoted = @()
    foreach ($a in $Argv) {
        if ($a -match '[\s"]') { $quoted += '"' + $a.Replace('"', '\"') + '"' } else { $quoted += $a }
    }
    if ($quoted.Count -gt 0) { $psi.Arguments = ($quoted -join ' ') }

    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.StandardInput.Close()
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    $proc.WaitForExit()

    $text = $outTask.GetAwaiter().GetResult()
    $errText = $errTask.GetAwaiter().GetResult()
    $code = $proc.ExitCode
    $proc.Dispose()

    return @{ Code = $code; Text = $text; Error = $errText }
}

function Convert-LegacySettings {
    param([string]$Source, [string]$Destination)

    try {
        $raw = Get-Content -LiteralPath $Source -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "无法解析旧设置 $Source，已中止构建以免丢用户偏好：$($_.Exception.Message)"
    }

    $days = 400
    $logDays = 30

    $reportDays = $days
    if ($null -ne $raw.reportDays -and [int]$raw.reportDays -ge 1 -and [int]$raw.reportDays -le 3650) {
        $reportDays = [int]$raw.reportDays
    }

    $exportDays = $days
    if ($null -ne $raw.exportDays -and [int]$raw.exportDays -ge 1 -and [int]$raw.exportDays -le 3650) {
        $exportDays = [int]$raw.exportDays
    }

    $keepLogs = $logDays
    if ($null -ne $raw.logDays -and [int]$raw.logDays -ge 1 -and [int]$raw.logDays -le 3650) {
        $keepLogs = [int]$raw.logDays
    }

    $time = [string]$raw.time
    if ($time -notmatch '^([01]\d|2[0-3]):[0-5]\d$') { $time = '00:05' }

    $payload = [ordered]@{
        schemaVersion = 1
        reportDays    = $reportDays
        exportDays    = $exportDays
        time          = $time
        logDays       = $keepLogs
    }

    $parent = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $payload | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Destination -Encoding UTF8
}

function Resolve-Dotnet {
    param([string]$Root)

    if ($Root) {
        $candidate = Join-Path $Root 'dotnet.exe'
        if (-not (Test-Path $candidate)) { throw "指定的 DotnetRoot 下没有 dotnet.exe: $candidate" }
        return $candidate
    }

    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path $local) { return $local }

    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    throw '找不到 dotnet。请先运行 scripts\bootstrap-sdk.ps1 安装 SDK。'
}

$dotnet = Resolve-Dotnet -Root $DotnetRoot
Write-Host "dotnet : $dotnet"

$sdks = & $dotnet --list-sdks
if (-not $sdks) {
    throw "该 dotnet 没有任何 SDK。请运行 scripts\bootstrap-sdk.ps1。"
}
Write-Host "SDK    : $($sdks -join '; ')"

Write-Host ''
Write-Host '--- restore ---'
# Release 下 Cli.csproj 固定了 RuntimeIdentifier=win-x64，restore 必须带同一个 RID，
# 否则 project.assets.json 里没有 net8.0-windows/win-x64 目标，build 会报 NETSDK1047。
& $dotnet restore DiskWriteTracker.sln -r $Runtime
if ($LASTEXITCODE -ne 0) { throw 'restore 失败' }

Write-Host ''
Write-Host '--- build ---'
# 不要给 sln 传 -r：解决方案级构建不支持指定 RID（NETSDK1134）。
# Cli.csproj 自己声明了 RuntimeIdentifier=win-x64，restore 又带了同样的 RID，
# 所以这里不带 -r 也会选到 net8.0-windows/win-x64 目标。
& $dotnet build DiskWriteTracker.sln -c $Configuration --no-restore -v minimal
if ($LASTEXITCODE -ne 0) { throw 'build 失败' }

if (-not $SkipTests) {
    Write-Host ''
    Write-Host '--- test ---'
    # tests\ 目前只有手工验证用的 fixture，没有测试项目，所以这里必然走 else。
    # 分支保留：将来加了 csproj 就会自动纳入，而不是被静默跳过。
    $testProjects = Get-ChildItem -Path (Join-Path $repoRoot 'tests') -Filter '*.csproj' -Recurse -ErrorAction SilentlyContinue
    if ($testProjects) {
        & $dotnet test DiskWriteTracker.sln -c $Configuration --no-build -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'test 失败' }
    }
    else {
        Write-Host '（无测试项目，跳过）'
    }
}

if ($NoPublish) {
    Write-Host ''
    Write-Host '已跳过发布（-NoPublish）。'
    exit 0
}

Write-Host ''
Write-Host '--- publish ---'
$dist = Join-Path $repoRoot 'src\dist'
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist -Force | Out-Null }

# 注意：不要在此处 Remove-Item -Recurse。本机安全策略会拦截并 fail-closed。
# 采用覆盖式发布：publish 会重写同名文件，残留的旧文件不影响运行。
# 需要彻底重来请手动删除 dist 目录。

# publish 属性（RID / self-contained / 单文件 / 压缩 / R2R）全部固化在
# DiskWriteTracker.Cli.csproj 的 Release 条件组里，这里不再传 -p:。
# CI 走的是同一条命令，参数只有一处真源，本地与 CI 不可能漂移。
# 产物由 csproj 的 StageDist target 落到 src\dist，不需要 -o。
& $dotnet publish (Join-Path $repoRoot 'src\DiskWriteTracker.Cli\DiskWriteTracker.Cli.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'publish 失败' }

# ---------- 产物形态：只留 exe / README.txt / data ----------
# 覆盖式发布不会删除上一版留下的文件，所以这里逐个点名删除。
# 不能 Remove-Item -Recurse（本机安全策略 fail-closed），只用 .NET 的单文件/空目录删除。

$distData = Join-Path $dist 'data'
$distConfig = Join-Path $dist 'config'
$distLauncher = Join-Path $dist 'launcher'

# 1) 先把老版本的用户偏好搬到 data\settings.json，再删 config —— 顺序反了会丢设置
$legacySettings = Join-Path $distConfig 'launcher.json'
$newSettings = Join-Path $distData 'settings.json'
if ((Test-Path -LiteralPath $legacySettings) -and -not (Test-Path -LiteralPath $newSettings)) {
    Convert-LegacySettings -Source $legacySettings -Destination $newSettings
    Write-Host '已将 config\launcher.json 迁移为 data\settings.json'
}

# 2) 点名删除上一版形态的文件
$obsolete = @(
    (Join-Path $dist 'DiskWriteTracker.cmd'),
    (Join-Path $distLauncher 'lib.ps1'),
    (Join-Path $distLauncher 'menu.ps1'),
    (Join-Path $distConfig 'default.json'),
    (Join-Path $distConfig 'default.schema.json'),
    (Join-Path $distConfig 'launcher.json')
)
foreach ($file in $obsolete) {
    if (Test-Path -LiteralPath $file) {
        [IO.File]::Delete($file)
        Write-Host "已移除上一版文件: $file"
    }
}

# 3) 删空目录。非空只告警，绝不递归
foreach ($dir in @($distLauncher, $distConfig, (Join-Path $dist 'exports'))) {
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    try {
        [IO.Directory]::Delete($dir, $false)
        Write-Host "已移除空目录: $dir"
    }
    catch {
        Write-Warning "目录非空，保留待人工确认: $dir"
    }
}

# 4) README.txt 与 data\ 由 csproj 的 StageDist target 生成，这里不再重复拷贝。
#    两处都做会造成「两处真源」，而 StageDist 那份才是 CI 也会走的那条路径。

# 5) 形态断言：多一个条目都算失败，避免技术文件悄悄回流
$expected = @('DiskWriteTracker.exe', 'README.txt', 'data')
$actual = @(Get-ChildItem -LiteralPath $dist -Force | ForEach-Object { $_.Name })
$extra = @($actual | Where-Object { $expected -notcontains $_ })
$missingItems = @($expected | Where-Object { $actual -notcontains $_ })
if ($extra.Count -gt 0) { throw "dist 根目录出现预期外条目: $($extra -join ', ')" }
if ($missingItems.Count -gt 0) { throw "dist 根目录缺少: $($missingItems -join ', ')" }

$junk = @(Get-ChildItem -LiteralPath $dist -Recurse -Force -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @('.ps1', '.cmd', '.bat', '.psm1') -or $_.Name -like '*.schema.json' })
if ($junk.Count -gt 0) { throw "dist 内仍有脚本类文件: $(($junk | ForEach-Object { $_.Name }) -join ', ')" }

$exe = Join-Path $dist 'DiskWriteTracker.exe'
if (-not (Test-Path $exe)) { throw "发布后未找到 $exe" }

Write-Host ''
Write-Host '--- smoke ---'

$doctor = Invoke-Engine -Path $exe -Argv @('doctor', '--json')
if ($doctor.Code -ne 0) { throw "doctor 冒烟失败 (exit=$($doctor.Code)): $($doctor.Error)" }
$doctorData = $doctor.Text | ConvertFrom-Json
if ($doctorData.data.dataDirectoryIsPortable -ne $true) {
    throw "便携模式未生效: $($doctorData.data.dataDirectory)"
}

# 无参 + 输入/输出重定向必须打印帮助而不是进菜单，否则构建会挂在这里等键盘
$noArg = Invoke-Engine -Path $exe -Argv @()
if ($noArg.Code -ne 0) { throw "无参运行退出码 $($noArg.Code): $($noArg.Error)" }
if (-not $noArg.Text.Contains('用法')) { throw '无参运行未输出帮助文本' }
if ($noArg.Text.Contains('请选择')) { throw 'stdin/stdout 重定向时仍进入了交互菜单' }

if (-not $SkipTests) {
    Write-Host ''
    Write-Host '--- verify ---'
    & (Join-Path $repoRoot 'scripts\verify.ps1') -Dist $dist
    if ($LASTEXITCODE -ne 0) { throw '验收断言失败' }
}

Write-Host ''
Write-Host "构建完成: $exe"
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "单文件大小: $sizeMb MB"
