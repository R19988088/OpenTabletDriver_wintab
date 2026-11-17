# Replaces the system-wide Wintab32.dll with the freshly built shim.
#
# Why a separate script: the DLL is mapped into every application that has
# already used Wintab, so Windows refuses to overwrite it while those apps (or
# OpenTabletDriver itself) are running. Killing them silently could throw away
# unsaved work, so this lists the holders first and only copies when nothing
# has it open.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File replace-wintab-system32.ps1
#
# Run it from an elevated shell (it will relaunch itself if it is not).

$root = $PSScriptRoot
# Prefer the fresh cargo output over the staged copy. If staging ever failed
# (for example the staged file was still mapped), this must not install a stale
# DLL again.
$built64 = Join-Path $root "wintab-shim\target\release\Wintab32.dll"
$built86 = Join-Path $root "wintab-shim\target\i686-pc-windows-msvc\release\Wintab32.dll"
$staged64 = Join-Path $root "portable-build\WintabNative\Wintab32.dll"
$staged86 = Join-Path $root "portable-build\WintabNative\Wintab32_x86.dll"
$src64 = if (Test-Path -LiteralPath $built64) { $built64 } else { $staged64 }
$src86 = if (Test-Path -LiteralPath $built86) { $built86 } else { $staged86 }
$dst64 = Join-Path $env:SystemRoot "System32\Wintab32.dll"
$dst86 = Join-Path $env:SystemRoot "SysWOW64\Wintab32.dll"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

$rmSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class FileLockQuery
{
    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public long ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    static extern int RmRegisterResources(uint dwSessionHandle,
        uint nFiles, string[] rgsFilenames,
        uint nApplications, IntPtr rgApplications,
        uint nServices, string[] rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded,
        ref uint pnProcInfo, IntPtr rgAffectedApps, out uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint dwSessionHandle);

    public static string[] WhoLocks(string path)
    {
        uint handle = 0;
        string key = Guid.NewGuid().ToString();
        if (RmStartSession(out handle, 0, key) != 0) return new string[0];
        try
        {
            string[] files = new string[] { path };
            if (RmRegisterResources(handle, (uint)files.Length, files, 0, IntPtr.Zero, 0, null) != 0)
                return new string[0];

            uint needed = 0;
            uint count = 0;
            uint reasons = 0;
            int rc = RmGetList(handle, out needed, ref count, IntPtr.Zero, out reasons);
            if (rc != 234 || needed == 0) return new string[0];

            int size = Marshal.SizeOf(typeof(RM_PROCESS_INFO));
            IntPtr buffer = Marshal.AllocHGlobal((int)(needed * size));
            try
            {
                count = needed;
                rc = RmGetList(handle, out needed, ref count, buffer, out reasons);
                if (rc != 0) return new string[0];

                var names = new List<string>();
                for (uint i = 0; i < count; i++)
                {
                    RM_PROCESS_INFO info = (RM_PROCESS_INFO)Marshal.PtrToStructure(
                        (IntPtr)((long)buffer + i * size), typeof(RM_PROCESS_INFO));
                    string label = info.strAppName;
                    if (info.strServiceShortName != null && info.strServiceShortName.Length > 0)
                        label = label + " [" + info.strServiceShortName + "]";
                    names.Add(label + " (pid " + info.Process.dwProcessId + ")");
                }
                return names.ToArray();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { RmEndSession(handle); }
    }
}
'@

if (-not $isAdmin) {
    Write-Host "Elevation required, relaunching..."
    $args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`""
    )
    Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $args
    exit 0
}

if (-not (Test-Path -LiteralPath $src64)) {
    Write-Host "ERROR: missing $src64 - run build-wintab.ps1 first."
    Read-Host "Press Enter to close"
    exit 1
}
if (-not (Test-Path -LiteralPath $src86)) {
    Write-Host "WARNING: no 32-bit build staged, SysWOW64 will be left alone."
}

function Get-Holders([string]$path) {
    # Restart Manager asks Windows who really holds the file. Enumerating
    # $proc.Modules misses services, other sessions, and any process whose
    # module list we are not allowed to read - and a 64-bit PowerShell cannot
    # walk a 32-bit process's modules at all.
    $rmNames = @()
    try {
        if (-not ([System.Management.Automation.PSTypeName]"FileLockQuery").Type) {
            Add-Type -TypeDefinition $rmSource
        }
        $rmNames = [FileLockQuery]::WhoLocks($path)
    }
    catch {
        $rmNames = @()
    }

    $holders = @()
    foreach ($name in $rmNames) {
        $holders += $name
    }
    if ($holders.Count -gt 0) {
        return $holders
    }

    # Fallback when Restart Manager is unavailable: module list of processes we
    # can read. Never authoritative, but better than saying nothing.
    foreach ($proc in Get-Process -ErrorAction SilentlyContinue) {
        try {
            foreach ($module in $proc.Modules) {
                if ($module.FileName -eq $path) {
                    $holders += ("{0} (pid {1})" -f $proc.ProcessName, $proc.Id)
                    break
                }
            }
        }
        catch {
            # Access denied on protected processes is expected.
        }
    }
    return $holders
}

function Install-Aside([string]$source, [string]$target) {
    # A mapped image cannot be overwritten, but it can sometimes be renamed
    # aside. Running apps keep their old mapping; every new launch picks up the
    # new file. Nothing is killed and no unsaved work is touched.
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $aside = "$target.old-$stamp"
    try {
        Rename-Item -LiteralPath $target -NewName (Split-Path -Leaf $aside) -ErrorAction Stop
    }
    catch {
        Write-Host ("  could not move the mapped file aside: {0}" -f $_.Exception.Message)
        Write-Host "  retrying after clearing the read-only bit and taking ownership"
        & attrib.exe -R $target | Out-Null
        & takeown.exe /F $target /A | Out-Null
        & icacls.exe $target /grant "*S-1-5-32-544:F" /Q | Out-Null
        try {
            Rename-Item -LiteralPath $target -NewName (Split-Path -Leaf $aside) -ErrorAction Stop
        }
        catch {
            Write-Host ("  still could not move it aside: {0}" -f $_.Exception.Message)
            Write-Host "  close every app that has used the tablet (Photoshop, SAI, OTD), then rerun."
            return $false
        }
    }
    try {
        Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop
        Write-Host ("  replaced OK - old build kept as {0}" -f (Split-Path -Leaf $aside))
        Write-Host "  apps already running still use the old build until they restart"
        return $true
    }
    catch {
        Write-Host ("  ERROR: copied nothing after rename: {0}" -f $_.Exception.Message)
        Write-Host ("  the old file is at {0} - rename it back if you want the previous state" -f $aside)
        return $false
    }
}

function Show-Diff([string]$source, [string]$target) {
    if (-not (Test-Path -LiteralPath $target)) {
        Write-Host "  not present"
        return
    }
    $a = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $b = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if ($a -eq $b) {
        Write-Host "  already up to date"
    }
    else {
        Write-Host "  OUT OF DATE (old build still installed)"
    }
}

Write-Host ""
Write-Host "== 64-bit: $dst64 =="
Show-Diff $src64 $dst64
$holders = Get-Holders $dst64
if ($holders.Count -gt 0) {
    Write-Host "  mapped by:"
    foreach ($p in $holders) {
        Write-Host ("    {0}" -f $p)
    }
    Write-Host ""
    if (-not (Install-Aside $src64 $dst64)) {
        Write-Host "Close these applications (save your work first) and OpenTabletDriver,"
        Write-Host "then run this script again."
        Read-Host "Press Enter to close"
        exit 2
    }
}
else {
    try {
        Copy-Item -LiteralPath $src64 -Destination $dst64 -Force
        Write-Host "  replaced OK"
    }
    catch {
        Write-Host "  ERROR: $($_.Exception.Message)"
        Write-Host "  retrying by moving the mapped file aside"
        if (-not (Install-Aside $src64 $dst64)) {
            Read-Host "Press Enter to close"
            exit 1
        }
    }
}

if (Test-Path -LiteralPath $src86) {
    Write-Host ""
    Write-Host "== 32-bit: $dst86 =="
    Show-Diff $src86 $dst86
    $holders32 = Get-Holders $dst86
    if ($holders32.Count -gt 0) {
        Write-Host "  mapped by:"
        foreach ($p in $holders32) {
            Write-Host ("    {0}" -f $p)
        }
        if (Install-Aside $src86 $dst86) {
            Write-Host "  replaced OK (old copy moved aside)"
        }
        else {
            Write-Host "  ERROR: could not replace the 32-bit copy"
        }
    }
    else {
        try {
            Copy-Item -LiteralPath $src86 -Destination $dst86 -Force
            Write-Host "  replaced OK"
        }
        catch {
            Write-Host "  ERROR: $($_.Exception.Message)"
            if (Install-Aside $src86 $dst86) {
                Write-Host "  replaced OK (old copy moved aside)"
            }
        }
    }
}

Write-Host ""
Write-Host "Done. Start OpenTabletDriver, then open the app and check"
Write-Host "$env:LOCALAPPDATA\OTDWintab\ for wintab-<app>-<pid>.log."
Read-Host "Press Enter to close"
