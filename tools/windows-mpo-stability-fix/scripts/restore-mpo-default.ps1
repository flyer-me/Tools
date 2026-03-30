$ErrorActionPreference = "Stop"

$regPath = "HKLM:\SOFTWARE\Microsoft\Windows\Dwm"
$valueName = "OverlayTestMode"

if (Test-Path -LiteralPath $regPath) {
    Remove-ItemProperty -Path $regPath -Name $valueName -ErrorAction SilentlyContinue
}

Write-Host "Removed $regPath\\$valueName (if present)."
Write-Host "Restart or sign out may be required for full effect."
