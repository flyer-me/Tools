@echo off
chcp 65001 >nul 2>&1
setlocal enabledelayedexpansion

:: ============================================================
::  HS2 Missing Mods Downloader — 一键环境安装 / One-Click Setup
::  1. Downloads latest ModResolver from GitHub
::  2. Extracts it to the current directory
::  3. Installs the downloader tool into the ModResolver folder
:: ============================================================

title ModResolver + Mods Downloader Setup

set "WORK_DIR=%~dp0"
set "DIST_DIR=%~dp0dist"

echo.
echo   ╔══════════════════════════════════════════════════════╗
echo   ║   HS2 Mods Downloader — Setup / 环境安装             ║
echo   ╚══════════════════════════════════════════════════════╝
echo.
echo This will download ModResolver Portable and install the
echo missing-mods downloader tool alongside it.
echo.
echo 这将下载 ModResolver 便携版并安装模组补全工具。
echo.

:: ── Check for existing ModResolver ──────────────────────
set "MR_DIR="
if exist "%WORK_DIR%..\ModResolver.exe" (
    for %%F in ("%WORK_DIR%..") do set "MR_DIR=%%~fF"
    echo [OK] Found ModResolver: !MR_DIR!
    goto :skip_download
)
if exist "%WORK_DIR%ModResolver V1.2.7 Portable\ModResolver.exe" (
    set "MR_DIR=%WORK_DIR%ModResolver V1.2.7 Portable"
    echo [OK] Found ModResolver: !MR_DIR!
    goto :skip_download
)
:: Check all ModResolver* directories
for /d %%d in ("%WORK_DIR%ModResolver*") do (
    if exist "%%d\ModResolver.exe" (
        set "MR_DIR=%%~fd"
        echo [OK] Found ModResolver: !MR_DIR!
        goto :skip_download
    )
)

:: ── Download ModResolver ────────────────────────────────
echo.
echo [1/3] Fetching latest ModResolver release info...
echo        获取最新 ModResolver 发布信息...
echo.

set "API_URL=https://api.github.com/repos/XrmSeries/ModResolver/releases/latest"
set "DOWNLOAD_URL="

:: Use PowerShell to get the download URL (more reliable than curl on Windows)
powershell -NoProfile -Command ^
    "$r=Invoke-RestMethod -Uri '%API_URL%' -Headers @{'User-Agent'='Setup/1.0'};" ^
    "$a=$r.assets | Where-Object {$_.name -like '*.rar'} | Select-Object -First 1;" ^
    "Write-Host \"Version: $($r.tag_name)\";" ^
    "Write-Host \"File: $($a.name) ($([math]::Round($a.size/1MB,1)) MB)\";" ^
    "Write-Host \"URL: $($a.browser_download_url)\";" ^
    "[System.Environment]::SetEnvironmentVariable('DL_URL',$a.browser_download_url,'Process');" ^
    "[System.Environment]::SetEnvironmentVariable('DL_FILE',$a.name,'Process')"

if "%DL_URL%"=="" (
    echo [ERROR] Could not get download URL from GitHub API.
    echo         Please download ModResolver manually from:
    echo         https://github.com/XrmSeries/ModResolver/releases
    pause
    exit /b 1
)

echo.
echo [2/3] Downloading %DL_FILE% ...
echo        下载中，约60MB，请稍候...
echo.

set "DL_PATH=%WORK_DIR%%DL_FILE%"
powershell -NoProfile -Command ^
    "Invoke-WebRequest -Uri $env:DL_URL -OutFile $env:DL_PATH -Headers @{'User-Agent'='Setup/1.0'}"

if not exist "%DL_PATH%" (
    echo [ERROR] Download failed. Please download manually.
    pause
    exit /b 1
)
echo [OK] Downloaded: %DL_FILE%

:: ── Extract ─────────────────────────────────────────────
echo.
echo [3/3] Extracting ...
echo        解压中...

set "EXTRACT_DIR=%WORK_DIR%"
set "EXTRACT_OK="

:: Try WinRAR
if exist "%ProgramFiles%\WinRAR\WinRAR.exe" (
    "%ProgramFiles%\WinRAR\WinRAR.exe" x -y "%DL_PATH%" "%EXTRACT_DIR%" >nul 2>&1
    if !errorlevel!==0 set "EXTRACT_OK=1"
)
:: Try 7-Zip
if not defined EXTRACT_OK if exist "%ProgramFiles%\7-Zip\7z.exe" (
    "%ProgramFiles%\7-Zip\7z.exe" x -y -o"%EXTRACT_DIR%" "%DL_PATH%" >nul 2>&1
    if !errorlevel!==0 set "EXTRACT_OK=1"
)
:: Try bandizip
if not defined EXTRACT_OK if exist "%ProgramFiles%\Bandizip\Bandizip.exe" (
    "%ProgramFiles%\Bandizip\Bandizip.exe" x -o:"%EXTRACT_DIR%" -y "%DL_PATH%" >nul 2>&1
    if !errorlevel!==0 set "EXTRACT_OK=1"
)

if not defined EXTRACT_OK (
    echo [WARN] No archive tool found (WinRAR/7-Zip/Bandizip).
    echo         Please manually extract %DL_FILE% to this folder.
    echo         请手动解压 %DL_FILE% 到此文件夹。
    echo.
    echo         Then re-run this script to complete setup.
    echo         解压后重新运行此脚本完成安装。
    pause
    exit /b 0
)

echo [OK] Extracted.

:: Find the extracted ModResolver directory
for /d %%d in ("%EXTRACT_DIR%ModResolver*") do (
    if exist "%%d\ModResolver.exe" (
        set "MR_DIR=%%~fd"
    )
)

if not defined MR_DIR (
    echo [WARN] Could not auto-detect extracted ModResolver folder.
    echo         Please copy the files from dist\ into your ModResolver folder.
    pause
    exit /b 0
)

:skip_download

:: ── Install downloader tool ─────────────────────────────
echo.
echo Installing downloader tool to ModResolver directory...
echo 安装下载工具到 ModResolver 目录...
echo   From: %DIST_DIR%
echo   To:   %MR_DIR%

copy /Y "%DIST_DIR%\download_missing_mods.exe" "%MR_DIR%\" >nul 2>&1
copy /Y "%DIST_DIR%\download_missing_mods.bat" "%MR_DIR%\" >nul 2>&1

if exist "%MR_DIR%\download_missing_mods.exe" (
    echo [OK] Installed successfully! / 安装成功！
) else (
    echo [ERROR] Copy failed. Please manually copy dist\* to your ModResolver folder.
    pause
    exit /b 1
)

:: ── Done ────────────────────────────────────────────────
echo.
echo ═══════════════════════════════════════════════════════
echo   Setup complete! / 安装完成！
echo.
echo   Next step / 下一步:
echo     1. Run ModResolver.exe - click [Scan] to generate report
echo        运行 ModResolver.exe - 点击 Scan 生成报告
echo     2. Run download_missing_mods.bat to download missing mods
echo        运行 download_missing_mods.bat 下载缺失模组
echo.
echo   Folder / 目录: %MR_DIR%
echo ═══════════════════════════════════════════════════════
echo.
pause
