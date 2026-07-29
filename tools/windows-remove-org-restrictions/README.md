# Windows Remove Organization Restrictions

## Purpose

Removes "Some settings are managed by your organization" messages on Windows personal computers.

This tool backs up and removes:
- MDM enrollment entries (Intune / ConfigMgr leftovers)
- Policy registry keys that override user-facing settings
- Telemetry, Windows Update, Defender, and other managed-policy keys

Intended for personal machines that were previously enrolled in device management and now show "managed by your organization" banners even after unenrollment.

## Run

Open **PowerShell as Administrator**, then:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\Remove-OrgRestrictions.ps1"
```

The script auto-elevates if not already running as admin.

### Restore from backup

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\Remove-OrgRestrictions.ps1" -Restore
```

To restore from a specific backup directory:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\Remove-OrgRestrictions.ps1" -Restore -RestoreFromDir "C:\Users\<you>\OrgRestrictionsBackup\20260101_120000"
```

Backups are saved to `%USERPROFILE%\OrgRestrictionsBackup\<timestamp>\` as `.reg` files.

## Risk Notice

- **Requires Administrator privileges** (modifies `HKLM` registry).
- **A system restart is required** for all changes to take full effect.
- **Back up your data** before running. Registry backups are created automatically, but manual restoration may be needed if unusual MDM configurations are present.
- This tool removes registry keys; it does not uninstall software or reset Group Policy objects applied via domain join. If the machine is still domain-joined or actively managed by Intune, policies will reapply on next sync.
- Apply only on personal computers. Do not run on employer-managed devices.
