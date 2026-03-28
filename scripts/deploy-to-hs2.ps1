param(
    [string]$Hs2InstallDir = "D:\ProgramData\HS2_DXBR_R16"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot "ConsoleApp1.csproj"

dotnet msbuild $projectFile /t:DeploySingleFileToHs2 /p:Configuration=Release /p:Hs2InstallDir="$Hs2InstallDir"

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Single file deployed to: $(Join-Path $Hs2InstallDir 'HS2ModpackAnalyzer.exe')"
