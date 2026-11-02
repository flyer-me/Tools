param(
    [string]$Hs2InstallDir = ""
)

$ErrorActionPreference = "Stop"

# 不再提供机器特定的默认安装目录：这里是分发仓库，不是某台机器的部署脚本。
if ([string]::IsNullOrWhiteSpace($Hs2InstallDir)) {
    throw "用法：deploy-to-hs2.ps1 -Hs2InstallDir <HS2 安装目录>"
}

$toolRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $toolRoot "src\HS2ModpackAnalyzer.csproj"

# DeploySingleFileToHs2 依赖 Publish;CreateSingleFileExecutable，
# 所以这一步会真正走 publish 链路，与 CI 出包用的是同一条路径。
dotnet msbuild $projectFile /t:DeploySingleFileToHs2 /p:Configuration=Release /p:Hs2InstallDir="$Hs2InstallDir"

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Single file deployed to: $(Join-Path $Hs2InstallDir 'HS2ModpackAnalyzer.exe')"
