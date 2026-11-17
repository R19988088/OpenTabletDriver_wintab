# 收尾脚本：把上次整理时被运行中旧实例锁住、没搬走的目录收进 _archive
# 用法：重启电脑后（此时没有 OpenTabletDriver.Slim.exe 在跑）直接运行本脚本，无需管理员。
# 幂等：可以重复跑，已经搬走的不受影响。

$ErrorActionPreference = 'Stop'
$root = 'E:\000 app\OpenTabletDriver'
$slim = Join-Path $root 'slim'
$dst  = Join-Path $root '_archive\builds'
$pkg  = Join-Path $root '_archive\packages'
$logFile = Join-Path $root '_archive\logs\finish-archive.log'

function WriteLog([string]$line) {
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Add-Content -LiteralPath $logFile -Value "$stamp  $line" -Encoding utf8
}

if (-not (Test-Path -LiteralPath $slim)) {
    Write-Host 'slim/ 已经不存在，收尾早就完成了。'
    exit 0
}

$running = @(Get-Process -Name 'OpenTabletDriver.Slim' -ErrorAction SilentlyContinue |
            Where-Object { try { $_.Path -and $_.Path.StartsWith($root) } catch { $false } })
if ($running.Count -gt 0) {
    Write-Host "还有 $($running.Count) 个旧实例在跑，先关掉/重启再来。" -ForegroundColor Yellow
    exit 1
}

New-Item -ItemType Directory -Force -Path $dst, $pkg | Out-Null
WriteLog '--- finish-archive 开始 ---'

# 1) src 里被锁的那层 bin（其它源码早已搬到 ..\src）
$srcLeft = Join-Path $slim 'src'
if (Test-Path -LiteralPath $srcLeft) {
    try {
        Move-Item -LiteralPath $srcLeft -Destination (Join-Path $dst '_slim-src-bin') -Force
        WriteLog "已移动 slim\src -> _archive\builds\_slim-src-bin"
    } catch { WriteLog "移动 slim\src 失败: $($_.Exception.Message)" }
}

# 2) 26 个构建目录
foreach ($d in (Get-ChildItem -LiteralPath $slim -Directory -Force)) {
    try {
        Move-Item -LiteralPath $d.FullName -Destination (Join-Path $dst $d.Name) -Force
        WriteLog "已移动 slim\$($d.Name)"
    } catch { WriteLog "移动 $($d.Name) 失败: $($_.Exception.Message)" }
}

# 3) 散落文件
foreach ($f in (Get-ChildItem -LiteralPath $slim -File -Force)) {
    $target = if ($f.Extension -eq '.zip') { Join-Path $pkg $f.Name } else { Join-Path $dst $f.Name }
    try {
        Move-Item -LiteralPath $f.FullName -Destination $target -Force
        WriteLog "已移动 slim\$($f.Name)"
    } catch { WriteLog "移动 $($f.Name) 失败: $($_.Exception.Message)" }
}

# 4) 空了就把 slim 删掉
if ((Get-ChildItem -LiteralPath $slim -Force | Measure-Object).Count -eq 0) {
    Remove-Item -LiteralPath $slim -Force
    WriteLog '已删除空的 slim\'
}

WriteLog '--- finish-archive 结束 ---'
Get-ChildItem -LiteralPath $root -Force | Select-Object Mode, Name | Format-Table -AutoSize
Write-Host "日志：$logFile"
