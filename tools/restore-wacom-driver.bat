@echo off
setlocal EnableExtensions
chcp 65001 >nul 2>&1

set "DIR=%~dp0"
set "BACKUP=E:\000 app\wacom++\_runtime\wacom-driver-backup"

net session >nul 2>&1
if errorlevel 1 goto need_admin

echo [1/3] Stopping OpenTabletDriver...
taskkill /F /IM OpenTabletDriver.UX.Wpf.exe >nul 2>&1
taskkill /F /IM OpenTabletDriver.Daemon.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo [2/3] Reinstalling Wacom HID router driver...
if not exist "%BACKUP%\wachidrouter_pro.inf" goto no_backup
pnputil /add-driver "%BACKUP%\wachidrouter_pro.inf" /install
if errorlevel 1 goto failed

pnputil /add-driver "%BACKUP%\wacomrouterfilter.inf" /install

echo [3/3] Rescanning hardware...
pnputil /scan-devices

echo.
echo Done. Unplug and replug the tablet, then start OpenTabletDriver.
pause
exit /b 0

:need_admin
echo [ERROR] Run as administrator.
pause
exit /b 1

:no_backup
echo [ERROR] Driver backup not found: "%BACKUP%"
pause
exit /b 1

:failed
echo [ERROR] Driver installation failed.
pause
exit /b 1
