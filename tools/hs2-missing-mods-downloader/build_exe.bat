@echo off
chcp 65001 >nul 2>&1
:: ============================================================
::  Build standalone .exe from source
::  Requires: Python 3.8+ with PyInstaller (pip install pyinstaller)
::
::  Usage: build_exe.bat [--no-pause]
::         --no-pause   automation mode, do not wait for a key press
:: ============================================================

set "NOPAUSE="
if /i "%~1"=="--no-pause" set "NOPAUSE=1"

cd /d "%~dp0"

echo.
echo   Building download_missing_mods.exe ...
echo.

:: Check PyInstaller
python -c "import PyInstaller" >nul 2>&1
if %errorlevel% neq 0 (
    echo [INFO] Installing PyInstaller...
    pip install pyinstaller
)

:: Build
python -m PyInstaller ^
    --onefile ^
    --name "download_missing_mods" ^
    --clean ^
    --distpath "./dist" ^
    --workpath "./build" ^
    "%~dp0src\download_missing_mods.py"

:: Cleanup
rmdir /s /q build 2>nul
del download_missing_mods.spec 2>nul

if exist "%~dp0dist\download_missing_mods.exe" (
    echo.
    echo [OK] Built: dist\download_missing_mods.exe
    for %%f in ("%~dp0dist\download_missing_mods.exe") do echo   Size: %%~zf bytes
) else (
    echo [ERROR] Build failed.
    if not defined NOPAUSE pause
    exit /b 1
)

echo.
echo Build complete!
echo Run setup.bat to install into a ModResolver directory.
echo.
if not defined NOPAUSE pause
