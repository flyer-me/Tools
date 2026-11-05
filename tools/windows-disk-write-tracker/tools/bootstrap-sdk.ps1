<#
.SYNOPSIS
    免提权引导安装 .NET 10 SDK 到用户目录。

.DESCRIPTION
    先用机器上已有的 dotnet；只有它解析不出 global.json 时才装。
    安装走官方 dotnet-install.ps1，落到 %LOCALAPPDATA%\Microsoft\dotnet。

    为什么不无条件装：muxer 只在自己根目录下查 SDK，两个安装的 SDK 清单互不可见。
    系统已经有可用 SDK 时再往用户目录塞一份，只会多出一个永远选不中的副本
    （PATH 上 Program Files 在前，且 build.ps1 优先用系统那份），白占几百 MB。

.PARAMETER Channel
    SDK 通道，默认 10.0。

.PARAMETER InstallDir
    安装目录，默认 %LOCALAPPDATA%\Microsoft\dotnet。

.PARAMETER Force
    跳过"已有可用 SDK"的检查，强制安装。
#>
[CmdletBinding()]
param(
    [string]$Channel = '10.0',
    [string]$InstallDir,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if (-not $InstallDir) {
    $InstallDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$installed = Join-Path $InstallDir 'dotnet.exe'

# 系统装的、PATH 上的、本脚本自己装过的，都算候选。
# 用 `dotnet --version` 判断够不够 —— 这正是 muxer 解析 global.json 的入口。
function Get-DotnetCandidates {
    $list = @()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $list += $onPath.Source }
    if ($env:ProgramFiles) { $list += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') }
    $list += $installed
    $list | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -Unique
}

if (-not $Force) {
    Push-Location $repoRoot
    try {
        foreach ($candidate in (Get-DotnetCandidates)) {
            $ErrorActionPreference = 'Continue'
            $version = (& $candidate --version 2>$null | Out-String).Trim()
            $code = $LASTEXITCODE
            $ErrorActionPreference = 'Stop'
            if ($code -eq 0 -and $version -match '^\d+\.\d+\.\d+') {
                Write-Host "已存在可用的 SDK：$version"
                Write-Host "  $candidate"
                Write-Host ''
                Write-Host '无需安装。要强制再装一份，加 -Force。'
                exit 0
            }
        }
    }
    finally { Pop-Location }
}

$script = Join-Path $PSScriptRoot 'dotnet-install.ps1'
if (-not (Test-Path $script)) {
    Write-Host '未找到 dotnet-install.ps1，正在下载...'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
}

Write-Host "安装 SDK 通道 $Channel 到 $InstallDir"
& $script -Channel $Channel -InstallDir $InstallDir -NoPath

if (-not (Test-Path $installed)) {
    throw "安装失败：$installed 不存在"
}

Write-Host ''
Write-Host '已安装的 SDK：'
& $installed --list-sdks

Write-Host ''
Write-Host "后续请统一使用绝对路径: $installed"
