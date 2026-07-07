@echo off
chcp 65001 >nul 2>&1
setlocal enabledelayedexpansion

:: ============================================================
::  HS2 Missing Mods Downloader — 一键启动脚本 / One-click Launcher
::  Prefers download_missing_mods.exe (standalone).
::  Falls back to download_missing_mods.py (requires Python 3.8+).
::  优先使用exe独立版，如不存在则使用py脚本版（需Python 3.8+）
:: ============================================================

title HS2 Mods Downloader

echo.
echo   ╔══════════════════════════════════════════════════════╗
echo   ║     HS2 Missing Mods Downloader v3.0                ║
echo   ║     HoneySelect2 缺失模组补全工具                    ║
echo   ╚══════════════════════════════════════════════════════╝
echo.

:: --- Use .exe if available ---
set RUNNER=
if exist "%~dp0download_missing_mods.exe" (
    set RUNNER="%~dp0download_missing_mods.exe"
    echo [OK] Using standalone exe / 使用独立exe
    goto :menu
)

:: --- Fallback: find Python ---
echo [INFO] download_missing_mods.exe not found, looking for Python...
echo        未找到exe，尝试使用Python运行...
echo.
set PYTHON=
for %%p in (python3 python py) do (
    where %%p >nul 2>&1
    if !errorlevel!==0 (
        set PYTHON=%%p
        goto :found_python
    )
)

echo [ERROR] Neither .exe nor Python found! / 未找到exe也未找到Python！
echo.
echo Options / 选项:
echo   1. Place download_missing_mods.exe in this folder (recommended)
echo      将 download_missing_mods.exe 放在此文件夹（推荐）
echo   2. Install Python 3.8+: https://www.python.org/downloads/
echo      Or: winget install Python.Python.3.12
echo.
pause
exit /b 1

:found_python
set RUNNER=%PYTHON% -u "%~dp0download_missing_mods.py"
echo [OK] Using: %PYTHON%
echo.

:: --- Check for faster downloader ---
where aria2c >nul 2>&1
if %errorlevel%==0 (
    echo [INFO] aria2c detected - use --aria2c for faster downloads
    echo        检测到aria2c - 使用 --aria2c 加速下载
) else (
    echo [TIP] Install aria2c for 3-5x faster downloads:
    echo       安装aria2c可获得3-5倍下载速度:
    echo       winget install aria2.aria2
)
echo.

:: --- Main menu ---
echo ─────────────────────────────────────────────
echo  Download mode / 下载模式:
echo    [1] Essential only / 仅必需模组 (recommended/推荐, ~13 GB)
echo    [2] All mods / 全部模组 (~64 GB)
echo    [3] Dry-run preview / 预览模式
echo    [4] Custom / 自定义参数
echo    [H] Help / 帮助
echo    [Q] Quit / 退出
echo ─────────────────────────────────────────────
set /p choice="Choice / 请选择 [1-4,H,Q]: "

if /i "%choice%"=="1" (
    set ARGS=
    goto :run
)
if /i "%choice%"=="2" (
    set ARGS=--all
    goto :run
)
if /i "%choice%"=="3" (
    set ARGS=--dry-run
    goto :run
)
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
if /i "%choice%"=="q" (
    exit /b 0
)
echo Invalid choice, try again...
goto :eof

:run
echo.
echo Running: %RUNNER% %ARGS%
echo ─────────────────────────────────────────────
echo.
%RUNNER% %ARGS%

if %errorlevel% neq 0 (
    echo.
    echo [NOTE] Download finished with warnings. / 下载完成，但有些项目可能失败。
    echo Run again to retry failed items: %~nx0
    echo 重新运行可自动重试失败项目。
)

echo.
echo ═══════════════════════════════════════════════
echo   Done! Press any key to exit. / 完成！按任意键退出。
echo ═══════════════════════════════════════════════
pause >nul
