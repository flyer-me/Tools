<#
.SYNOPSIS
    Removes "managed by your organization" restrictions on Windows personal computers.
.DESCRIPTION
    Backs up and removes MDM enrollment entries and policy registry keys that cause
    the "Some settings are managed by your organization" message in Windows Settings.
    Designed for personal computers that were previously enrolled in MDM (Intune, etc.).
.NOTES
    Requires Administrator privileges.
    A system restart is recommended after running this script.
#>

[CmdletBinding()]
param(
    [string]$BackupDir = "$env:USERPROFILE\OrgRestrictionsBackup",
    [switch]$Restore,
    [string]$RestoreFromDir
)

$ErrorActionPreference = "Stop"

# ── Self-elevation ──────────────────────────────────────────────
function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Elevated {
    $scriptPath = $MyInvocation.MyCommand.Path
    $argsList = $MyInvocation.BoundParameters.GetEnumerator() | ForEach-Object {
        if ($_.Value -is [switch]) {
            if ($_.Value) { "-$($_.Key)" }
        } else {
            "-$($_.Key) `"$($_.Value)`""
        }
    }
    $argString = ($argsList | Where-Object { $_ }) -join " "
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" $argString"
    exit 0
}

# ── Restore mode ────────────────────────────────────────────────
function Invoke-Restore {
    param([string]$SourceDir)
    if (-not (Test-Path $SourceDir -PathType Container)) {
        Write-Host "Backup directory not found: $SourceDir" -ForegroundColor Red
        exit 1
    }
    $regFiles = Get-ChildItem -Path $SourceDir -Filter "*.reg" | Sort-Object Name
    if (-not $regFiles) {
        Write-Host "No .reg backup files found in $SourceDir" -ForegroundColor Red
        exit 1
    }
    Write-Host "Restoring from: $SourceDir" -ForegroundColor Cyan
    foreach ($f in $regFiles) {
        Write-Host "  Importing: $($f.Name)"
        reg import "`"$($f.FullName)`"" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "  Import failed for $($f.Name) (exit code: $LASTEXITCODE)"
        }
    }
    Write-Host "Restore complete. Please restart your computer." -ForegroundColor Green
}

# ── Backup helpers ──────────────────────────────────────────────
function Backup-RegistryKey {
    param([string]$Key, [string]$OutputFile)
    $keyExists = $false
    try {
        $null = Get-ItemProperty -Path $Key -ErrorAction Stop
        $keyExists = $true
    } catch {}
    if ($keyExists) {
        reg export "$Key" "$OutputFile" /y 2>&1 | Out-Null
        return $LASTEXITCODE -eq 0
    }
    return $false
}

function Remove-RegistryKeySafe {
    param([string]$Key)
    try {
        $null = Get-Item -Path $Key -ErrorAction Stop
        Remove-Item -Path $Key -Recurse -Force -ErrorAction Stop
        return $true
    } catch {
        return $false
    }
}

