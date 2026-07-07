@echo off
chcp 65001 >nul 2>&1
:: ============================================================
::  Build standalone .exe from source
::  Requires: Python 3.8+ with PyInstaller (pip install pyinstaller)
:: ============================================================

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
    pause
    exit /b 1
)

echo.
echo Build complete! / 构建完成！
echo Run setup.bat to install into a ModResolver directory.
echo.
pause
