@echo off
setlocal EnableExtensions
chcp 65001 >nul 2>&1

rem ---- self-elevate ----
net session >nul 2>&1
if errorlevel 1 (
    echo 正在请求管理员权限...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "ROOT=%~dp0"
set "SRC=%ROOT%WintabNative\Wintab32.dll"
set "S32=%SystemRoot%\System32\Wintab32.dll"
set "WOW=%SystemRoot%\SysWOW64\Wintab32.dll"
set "BAK=%ROOT%WintabBackup"

echo.
echo === 管理员权限已确认 ===
echo.

if not exist "%SRC%" (
    echo [错误] 找不到 %SRC%
    pause & exit /b 1
)
for %%A in ("%SRC%") do echo 源文件: %%~zA 字节

echo.
echo [1/4] 停止 OpenTabletDriver
taskkill /F /IM OpenTabletDriver.UX.Wpf.exe >nul 2>&1
taskkill /F /IM OpenTabletDriver.Daemon.exe >nul 2>&1
taskkill /F /IM OpenTabletDriver.Console.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo [2/4] 备份原件到 WintabBackup
if not exist "%BAK%" mkdir "%BAK%"
if exist "%S32%" if not exist "%BAK%\Wintab32.system32.dll" copy /Y "%S32%" "%BAK%\Wintab32.system32.dll" >nul
if exist "%WOW%" if not exist "%BAK%\Wintab32.syswow64.dll" copy /Y "%WOW%" "%BAK%\Wintab32.syswow64.dll" >nul

echo [3/4] 覆盖 System32 和 SysWOW64
call :copy "%SRC%" "%S32%"
call :copy "%SRC%" "%WOW%"

echo [4/4] 校验
call :hash "%SRC%"  SRC
call :hash "%S32%"  S32
call :hash "%WOW%"  WOW
echo.
if /I "%SRC_HASH%"=="%S32_HASH%" (echo System32  OK  %S32_HASH%) else (echo System32  不一致! 需要 %SRC_HASH%)
if /I "%SRC_HASH%"=="%WOW_HASH%" (echo SysWOW64  OK  %WOW_HASH%) else (echo SysWOW64  不一致!)
echo.
echo 完成。请重新打开 OpenTabletDriver 和绘画软件。
pause
exit /b 0

:copy
attrib -R -S -H "%~2" >nul 2>&1
copy /Y "%~1" "%~2" >nul 2>&1
if errorlevel 1 (
    echo   [失败] %~2
    echo          文件可能被占用，请关闭所有使用 Wintab 的程序后重试。
) else (
    echo   [成功] %~2
)
exit /b 0

:hash
for /f "skip=1 tokens=*" %%H in ('certutil -hashfile "%~1" MD5 2^>nul') do (
    set "%~2_HASH=%%H"
    goto :eof
)
exit /b 0
