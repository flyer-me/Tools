$ErrorActionPreference = "Stop"

$regPath = "HKLM:\SOFTWARE\Microsoft\Windows\Dwm"
$valueName = "OverlayTestMode"

if (-not (Test-Path -LiteralPath $regPath)) {
    Write-Host "Registry path not found: $regPath"
    exit 0
}

$value = Get-ItemProperty -Path $regPath -Name $valueName -ErrorAction SilentlyContinue
if ($null -eq $value) {
    Write-Host "$valueName is not set. MPO behavior is OS default."
}
else {
    Write-Host "$valueName = $($value.$valueName)"
}
