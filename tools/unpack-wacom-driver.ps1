# Unpacks Wacom's official tablet driver installer and collects Wintab32.dll.
#
# Why: the shim has been guessing at Wintab's declaration surface (DVC_*, CSR_*,
# default context, axis ranges). The official driver contains the exact answers,
# and dumping them from its own DLL is faster and safer than one more round of
# trial and error.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File unpack-wacom-driver.ps1
#
# Needs 7-Zip (7z.exe). Wacom's installer is a nested archive, so extraction
# runs repeatedly until no new archive turns up.

$out = Join-Path $PSScriptRoot "wacom-official"
$seven = "C:\Program Files\7-Zip\7z.exe"
$bin = Join-Path $out "bin"

function Fail([string]$message) {
    Write-Host "ERROR: $message"
    Read-Host "Press Enter to close"
    exit 1
}

if (-not (Test-Path -LiteralPath $seven)) { Fail "7-Zip not found at $seven" }

# The installer lives under a folder whose name is not ASCII, and Windows
# PowerShell reads BOM-less scripts as ANSI, so a literal path here arrives
# mojibake. Discover it instead; WACOM_INSTALLER overrides when set.
$installer = $env:WACOM_INSTALLER
if (-not $installer) {
    $roots = @("E:\", (Join-Path $env:USERPROFILE "Downloads"))
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $hit = Get-ChildItem -LiteralPath $root -Filter "WacomTablet_*.exe" -File `
            -Recurse -Depth 2 -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { $installer = $hit.FullName; break }
    }
}
if (-not $installer -or -not (Test-Path -LiteralPath $installer)) {
    Fail "no WacomTablet_*.exe found; set WACOM_INSTALLER to its full path"
}
Write-Host "installer: $installer"

Write-Host ""
Write-Host "== 7-Zip archive listing (first 60 entries) =="
& $seven l $installer
if ($LASTEXITCODE -ne 0) {
    Write-Host "7-Zip cannot read this file as an archive (exit $LASTEXITCODE)."
    Write-Host "Tell me the format it prints above and I will switch the extraction step."
}

if (Test-Path -LiteralPath $out) {
    Write-Host "clearing previous extraction: $out"
    Remove-Item -LiteralPath $out -Recurse -Force
}
New-Item -ItemType Directory -Path $out -Force | Out-Null
New-Item -ItemType Directory -Path $bin -Force | Out-Null

$archiveExtensions = @(".exe", ".msi", ".cab", ".7z", ".zip", ".rar")
$seen = @{}

for ($pass = 1; $pass -le 4; $pass++) {
    $archives = Get-ChildItem -LiteralPath $out -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $archiveExtensions -contains $_.Extension.ToLower() -and -not $seen.ContainsKey($_.FullName) }

    if ($archives.Count -eq 0) { break }

    Write-Host ""
    Write-Host "== extraction pass ${pass}: $($archives.Count) archive(s) =="
    foreach ($archive in $archives) {
        $seen[$archive.FullName] = $true
        $target = Join-Path $archive.DirectoryName ("x-" + $archive.BaseName)
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Write-Host ("  {0}" -f $archive.FullName)
        & $seven x $archive.FullName "-o$target" -y | Select-Object -Last 6
        Write-Host ("  exit code: {0}" -f $LASTEXITCODE)
    }
}

Write-Host ""
Write-Host "== Wintab32.dll candidates =="
$found = Get-ChildItem -LiteralPath $out -Recurse -File -Filter "Wintab32.dll" -ErrorAction SilentlyContinue
if ($found.Count -eq 0) {
    Write-Host "  none found. Contents of the extraction root:"
    Get-ChildItem -LiteralPath $out -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object Length -Descending |
        Select-Object -First 40 |
        ForEach-Object { Write-Host ("  {0,10:N0}  {1}" -f $_.Length, $_.FullName) }
    Write-Host ""
    Write-Host "If the installer is a Wacom 'setup.exe' wrapper, the payload may be an"
    Write-Host "MSI or a CAB that 7-Zip lists but cannot expand. Send me the listing above."
    Read-Host "Press Enter to close"
    exit 2
}

$copied = 0
foreach ($dll in $found) {
    $info = (Get-Item -LiteralPath $dll.FullName).VersionInfo
    Write-Host ("  {0}" -f $dll.FullName)
    Write-Host ("      size={0:N0} bytes  file={1}  product={2}" -f
        $dll.Length, $info.FileVersion, $info.ProductVersion)

    # Keep the first of each bitness: later copies are usually the same payload
    # repeated per language folder.
    $name = if ($dll.FullName -match "SysWOW64|32-bit|x86") { "Wintab32_x86.dll" } else { "Wintab32_x64.dll" }
    $destination = Join-Path $bin $name
    if (-not (Test-Path -LiteralPath $destination)) {
        Copy-Item -LiteralPath $dll.FullName -Destination $destination
        $copied++
    }
}

Write-Host ""
Write-Host "collected $copied file(s) into $bin"
Get-ChildItem -LiteralPath $bin -File | ForEach-Object {
    Write-Host ("  {0,10:N0}  {1}" -f $_.Length, $_.Name)
}
Write-Host ""
Write-Host "Next: the declaration dump will LoadLibrary these and read WTInfo for"
Write-Host "every standard category, so the shim can copy the real answers."
Read-Host "Press Enter to close"
