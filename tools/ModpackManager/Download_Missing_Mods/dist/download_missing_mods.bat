@echo off
setlocal enabledelayedexpansion

:: ============================================================
::  HS2 Missing Mods Downloader - One-click Launcher v3.0
::  Prefers download_missing_mods.exe (standalone, no Python).
::  Falls back to .py if Python 3.8+ is available.
:: ============================================================

title HS2 Mods Downloader

echo.
echo   +----------------------------------------------------+
echo   ^|     HS2 Missing Mods Downloader v3.0                ^|
echo   +----------------------------------------------------+
echo.

:: --- Use .exe if available ---
set RUNNER=
if exist "%~dp0download_missing_mods.exe" (
    set RUNNER="%~dp0download_missing_mods.exe"
    echo [OK] Found standalone exe
    goto :menu
)

:: --- Fallback: find Python ---
echo [INFO] download_missing_mods.exe not found, looking for Python...
echo.
for %%p in (python3 python py) do (
    where %%p >nul 2>&1
    if !errorlevel!==0 (
        set PYTHON=%%p
        goto :found_python
    )
)

echo [ERROR] Neither .exe nor Python found.
echo.
echo Options:
echo   1. Place download_missing_mods.exe in this folder (recommended)
echo   2. Install Python 3.8+ from https://www.python.org/downloads/
echo      Or: winget install Python.Python.3.12
echo.
pause
exit /b 1

:found_python
set RUNNER=%PYTHON% -u "%~dp0download_missing_mods.py"
echo [OK] Found: %PYTHON%
echo.

:: --- Check for aria2c ---
where aria2c >nul 2>&1
if %errorlevel%==0 (
    echo [INFO] aria2c detected - use --aria2c for faster downloads
) else (
    echo [TIP]  Install aria2c for faster downloads:
    echo        winget install aria2.aria2
)
echo.

:: --- Main menu ---
:menu
echo ----------------------------------------------------
echo  Download mode:
echo    [1] Essential only (character mods: clothes/hair/body)
echo    [2] All mods (includes Studio props and Maps)
echo    [3] Dry-run preview (show what would be downloaded)
echo    [4] Custom arguments
echo    [H] Help
echo    [Q] Quit
echo ----------------------------------------------------
set /p choice="Choice [1-4,H,Q]: "

if /i "%choice%"=="1" (set ARGS=& goto :run)
if /i "%choice%"=="2" (set ARGS=--all& goto :run)
if /i "%choice%"=="3" (set ARGS=--dry-run& goto :run)
if /i "%choice%"=="4" (
    echo.
    echo Enter custom arguments, e.g.: --all --aria2c -w 16
    set /p ARGS="Args: "
    goto :run
)
if /i "%choice%"=="h" (
    %RUNNER% --help
    pause
    exit /b 0
)
if /i "%choice%"=="q" (exit /b 0)

echo Invalid choice, try again...
goto :menu

:: --- Run ---
:run
echo.
echo Running: %RUNNER% %ARGS%
echo ----------------------------------------------------
echo.
%RUNNER% %ARGS%

if %errorlevel% neq 0 (
    echo.
    echo [NOTE] Download finished with warnings.
    echo Run again to retry failed items.
)

echo.
echo ====================================================
echo   Done. Press any key to exit.
echo ====================================================
pause >nul
