# Compares the freshly built DLLs with the copies Windows is actually loading.
# Run from any shell; no administrator rights needed to read/hash the files.

$root = $PSScriptRoot
$built64 = Join-Path $root "wintab-shim\target\release\Wintab32.dll"
$built86 = Join-Path $root "wintab-shim\target\i686-pc-windows-msvc\release\Wintab32.dll"
$staged64 = Join-Path $root "portable-build\WintabNative\Wintab32.dll"
$staged86 = Join-Path $root "portable-build\WintabNative\Wintab32_x86.dll"
$pairs = @(
    @{ Name = "x64"; Source = $(if (Test-Path -LiteralPath $built64) { $built64 } else { $staged64 }); Installed = Join-Path $env:SystemRoot "System32\Wintab32.dll" },
    @{ Name = "x86"; Source = $(if (Test-Path -LiteralPath $built86) { $built86 } else { $staged86 }); Installed = Join-Path $env:SystemRoot "SysWOW64\Wintab32.dll" }
)

foreach ($pair in $pairs) {
    Write-Host ""
    Write-Host "== $($pair.Name) =="
    if (-not (Test-Path -LiteralPath $pair.Source)) {
        Write-Host "  build output missing: $($pair.Source)"
        continue
    }
    if (-not (Test-Path -LiteralPath $pair.Installed)) {
        Write-Host "  not installed: $($pair.Installed)"
        continue
    }
    $src = Get-Item -LiteralPath $pair.Source
    $dst = Get-Item -LiteralPath $pair.Installed
    $srcHash = (Get-FileHash -LiteralPath $pair.Source -Algorithm SHA256).Hash
    $dstHash = (Get-FileHash -LiteralPath $pair.Installed -Algorithm SHA256).Hash
    Write-Host ("  build:     {0}  {1} bytes  {2}" -f $src.LastWriteTime, $src.Length, $srcHash)
    Write-Host ("  installed: {0}  {1} bytes  {2}" -f $dst.LastWriteTime, $dst.Length, $dstHash)
    if ($srcHash -eq $dstHash) {
        Write-Host "  MATCH: Windows is loading the build in this folder."
    }
    else {
        Write-Host "  MISMATCH: the installed DLL is a different build."
    }
}

Write-Host ""
Write-Host "After starting SAI, the new log should contain: WTOpen ... width=36"
Write-Host "After starting Photoshop with the 0x1FFC mask, it should contain width=52 pressure_at=Some(36)."
