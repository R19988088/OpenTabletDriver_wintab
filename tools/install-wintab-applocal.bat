@echo off
setlocal EnableExtensions

rem Drop the Wintab shim into a single application's own folder.
rem Windows resolves "Wintab32.dll" from the application directory BEFORE
rem System32, so this takes effect without touching the system at all.
rem
rem Usage:  install-wintab-applocal.bat "C:\Program Files\Adobe\Adobe Photoshop 2026" [x64^|x86]
rem
rem x64 is the default; 32-bit applications (PaintTool SAI 1.x, some older
rem editors) need x86.

if "%~1"=="" (
    echo Usage: %~nx0 "path to the application folder" [x64^|x86]
    pause
    exit /b 1
)

set "TARGET=%~1"
set "ARCH=%~2"
if "%ARCH%"=="" set "ARCH=x64"

set "ROOT=%~dp0"
if "%ARCH%"=="x86" (
    set "SRC=%ROOT%portable-build\WintabNative\Wintab32_x86.dll"
    set "BACKUPNAME=Wintab32_x86.original.dll"
) else (
    set "SRC=%ROOT%portable-build\WintabNative\Wintab32.dll"
    set "BACKUPNAME=Wintab32.original.dll"
)

if not exist "%TARGET%\" (
    echo ERROR: folder not found: "%TARGET%"
    pause
    exit /b 1
)
if not exist "%SRC%" (
    echo ERROR: shim not found: "%SRC%"
    echo Build it first with build-wintab.ps1
    pause
    exit /b 1
)

if exist "%TARGET%\Wintab32.dll" if not exist "%TARGET%\%BACKUPNAME%" (
    echo Backing up the application's existing Wintab32.dll...
    copy /Y "%TARGET%\Wintab32.dll" "%TARGET%\%BACKUPNAME%" >nul
)

copy /Y "%SRC%" "%TARGET%\Wintab32.dll" >nul
if errorlevel 1 (
    echo ERROR: could not write to "%TARGET%\Wintab32.dll"
    echo Close the application first, or run this as administrator.
    pause
    exit /b 1
)

echo.
echo Installed (%ARCH%): "%TARGET%\Wintab32.dll"
echo Restart the application. To undo, restore %BACKUPNAME% or delete Wintab32.dll.
pause
exit /b 0
