# 收尾脚本：删掉上次整理时被运行中旧实例锁住、搬不走的旧构建目录
# 用法：重启电脑后（此时没有 OpenTabletDriver.Slim.exe 在跑）直接运行本脚本，无需管理员。
# 说明：按"只保留最近三个版本"的策略，这些目录属于早期中间构建，直接删除，不再归档。
# 幂等：可以重复跑。

$ErrorActionPreference = 'Stop'
$root = 'E:\000 app\OpenTabletDriver'
$slim = Join-Path $root 'slim'
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
    Write-Host "还有 $($running.Count) 个旧实例在跑，先重启再来。" -ForegroundColor Yellow
    exit 1
}

WriteLog '--- finish-archive 开始（删除模式）---'
$gb0 = ((Get-ChildItem -LiteralPath $slim -Recurse -File -Force -ErrorAction SilentlyContinue |
         Measure-Object Length -Sum).Sum) / 1GB
WriteLog ("待删除体积约 {0:N1} GB" -f $gb0)

try {
    Remove-Item -LiteralPath $slim -Recurse -Force
    WriteLog '已删除 slim\（含 26 个旧构建目录与 src 残留的 bin）'
} catch {
    WriteLog "删除 slim\ 失败: $($_.Exception.Message)"
}

$gb1 = $null
try { $gb1 = ((Get-ChildItem -LiteralPath $slim -Recurse -File -Force -ErrorAction SilentlyContinue |
        Measure-Object Length -Sum).Sum) / 1GB } catch {}
if ($null -ne $gb1 -and $gb1 -gt 0) { WriteLog ("仍有 {0:N1} GB 未删掉，可能需要重启后再跑一次" -f $gb1) }

WriteLog '--- finish-archive 结束 ---'
Get-ChildItem -LiteralPath $root -Force | Select-Object Mode, Name | Format-Table -AutoSize
Write-Host "日志：$logFile"