# ── Main cleanup ────────────────────────────────────────────────
function Invoke-Cleanup {
    param([string]$BackupRoot)

    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $sessionDir = Join-Path $BackupRoot $timestamp
    New-Item -ItemType Directory -Path $sessionDir -Force | Out-Null

    Write-Host "Backup directory: $sessionDir" -ForegroundColor Cyan
    Write-Host ""

    # --- Phase 1: Backup and remove MDM enrollments ---
    Write-Host "== Phase 1: MDM Enrollments ==" -ForegroundColor Yellow
    $enrollmentRoot = "HKLM:\SOFTWARE\Microsoft\Enrollments"
    Backup-RegistryKey -Key $enrollmentRoot -OutputFile (Join-Path $sessionDir "enrollments.reg")
    $enrollments = Get-ChildItem -Path $enrollmentRoot -ErrorAction SilentlyContinue
    if ($enrollments) {
        foreach ($e in $enrollments) {
            $name = $e.PSChildName
            Write-Host "  Removing enrollment: $name"
            Remove-RegistryKeySafe -Key $e.PSPath
        }
        Write-Host "  Removed $($enrollments.Count) enrollment(s)." -ForegroundColor Green
    } else {
        Write-Host "  No MDM enrollments found."
    }

    # --- Phase 2: Clean HKLM policy keys ---
    Write-Host "`n== Phase 2: HKLM Policy Keys ==" -ForegroundColor Yellow
    Backup-RegistryKey -Key "HKLM:\SOFTWARE\Policies" -OutputFile (Join-Path $sessionDir "policies_HKLM.reg")

    $hklmKeys = @(
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Appx",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Explorer",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Network Connections",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\SettingSync",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WorkplaceJoin",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WcmSvc",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\TenantRestrictions",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\MpeHttpExt",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\BITS",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Bowser",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\EnhancedStorageDevices",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\IPSec",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\NetworkConnectivityStatusIndicator",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\NetworkProvider",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Sudo",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows\WSDAPI",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows Advanced Threat Protection",
        "HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender",
        "HKLM:\SOFTWARE\Policies\Microsoft\SQMClient"
    )

    $hklmRemoved = 0
    foreach ($key in $hklmKeys) {
        if (Remove-RegistryKeySafe -Key $key) {
            Write-Host "  Removed: $key"
            $hklmRemoved++
        }
    }
    Write-Host "  Removed $hklmRemoved HKLM policy key(s)." -ForegroundColor Green

    # --- Phase 3: Clean HKLM CurrentVersion Policies (non-system) ---
    Write-Host "`n== Phase 3: HKLM CurrentVersion Policies ==" -ForegroundColor Yellow
    Backup-RegistryKey -Key "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies" -OutputFile (Join-Path $sessionDir "policies_CurrentVersion.reg")

    $cvKeys = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop",
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments",
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection"
    )

    $cvRemoved = 0
    foreach ($key in $cvKeys) {
        if (Remove-RegistryKeySafe -Key $key) {
            Write-Host "  Removed: $key"
            $cvRemoved++
        }
    }

    # Also clean specific values under Explorer (not the System key which has UAC defaults)
    $explorerKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer"
    $explorerValues = @("ForceActiveDesktopOn", "NoActiveDesktop", "NoActiveDesktopChanges")
    $explorerCount = 0
    foreach ($val in $explorerValues) {
        try {
            Remove-ItemProperty -Path $explorerKey -Name $val -Force -ErrorAction Stop
            Write-Host "  Removed value: $explorerKey\$val"
            $explorerCount++
        } catch {}
    }
    if ($explorerCount -gt 0) {
        $cvRemoved += $explorerCount
    }
    Write-Host "  Removed $cvRemoved CurrentVersion policy entries." -ForegroundColor Green

    # --- Phase 4: Clean HKCU policy keys ---
    Write-Host "`n== Phase 4: HKCU Policy Keys ==" -ForegroundColor Yellow
    Backup-RegistryKey -Key "HKCU:\SOFTWARE\Policies" -OutputFile (Join-Path $sessionDir "policies_HKCU.reg")

    $hkcuKeys = @(
        "HKCU:\SOFTWARE\Policies\Microsoft\Windows\CloudContent",
        "HKCU:\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
        "HKCU:\SOFTWARE\Policies\Microsoft\Windows\Explorer",
        "HKCU:\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
        "HKCU:\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion",
        "HKCU:\SOFTWARE\Policies\Power"
    )

    $hkcuRemoved = 0
    foreach ($key in $hkcuKeys) {
        if (Remove-RegistryKeySafe -Key $key) {
            Write-Host "  Removed: $key"
            $hkcuRemoved++
        }
    }
    Write-Host "  Removed $hkcuRemoved HKCU policy key(s)." -ForegroundColor Green

    # --- Summary ---
    Write-Host ""
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host "  Cleanup Complete" -ForegroundColor Green
    Write-Host "  Backup saved to: $sessionDir" -ForegroundColor Cyan
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Please restart your computer for changes to take full effect." -ForegroundColor Yellow
    Write-Host "To restore, run: powershell -File `"$PSCommandPath`" -Restore -RestoreFromDir `"$sessionDir`"" -ForegroundColor Gray
}

# ── Entry point ─────────────────────────────────────────────────
if (-not (Test-Admin)) {
    Write-Host "This script requires Administrator privileges. Requesting elevation..." -ForegroundColor Yellow
    Invoke-Elevated
}

if ($Restore) {
    $sourceDir = if ($RestoreFromDir) { $RestoreFromDir } else { $BackupDir }
    if (-not $RestoreFromDir) {
        $backups = Get-ChildItem -Path $sourceDir -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending
        if (-not $backups) {
            Write-Host "No backups found in $sourceDir" -ForegroundColor Red
            exit 1
        }
        $sourceDir = $backups[0].FullName
        Write-Host "Using latest backup: $sourceDir" -ForegroundColor Cyan
    }
    Invoke-Restore -SourceDir $sourceDir
} else {
    Invoke-Cleanup -BackupRoot $BackupDir
}
