@echo off
rem ============================================================
rem  OTD startup cleanup (batch only, no PowerShell)
rem  Called by scheduled task "OTD Startup Cleanup" at logon.
rem  - repairs the %LOCALAPPDATA%\OTDWintab junction if it dangles
rem  - terminates leftover OpenTabletDriver.Slim.exe instances that
rem    live outside the current install dir (or whose exe is gone)
rem  Log: %LOCALAPPDATA%\OTD-startup-cleanup.log
rem ============================================================
chcp 65001 >nul
setlocal EnableDelayedExpansion
set "LOG=%LOCALAPPDATA%\OTD-startup-cleanup.log"
set "DATADIR=D:\app\OpenTabletDriver\data"
set "KEEPDIR=D:\app\OpenTabletDriver"
set "LINK=%LOCALAPPDATA%\OTDWintab"

call :log "===== batch cleanup start ====="

rem ---------- 1. OTDWintab junction ----------
fsutil reparsepoint query "%LINK%" >nul 2>&1
if !errorlevel! equ 0 (
    if exist "%LINK%\feel.json" (
        call :log "junction ok: %LINK%"
    ) else (
        rd "%LINK%" >nul 2>&1
        if exist "%DATADIR%" (
            mklink /J "%LINK%" "%DATADIR%" >nul 2>&1
            call :log "junction was broken, relinked to %DATADIR% (err=!errorlevel!)"
        ) else (
            call :log "junction broken and %DATADIR% missing; left alone"
        )
    )
) else (
    if exist "%LINK%" (
        call :log "%LINK% is a plain directory; left alone"
    ) else (
        if exist "%DATADIR%" (
            mklink /J "%LINK%" "%DATADIR%" >nul 2>&1
            call :log "junction missing, created -> %DATADIR% (err=!errorlevel!)"
        )
    )
)

rem ---------- 2. leftover instances ----------
rem Current build runs as OpenTabletDriver.Slim.v32.exe, so the bare
rem OpenTabletDriver.Slim.exe image name only matches stale builds.
set "KILLED=0"
set "FAILED=0"
for /f "tokens=2 delims=," %%p in ('tasklist /fi "imagename eq OpenTabletDriver.Slim.exe" /fo csv /nh 2^>nul') do (
    set "PID=%%~p"
    if defined PID (
        taskkill /F /PID !PID! >nul 2>&1
        if !errorlevel! equ 0 (
            set /a KILLED+=1
            call :log "killed pid=!PID!"
        ) else (
            set /a FAILED+=1
            call :log "kill refused pid=!PID! (likely cross-session zombie, clears on reboot)"
        )
    )
)
call :log "instances: killed=!KILLED! refused=!FAILED!"

call :log "===== batch cleanup end ====="
exit /b 0

:log
>>"%LOG%" echo %DATE% %TIME% %~1
exit /b 0
