$ErrorActionPreference = "Stop"

$regPath = "HKLM:\SOFTWARE\Microsoft\Windows\Dwm"
$valueName = "OverlayTestMode"
$valueData = 5

if (-not (Test-Path -LiteralPath $regPath)) {
    New-Item -Path $regPath -Force | Out-Null
}

New-ItemProperty -Path $regPath -Name $valueName -PropertyType DWord -Value $valueData -Force | Out-Null
Write-Host "Set $regPath\\$valueName = $valueData"
Write-Host "Restart or sign out may be required for full effect."
