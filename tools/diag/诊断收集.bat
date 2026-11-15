@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
set "LOGDIR=%LOCALAPPDATA%\OTDWintab"
set "REPORT=%ROOT%diag-report.txt"
set "MARK=%LOGDIR%\diag-installed.flag"
set "DLL=OpenTabletDriver.Wintab.dll"
set "COUNT=0"

rem Two-phase diagnostic for the Wintab pressure chain.
rem   Phase 1 (marker missing): copy the diagnostic plugin into this package.
rem   Phase 2 (marker present): merge every log into ONE plain text file here.
rem
rem No PowerShell and no zip on purpose: Windows PowerShell 5.1 on some machines is
rem missing modules such as Get-FileHash and Compress-Archive, which made earlier
rem collectors fail silently. dir, type and certutil always exist, and the whole file
rem stays ASCII so a GBK console cannot mis-parse it.

if not exist "%ROOT%OpenTabletDriver.UX.Wpf.exe" goto bad_place
if not exist "%ROOT%%DLL%" goto bad_files

if not exist "%MARK%" goto install
goto collect

:bad_place
echo.
echo [ERROR] Put this script in the portable package folder,
echo         next to OpenTabletDriver.UX.Wpf.exe.
echo         Current folder: %ROOT%
echo.
pause
exit /b 1

:bad_files
echo.
echo [ERROR] %DLL% must sit next to this script.
echo.
pause
exit /b 1

:install
echo.
echo === Step 1 of 2: install the diagnostic plugin (once) ===
echo.
if not exist "%LOGDIR%" mkdir "%LOGDIR%" 2>nul
if not exist "%ROOT%Plugins\OpenTabletDriver.Wintab" mkdir "%ROOT%Plugins\OpenTabletDriver.Wintab" 2>nul
if not exist "%ROOT%userdata\Plugins\OpenTabletDriver.Wintab" mkdir "%ROOT%userdata\Plugins\OpenTabletDriver.Wintab" 2>nul

copy /Y "%ROOT%%DLL%" "%ROOT%Plugins\OpenTabletDriver.Wintab\%DLL%" >nul 2>&1
copy /Y "%ROOT%%DLL%" "%ROOT%userdata\Plugins\OpenTabletDriver.Wintab\%DLL%" >nul 2>&1

if not exist "%ROOT%Plugins\OpenTabletDriver.Wintab\%DLL%" goto copy_failed
if not exist "%ROOT%userdata\Plugins\OpenTabletDriver.Wintab\%DLL%" goto copy_failed

echo Replaced:
echo   Plugins\OpenTabletDriver.Wintab\%DLL%
echo   userdata\Plugins\OpenTabletDriver.Wintab\%DLL%
echo.
echo Now do this:
echo   1) Close OpenTabletDriver completely (tray -^> exit; in Task Manager check
echo      OpenTabletDriver.Daemon.exe is gone too)
echo   2) Start OpenTabletDriver.UX.Wpf.exe again and wait for the tray icon
echo   3) Open SAI and draw a few strokes, hard and soft, plus some hovering - keep SAI open
echo   4) Double-click this script once more
echo.
echo On the second run it writes ONE file here (no zip):
echo   %REPORT%
echo.
echo installed > "%MARK%"
pause
exit /b 0

:copy_failed
echo.
echo [ERROR] Could not copy the plugin. Close OpenTabletDriver first, then run again.
echo.
pause
exit /b 1

:collect
echo.
echo === Step 2 of 2: collect into one txt ===
echo.
if exist "%REPORT%" del /F "%REPORT%" >nul 2>&1

(
echo OTD Wintab diagnostic report
echo generated: %DATE% %TIME%
echo package dir: %ROOT%
echo log dir: %LOGDIR%
echo.
echo ===== system Wintab32.dll =====
dir /-c "%SystemRoot%\System32\Wintab32.dll" 2>nul
dir /-c "%SystemRoot%\SysWOW64\Wintab32.dll" 2>nul
echo.
echo ===== bundled WintabNative =====
dir /-c "%ROOT%WintabNative" 2>nul
echo.
echo ===== package plugins =====
dir /-c "%ROOT%Plugins\OpenTabletDriver.Wintab" 2>nul
dir /-c "%ROOT%userdata\Plugins\OpenTabletDriver.Wintab" 2>nul
) >> "%REPORT%"

echo. >> "%REPORT%"
echo ===== sha256 ===== >> "%REPORT%"
certutil -hashfile "%SystemRoot%\System32\Wintab32.dll" SHA256 >> "%REPORT%" 2>nul
certutil -hashfile "%SystemRoot%\SysWOW64\Wintab32.dll" SHA256 >> "%REPORT%" 2>nul
certutil -hashfile "%ROOT%WintabNative\Wintab32.dll" SHA256 >> "%REPORT%" 2>nul
certutil -hashfile "%ROOT%WintabNative\Wintab32_x86.dll" SHA256 >> "%REPORT%" 2>nul

echo. >> "%REPORT%"
echo ===== settings.json ===== >> "%REPORT%"
if exist "%ROOT%userdata\settings.json" type "%ROOT%userdata\settings.json" >> "%REPORT%"

echo. >> "%REPORT%"
echo ===== logs (newest 30 files) ===== >> "%REPORT%"
if not exist "%LOGDIR%" echo [no log folder yet - the diagnostic plugin has not run] >> "%REPORT%"
if exist "%LOGDIR%" for /f "delims=" %%F in ('dir /b /o-d "%LOGDIR%\*.log" 2^>nul') do call :add_log "%%F"

echo.
for %%A in ("%REPORT%") do echo Done. Size: %%~zA bytes
echo.
echo ==========================================
echo Send back this ONE file (no zip needed):
echo   %REPORT%
echo ==========================================
echo.
pause
exit /b 0

:add_log
if %COUNT% GEQ 30 exit /b 0
for %%A in ("%REPORT%") do if %%~zA GEQ 10000000 exit /b 0
set /a COUNT+=1
echo. >> "%REPORT%"
echo ----- %~1 ----- >> "%REPORT%"
type "%LOGDIR%\%~1" >> "%REPORT%"
exit /b 0
