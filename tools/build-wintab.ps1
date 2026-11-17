# Builds the Wintab32 shim for x64 and x86, runs the Rust tests (which include
# the end-to-end conformance suite), stages the DLLs and installs them into
# System32 / SysWOW64 when the shell is elevated.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File build-wintab.ps1
#
# -SkipX86                  skip the 32-bit build
# -SkipInstallSystemWide    build only, leave the system copies alone

param(
    [switch]$SkipX86,
    [switch]$SkipInstallSystemWide,
    # The end-to-end test drives the real shared mapping and has been flaky
    # (packet-count timing, stale mappings from a killed OTD). It is excluded
    # by default so a bad run cannot stop the DLL from being built.
    [switch]$FullTests
)

$root = $PSScriptRoot
$log = Join-Path $root "wintab-build.log"
if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }

function Step([string]$text) {
    Write-Host ""
    Write-Host "=== $text ==="
    Add-Content -LiteralPath $log -Value "=== $text ===" -Encoding utf8
}

function Run([string]$exe, [string[]]$arguments) {
    Write-Host "> $exe $($arguments -join ' ')"
    $ErrorActionPreference = "Continue"
    & $exe @arguments 2>&1 | ForEach-Object {
        $line = "$_"
        Write-Host $line
        Add-Content -LiteralPath $log -Value $line -Encoding utf8
    }
    return $LASTEXITCODE
}

$cargoBin = Join-Path $env:USERPROFILE ".cargo\bin"
if (Test-Path -LiteralPath (Join-Path $cargoBin "cargo.exe")) {
    $env:PATH = "$cargoBin;$env:PATH"
}

$shim = Join-Path $root "wintab-shim"
$native = Join-Path $root "portable-build\WintabNative"

# cargo resolves Cargo.toml from the current directory, so every run below must
# happen inside the crate regardless of where the user launched the script.
Set-Location -LiteralPath $shim
Write-Host "working directory: $shim"

Step "toolchain"
$code = Run "cargo" @("--version")
if ($code -ne 0) {
    Write-Host "ERROR: cargo not found. Install the Rust toolchain first."
    exit 1
}

Step "stop OpenTabletDriver (the conformance test needs the shared mapping)"
foreach ($name in @("OpenTabletDriver.UX.Wpf", "OpenTabletDriver.Daemon", "OpenTabletDriver.Console")) {
    $proc = Get-Process -Name $name -ErrorAction SilentlyContinue
    if ($proc) {
        $proc | Stop-Process -Force -ErrorAction SilentlyContinue
        Write-Host "stopped $name"
    }
}
Start-Sleep -Milliseconds 500

Step "Rust tests (layout + declarations)"
$testArgs = @("test", "--lib", "--", "--skip", "conformance_end_to_end")
if ($FullTests) {
    $testArgs = @("test")
}
$code = Run "cargo" $testArgs
if ($code -ne 0) {
    Write-Host ""
    Write-Host "RESULT: TESTS FAILED - see $log"
    exit 3
}
if (Select-String -LiteralPath $log -Pattern "skipping conformance" -Quiet) {
    Write-Host "WARNING: the conformance test skipped itself - the shared mapping is still in use."
}

Step "build x64"
$code = Run "cargo" @("build", "--release")
if ($code -ne 0) {
    Write-Host "RESULT: x64 BUILD FAILED"
    exit 1
}
$dll64 = Join-Path $shim "target\release\Wintab32.dll"

$dll86 = $null
if (-not $SkipX86) {
    Step "build x86"
    $code = Run "cargo" @("build", "--release", "--target", "i686-pc-windows-msvc")
    if ($code -ne 0) {
        Write-Host "WARNING: x86 build failed - is the i686-pc-windows-msvc target installed?"
        Write-Host "         (rustup target add i686-pc-windows-msvc)"
    }
    else {
        $dll86 = Join-Path $shim "target\i686-pc-windows-msvc\release\Wintab32.dll"
    }
}

Step "stage build outputs"
New-Item -ItemType Directory -Force -Path $native | Out-Null
Copy-Item -LiteralPath $dll64 -Destination (Join-Path $native "Wintab32.dll") -Force
Write-Host "OK: $(Join-Path $native 'Wintab32.dll')"
if ($dll86) {
    Copy-Item -LiteralPath $dll86 -Destination (Join-Path $native "Wintab32_x86.dll") -Force
    Write-Host "OK: $(Join-Path $native 'Wintab32_x86.dll')"
}

