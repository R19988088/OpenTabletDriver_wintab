# OTD 开机清理器（临时）
# 目的：每次登录后保证系统里只有一份当前安装的 OTD Slim，
#       顺手修好 %LOCALAPPDATA%\OTDWintab 的死联接，并清掉指向已删构建的旧自启项。
#
# 安装（管理员）：
#   schtasks /create /tn "OTD Startup Cleanup" /rl highest /sc onlogon /delay 0000:20 /f ^
#     /tr "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"E:\000 app\OpenTabletDriver\tools\otd-startup-cleanup.ps1\""
# 卸载：
#   schtasks /delete /tn "OTD Startup Cleanup" /f
#
# 也可以直接跑一次：powershell -ExecutionPolicy Bypass -File tools\otd-startup-cleanup.ps1

param(
    # 允许保留的安装目录（这下面的 OpenTabletDriver.Slim*.exe 不杀）
    [string]$InstallDir = 'D:\app\OpenTabletDriver',
    # 已知的安装数据目录，用于重建 OTDWintab 联接
    [string]$DataDir = 'D:\app\OpenTabletDriver\data',
    # 只检查/修复，不杀进程
    [switch]$DryRun
)

$ErrorActionPreference = 'Continue'
$logPath = Join-Path $env:LOCALAPPDATA 'OTD-startup-cleanup.log'

function Log([string]$text) {
    $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $text
    Write-Output $line
    try { Add-Content -LiteralPath $logPath -Value $line -Encoding utf8 } catch { }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Log "===== 开始（pid=$PID，admin=$isAdmin） ====="

# ---------- 1. 修 OTDWintab 联接 ----------
$link = Join-Path $env:LOCALAPPDATA 'OTDWintab'
try {
    $item = Get-Item -LiteralPath $link -Force -ErrorAction Stop
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        $target = $item.Target
        if (-not (Test-Path -LiteralPath $target)) {
            if ($DryRun) {
                Log "联接指向不存在的 $target（只检查，不修）"
            } else {
                [IO.Directory]::Delete($link)
                if (Test-Path -LiteralPath $DataDir) {
                    cmd /c mklink /J "$link" "$DataDir" | Out-Null
                    Log "死联接（→ $target）已重建到 $DataDir"
                } else {
                    Log "死联接（→ $target）已删除，但 $DataDir 不存在，没重建"
                }
            }
        } else {
            Log "联接正常 → $target"
        }
    } else {
        Log "OTDWintab 是普通目录（不是联接）：shim 与主程序会各看一份 feel.json"
    }
} catch {
    Log "OTDWintab 不存在或不可读：$($_.Exception.Message)"
}

# ---------- 2. 清旧实例 ----------
$keepRoot = (Resolve-Path -LiteralPath $InstallDir -ErrorAction SilentlyContinue)
if ($keepRoot) { $keepRoot = $keepRoot.Path.TrimEnd('\') }

$procs = Get-CimInstance Win32_Process -Filter "Name LIKE 'OpenTabletDriver.Slim%'" -ErrorAction SilentlyContinue
if (-not $procs) { Log '没有 OTD 进程' }

$kept = 0; $killed = 0; $failed = @()
foreach ($p in $procs) {
    $exe = $p.ExecutablePath
    $isKeep = $false
    if ($exe -and $keepRoot) {
        $dir = [IO.Path]::GetDirectoryName($exe)
        if ($dir -and $dir.TrimEnd('\') -eq $keepRoot -and (Test-Path -LiteralPath $exe)) { $isKeep = $true }
    }
    if ($isKeep) {
        $kept++
        Log "保留 pid=$($p.ProcessId) $exe"
        continue
    }

    $why = if (-not $exe) { '路径不可读' } elseif (-not (Test-Path -LiteralPath $exe)) { 'exe 已不存在' } else { "不在 $keepRoot" }
    if ($DryRun) {
        Log "将终止 pid=$($p.ProcessId) $exe（$why）"
        continue
    }

    $ok = $false
    try {
        & taskkill.exe /F /PID $p.ProcessId 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { $ok = $true }
    } catch { }
    if (-not $ok) {
        try {
            $r = Invoke-CimMethod -InputObject $p -MethodName Terminate -ErrorAction Stop
            if ($r.ReturnValue -eq 0) { $ok = $true }
        } catch { }
    }
    if ($ok) {
        $killed++
        Log "已终止 pid=$($p.ProcessId) $exe（$why）"
    } else {
        $failed += $p.ProcessId
        Log "终止失败 pid=$($p.ProcessId) $exe（$why）—— 通常是跨会话/内核等待的死实例，重启后消失"
    }
}

Log "进程：保留 $kept，终止 $killed，失败 $($failed.Count)"

# ---------- 3. 清失效自启项（只删指向不存在文件的 OTD 项）----------
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Test-Path $runKey) {
    $props = (Get-ItemProperty $runKey).PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' }
    foreach ($prop in $props) {
        $cmd = [string]$prop.Value
        if ($cmd -notmatch 'OpenTabletDriver') { continue }

        # 取出第一个带引号或不带引号的可执行路径
        if ($cmd -match '^\s*"([^"]+\.exe)"') { $candidate = $matches[1] }
        elseif ($cmd -match '^\s*([^\s]+\.exe)') { $candidate = $matches[1] }
        else { continue }

        if (-not (Test-Path -LiteralPath $candidate)) {
            if ($DryRun) {
                Log "将删除失效自启项 [$($prop.Name)] → $candidate"
            } else {
                Remove-ItemProperty -Path $runKey -Name $prop.Name -Force -ErrorAction SilentlyContinue
                Log "已删除失效自启项 [$($prop.Name)] → $candidate"
            }
        } else {
            Log "自启项 [$($prop.Name)] 目标存在，保留 → $candidate"
        }
    }
}

Log '===== 结束 ====='
