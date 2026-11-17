@echo off
setlocal EnableExtensions

rem Install the bundled Wintab shim for 64-bit and 32-bit applications.
net session >nul 2>&1
if errorlevel 1 (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "ROOT=%~dp0"
set "SRC64=%ROOT%WintabNative\Wintab32.dll"
set "SRC32=%ROOT%WintabNative\Wintab32_x86.dll"
set "SYS32=%SystemRoot%\System32\Wintab32.dll"
set "SYSWOW=%SystemRoot%\SysWOW64\Wintab32.dll"
set "BACKUP=%ROOT%WintabBackup"

if not exist "%SRC64%" (
    echo ERROR: Missing x64 shim: "%SRC64%"
    pause
    exit /b 1
)
if not exist "%SRC32%" (
    echo ERROR: Missing x86 shim: "%SRC32%"
    pause
    exit /b 1
)

echo Stopping OpenTabletDriver...
taskkill /F /IM OpenTabletDriver.UX.Wpf.exe >nul 2>&1
taskkill /F /IM OpenTabletDriver.Daemon.exe >nul 2>&1
taskkill /F /IM OpenTabletDriver.Console.exe >nul 2>&1
timeout /t 2 /nobreak >nul

if not exist "%BACKUP%" mkdir "%BACKUP%"
if exist "%SYS32%" if not exist "%BACKUP%\Wintab32.system32.dll" copy /Y "%SYS32%" "%BACKUP%\Wintab32.system32.dll" >nul
if exist "%SYSWOW%" if not exist "%BACKUP%\Wintab32.syswow64.dll" copy /Y "%SYSWOW%" "%BACKUP%\Wintab32.syswow64.dll" >nul

echo Installing x64 shim to System32...
call :install "%SRC64%" "%SYS32%"
echo Installing x86 shim to SysWOW64...
call :install "%SRC32%" "%SYSWOW%"

echo.
if exist "%SYS32%" (echo System32 install complete.) else (echo ERROR: System32 install failed.)
if exist "%SYSWOW%" (echo SysWOW64 install complete.) else (echo ERROR: SysWOW64 install failed.)
echo.
echo Restart OpenTabletDriver and your Wintab application.
pause
exit /b 0

:install
attrib -R -S -H "%~2" >nul 2>&1
copy /Y "%~1" "%~2" >nul 2>&1
if errorlevel 1 (
    echo ERROR: Could not copy to "%~2"
    exit /b 1
)
echo OK: "%~2"
exit /b 0
