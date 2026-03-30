# Windows MPO Stability Fix

## Purpose

Disable potentially conflicting Multi-Plane Overlay (MPO) behavior by setting:

`HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Dwm\OverlayTestMode = 5 (DWORD)`

This may reduce display instability/flicker issues on some systems.

## Scripts

- `disable-mpo.ps1`: sets `OverlayTestMode` to `5`.
- `restore-mpo-default.ps1`: removes `OverlayTestMode` and returns to OS default behavior.
- `get-mpo-status.ps1`: prints current registry state.

## Run

```powershell
powershell -ExecutionPolicy Bypass -File .\\scripts\\disable-mpo.ps1
```

```powershell
powershell -ExecutionPolicy Bypass -File .\\scripts\\get-mpo-status.ps1
```

```powershell
powershell -ExecutionPolicy Bypass -File .\\scripts\\restore-mpo-default.ps1
```

## Risk Notice

- Requires administrator privileges (`HKLM`).
- A restart or sign-out may be required for full effect.
- Apply only in authorized troubleshooting scenarios.