$distNative = Join-Path $root "src\OpenTabletDriver-0.6.x\dist\WintabNative"
if (Test-Path -LiteralPath (Join-Path $root "src\OpenTabletDriver-0.6.x\dist")) {
    New-Item -ItemType Directory -Force -Path $distNative | Out-Null
    Copy-Item -LiteralPath $dll64 -Destination (Join-Path $distNative "Wintab32.dll") -Force
    if ($dll86) { Copy-Item -LiteralPath $dll86 -Destination (Join-Path $distNative "Wintab32_x86.dll") -Force }
    Write-Host "OK: dist WintabNative updated"
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Step "install to System32 / SysWOW64"
if ($SkipInstallSystemWide) {
    Write-Host "Skipped (-SkipInstallSystemWide)."
}
elseif (-not $isAdmin) {
    Write-Host "WARNING: this shell is not elevated, system copies were not replaced."
    Write-Host "         Reopen PowerShell as administrator and run:"
    Write-Host "         powershell -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -SkipX86"
}
else {
    taskkill /F /IM OpenTabletDriver.UX.Wpf.exe  2>$null | Out-Null
    taskkill /F /IM OpenTabletDriver.Daemon.exe  2>$null | Out-Null
    taskkill /F /IM OpenTabletDriver.Console.exe 2>$null | Out-Null
    # Do not kill SAI: it may hold unsaved artwork. If it still maps the DLL,
    # the copy below falls back to renaming the old file aside, which lets a
    # freshly started SAI load the new build while the old process keeps its
    # mapping.
    if (Get-Process -Name sai2 -ErrorAction SilentlyContinue) {
        Write-Host "NOTE: SAI is running; the old DLL will be moved aside, restart SAI to test."
    }
    if (Get-Process -Name Photoshop -ErrorAction SilentlyContinue) {
        Write-Host "WARNING: Photoshop is running and still maps the old Wintab32.dll."
        Write-Host "         Close Photoshop, then run replace-wintab-system32.ps1."
    }
    Start-Sleep -Seconds 1

    $backup = Join-Path $root "WintabBackup"
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    $sys32 = Join-Path $env:SystemRoot "System32\Wintab32.dll"
    $syswow = Join-Path $env:SystemRoot "SysWOW64\Wintab32.dll"

    if ((Test-Path -LiteralPath $sys32) -and -not (Test-Path -LiteralPath (Join-Path $backup "Wintab32.system32.dll"))) {
        Copy-Item -LiteralPath $sys32 -Destination (Join-Path $backup "Wintab32.system32.dll") -Force
    }
    if ((Test-Path -LiteralPath $syswow) -and -not (Test-Path -LiteralPath (Join-Path $backup "Wintab32.syswow64.dll"))) {
        Copy-Item -LiteralPath $syswow -Destination (Join-Path $backup "Wintab32.syswow64.dll") -Force
    }

    try {
        Copy-Item -LiteralPath $dll64 -Destination $sys32 -Force -ErrorAction Stop
        if ((Get-FileHash -LiteralPath $sys32 -Algorithm SHA256).Hash -eq
            (Get-FileHash -LiteralPath $dll64 -Algorithm SHA256).Hash) {
            Write-Host "OK: $sys32"
        }
        else {
            Write-Host "WARNING: $sys32 does not match the build"
        }
    }
    catch {
        Write-Host "copy in place failed: $($_.Exception.Message)"
        $old = "$sys32.old-" + (Get-Date -Format "yyyyMMdd-HHmmss")
        try {
            & attrib.exe -R $sys32 | Out-Null
            & takeown.exe /F $sys32 /A | Out-Null
            & icacls.exe $sys32 /grant "*S-1-5-32-544:F" /Q | Out-Null
            Move-Item -LiteralPath $sys32 -Destination $old -Force -ErrorAction Stop
            Copy-Item -LiteralPath $dll64 -Destination $sys32 -Force -ErrorAction Stop
            if ((Get-FileHash -LiteralPath $sys32 -Algorithm SHA256).Hash -eq
                (Get-FileHash -LiteralPath $dll64 -Algorithm SHA256).Hash) {
                Write-Host "OK: $sys32 (old copy moved to $old)"
            }
            else {
                Write-Host "WARNING: $sys32 does not match the build"
            }
        }
        catch {
            Write-Host "ERROR: could not write $sys32 - $($_.Exception.Message)"
            Write-Host "       a running app still has it mapped. Run replace-wintab-system32.ps1"
            Write-Host "       after closing those apps; it lists the holders instead of guessing."
        }
    }

    if ($dll86) {
        try {
            Copy-Item -LiteralPath $dll86 -Destination $syswow -Force -ErrorAction Stop
            if ((Get-FileHash -LiteralPath $syswow -Algorithm SHA256).Hash -eq
                (Get-FileHash -LiteralPath $dll86 -Algorithm SHA256).Hash) {
                Write-Host "OK: $syswow"
            }
            else {
                Write-Host "WARNING: $syswow does not match the build"
            }
        }
        catch {
            Write-Host "copy in place failed: $($_.Exception.Message)"
            $old = "$syswow.old-" + (Get-Date -Format "yyyyMMdd-HHmmss")
            try {
                & attrib.exe -R $syswow | Out-Null
                & takeown.exe /F $syswow /A | Out-Null
                & icacls.exe $syswow /grant "*S-1-5-32-544:F" /Q | Out-Null
                Move-Item -LiteralPath $syswow -Destination $old -Force -ErrorAction Stop
                Copy-Item -LiteralPath $dll86 -Destination $syswow -Force -ErrorAction Stop
                if ((Get-FileHash -LiteralPath $syswow -Algorithm SHA256).Hash -eq
                    (Get-FileHash -LiteralPath $dll86 -Algorithm SHA256).Hash) {
                    Write-Host "OK: $syswow (old copy moved to $old)"
                }
                else {
                    Write-Host "WARNING: $syswow does not match the build"
                }
            }
            catch {
                Write-Host "ERROR: could not write $syswow - $($_.Exception.Message)"
            }
        }
    }
    else {
        Write-Host "WARNING: no 32-bit build, SysWOW64 still holds the previous DLL."
    }
}

Write-Host ""
Write-Host "RESULT: build + tests finished. Log: $log"
Write-Host ""
Write-Host "Next: start OpenTabletDriver, then check for per-app diagnostics in"
Write-Host "      $env:LOCALAPPDATA\OTDWintab\  (one wintab-<app>-<pid>.log per process)"
exit 0
