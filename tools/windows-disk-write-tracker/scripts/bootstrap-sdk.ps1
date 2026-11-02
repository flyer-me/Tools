<#
.SYNOPSIS
    免提权引导安装 .NET 8 SDK 到用户目录。

.DESCRIPTION
    使用官方 dotnet-install.ps1，安装到 %LOCALAPPDATA%\Microsoft\dotnet。
    关键：不要依赖 PATH 上的 dotnet —— muxer 只在自己根目录下查找 SDK。

.PARAMETER Channel
    SDK 通道，默认 8.0。

.PARAMETER InstallDir
    安装目录，默认 %LOCALAPPDATA%\Microsoft\dotnet。
#>
[CmdletBinding()]
param(
    [string]$Channel = '8.0',
    [string]$InstallDir
)

$ErrorActionPreference = 'Stop'

if (-not $InstallDir) {
    $InstallDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
}

$script = Join-Path $PSScriptRoot 'dotnet-install.ps1'
if (-not (Test-Path $script)) {
    Write-Host '未找到 dotnet-install.ps1，正在下载...'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
}

Write-Host "安装 SDK 通道 $Channel 到 $InstallDir"
& $script -Channel $Channel -InstallDir $InstallDir -NoPath

$dotnet = Join-Path $InstallDir 'dotnet.exe'
if (-not (Test-Path $dotnet)) {
    throw "安装失败：$dotnet 不存在"
}

Write-Host ''
Write-Host '已安装的 SDK：'
& $dotnet --list-sdks

Write-Host ''
Write-Host "后续请统一使用绝对路径: $dotnet"
