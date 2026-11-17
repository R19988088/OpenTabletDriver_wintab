# Installs the already-built Wintab32 shim from portable-build\WintabNative into
# System32 (x64) and SysWOW64 (x86). Backs up whatever is currently installed,
# then verifies the copies by hash. Must run elevated.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File install-wintab.ps1

$root = $PSScriptRoot
$native = Join-Path $root "portable-build\WintabNative"
$backup = Join-Path $root "WintabBackup"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logPath = Join-Path $root "wintab-install.log"

New-Item -ItemType Directory -Force -Path $backup | Out-Null
Set-Content -LiteralPath $logPath -Value "install started $stamp" -Encoding utf8

function Say([string]$text) {
    Write-Host $text
    Add-Content -LiteralPath $logPath -Value $text -Encoding utf8
}

function Install-One([string]$src, [string]$dst) {
    $currentHash = if (Test-Path -LiteralPath $dst) { (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash } else { "" }
    $srcHash = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash
    if ($currentHash -eq $srcHash) {
        Say "already current: $dst"
        return $true
    }
    if (Test-Path -LiteralPath $dst) {
        $bak = Join-Path $backup ("Wintab32.before-" + $stamp + "-" + (Split-Path $dst -Leaf) + (Split-Path (Split-Path $dst -Parent) -Leaf) + ".dll")
        Copy-Item -LiteralPath $dst -Destination $bak -Force
        Say "backed up $dst -> $bak"
    }
    try {
        Copy-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop
    }
    catch {
        Say "in-place copy failed ($($_.Exception.Message)); moving the old file aside"
        $old = "$dst.old-$stamp"
        & attrib.exe -R $dst | Out-Null
        & takeown.exe /F $dst /A | Out-Null
        & icacls.exe $dst /grant "*S-1-5-32-544:F" /Q | Out-Null
        Move-Item -LiteralPath $dst -Destination $old -Force
        Copy-Item -LiteralPath $src -Destination $dst -Force
    }
    $newHash = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash
    if ($newHash -eq $srcHash) { Say "OK: $dst"; return $true }
    Say "MISMATCH: $dst"
    return $false
}

$ok64 = Install-One (Join-Path $native "Wintab32.dll") "C:\Windows\System32\Wintab32.dll"
$ok86 = Install-One (Join-Path $native "Wintab32_x86.dll") "C:\Windows\SysWOW64\Wintab32.dll"

if ($ok64 -and $ok86) { Say "RESULT: both architectures installed" }
else { Say "RESULT: incomplete - see above" }
