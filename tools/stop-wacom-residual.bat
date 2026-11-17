@echo off
setlocal EnableExtensions

rem Stop Wacom user-mode services and processes so Wintab can own tablet input.
net session >nul 2>&1
if errorlevel 1 (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo Stopping Wacom services...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$items = Get-CimInstance Win32_Service | Where-Object { $_.Name -match 'Wacom|Wac' -or $_.DisplayName -match 'Wacom|Wac' }; foreach ($item in $items) { Write-Host ('  ' + $item.Name + ' [' + $item.State + ']'); if ($item.State -ne 'Stopped') { Stop-Service -Name $item.Name -Force -ErrorAction SilentlyContinue }; Set-Service -Name $item.Name -StartupType Disabled -ErrorAction SilentlyContinue }"

echo Stopping Wacom user processes...
for %%P in (Wacom_Tablet Wacom_TabletUser WacomHost Wacom_Touch Wacom_TouchUser PenTablet WTabletServicePro) do (
    taskkill /F /IM "%%P.exe" >nul 2>&1
)

echo.
echo Wacom residual services and user processes were stopped.
echo Restart OpenTabletDriver, then use the Wintab output mode.
echo To restore Wacom services, set their startup type back in services.msc.
pause
exit /b 0
