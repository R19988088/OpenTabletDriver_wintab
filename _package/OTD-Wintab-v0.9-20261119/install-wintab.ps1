$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'WintabNative\Wintab32.dll'
if (-not (Test-Path -LiteralPath $src)) { Write-Host "找不到 $src"; exit 1 }

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath) -Wait
    exit
}

$dst = Join-Path $env:SystemRoot 'System32\Wintab32.dll'
if (Test-Path -LiteralPath $dst) {
    $bak = Join-Path $env:SystemRoot ('System32\Wintab32.pre-' + (Get-Date -Format 'HHmmss') + '.dll')
    Rename-Item -LiteralPath $dst -NewName (Split-Path $bak -Leaf) -Force
    Write-Host "旧版已备份为 $bak"
}
Copy-Item -LiteralPath $src -Destination $dst -Force
$h = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash.Substring(0,10)
Write-Host "已安装到 $dst"
Write-Host "版本: $h  (期望 912D4A5B84)"
