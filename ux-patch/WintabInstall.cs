using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace OpenTabletDriver.UX.Wpf
{
    /// <summary>
    /// Puts the bundled Wintab32.dll into System32 and SysWOW64 when it is missing or stale.
    /// A shim that is not installed is invisible: every Wintab application simply reports no
    /// pressure, and telling the user to go find install-wintab.bat was the weak point of this
    /// package. The check is two SHA-256 comparisons, so it is cheap enough to run every start.
    /// Writing to those directories needs administrator rights, so the copy runs from a
    /// generated PowerShell script that is started with the UAC prompt.
    /// </summary>
    internal static class WintabInstall
    {
        private const uint MB_ICONWARNING = 0x00000030;
        private const uint MB_ICONINFORMATION = 0x00000040;
        private const uint MB_ICONQUESTION = 0x00000020;
        private const uint MB_YESNO = 0x00000004;
        private const int IDYES = 6;

        private const string PreferenceKey = @"Software\OpenTabletDriver\Wintab";
        private const string DeclinedValue = "WintabInstallDeclined";

        public static void EnsureInstalled()
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                Note($"wintab check failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Run()
        {
            var baseDirectory = AppContext.BaseDirectory;
            var source64 = Path.Join(baseDirectory, "WintabNative", "Wintab32.dll");
            var source32 = Path.Join(baseDirectory, "WintabNative", "Wintab32_x86.dll");

            // Not a Wintab package: nothing to install, and no reason to nag.
            if (!File.Exists(source64) || !File.Exists(source32))
                return;

            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var target64 = Path.Join(windowsDirectory, "System32", "Wintab32.dll");
            var target32 = Path.Join(windowsDirectory, "SysWOW64", "Wintab32.dll");

            // OTD_INSTALL_DRYRUN points the same install path at a sandbox and reports through the
            // notes file instead of a message box, so the whole thing can be exercised on a machine
            // whose system copies are already correct without overwriting them.
            var dryRun = Environment.GetEnvironmentVariable("OTD_INSTALL_DRYRUN") is { Length: > 0 };
            if (dryRun)
            {
                var sandbox = Path.Join(Path.GetTempPath(), "otd-install-dryrun");
                Directory.CreateDirectory(sandbox);
                target64 = Path.Join(sandbox, "system32-Wintab32.dll");
                target32 = Path.Join(sandbox, "syswow64-Wintab32.dll");
            }

            var pending = new StringBuilder();
            if (!SameContent(source64, target64))
                pending.AppendLine("  " + target64 + (File.Exists(target64) ? "  (版本不一致)" : "  (未安装)"));
            if (!SameContent(source32, target32))
                pending.AppendLine("  " + target32 + (File.Exists(target32) ? "  (版本不一致)" : "  (未安装)"));

            if (pending.Length == 0)
                return;

            if (!dryRun && Declined())
            {
                Note("wintab install skipped: declined earlier");
                return;
            }

            if (!dryRun && !IsElevated() && MessageBoxW(IntPtr.Zero,
                    "OpenTabletDriver 需要把 Wintab32.dll 放进系统目录，否则绘画软件拿不到压感。\r\n\r\n" +
                    "待写入：\r\n" + pending + "\r\n" +
                    "现在安装吗？会弹出一次管理员确认，旧文件备份在本目录 WintabBackup。\r\n" +
                    "选「否」就不再询问（以后可右键 install-wintab.bat 手动装）。",
                    "安装 Wintab32.dll", MB_YESNO | MB_ICONQUESTION) != IDYES)
            {
                SetDeclined(true);
                Note("wintab install declined by user");
                return;
            }

            var log = RunInstaller(baseDirectory, source64, source32, target64, target32, dryRun);
            if (log == null)
            {
                Note("wintab install: elevated installer did not run (UAC cancelled?)");
                return;
            }

            Note("wintab install: " + log.Replace("\r\n", " | ").Replace("\n", " | "));

            // The script only reports what it did; whether the system copies really are the bundled
            // ones is answered here, by hashing them again.
            var installed = SameContent(source64, target64) && SameContent(source32, target32);
            Note("wintab install result: " + (installed ? "both copies match" : "still not matching"));

            if (dryRun)
                return;

            if (!installed)
            {
                MessageBoxW(IntPtr.Zero,
                    "安装 Wintab32.dll 失败：\r\n\r\n" + log + "\r\n\r\n" +
                    "可以关闭本提示，然后右键 install-wintab.bat 手动安装。",
                    "安装失败", MB_ICONWARNING);
            }
            else
            {
                SetDeclined(false);
                MessageBoxW(IntPtr.Zero,
                    "Wintab32.dll 已安装到系统目录。\r\n\r\n" +
                    "已经开着的绘画软件需要完全关闭再打开，才会加载新的 DLL。",
                    "安装完成", MB_ICONINFORMATION);
            }
        }

        /// <summary>
        /// Writes the copy script into the temp folder and runs it elevated, returning its log.
        /// Passing PowerShell a script file avoids quoting two Windows paths into a command line.
        /// </summary>
        private static string? RunInstaller(string baseDirectory, string source64, string source32, string target64, string target32, bool dryRun)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var scriptPath = Path.Join(Path.GetTempPath(), $"otd-wintab-install-{stamp}.ps1");
            var logPath = Path.Join(Path.GetTempPath(), $"otd-wintab-install-{stamp}.txt");

            var script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            script.AppendLine("$out = @()");
            script.AppendLine("function Step($t) { $script:out += $t }");
            script.AppendLine("try {");
            script.AppendLine("  $bak = Join-Path " + Quote(baseDirectory) + " 'WintabBackup'");
            script.AppendLine("  New-Item -ItemType Directory -Force -Path $bak | Out-Null");
            script.AppendLine("  $pairs = @(" +
                "@(" + Quote(source64) + ", " + Quote(target64) + ", 'system32'), " +
                "@(" + Quote(source32) + ", " + Quote(target32) + ", 'syswow64'))");
            script.AppendLine("  foreach ($p in $pairs) {");
            script.AppendLine("    $src = $p[0]; $dst = $p[1]; $tag = $p[2]");
            script.AppendLine("    if (Test-Path -LiteralPath $dst) {");
            script.AppendLine("      Copy-Item -LiteralPath $dst -Destination (Join-Path $bak ('Wintab32.' + $tag + '-' + " + Quote(stamp) + " + '.dll')) -Force");
            script.AppendLine("    }");
            script.AppendLine("    try {");
            script.AppendLine("      Copy-Item -LiteralPath $src -Destination $dst -Force");
            script.AppendLine("      Step ('OK   ' + $dst)");
            script.AppendLine("    } catch {");
            script.AppendLine("      try {");
            script.AppendLine("        $aside = Join-Path (Split-Path -Parent $dst) ('Wintab32.old-' + [int](Get-Date -UFormat '%s') + '.dll')");
            script.AppendLine("        Move-Item -LiteralPath $dst -Destination $aside -Force");
            script.AppendLine("        Copy-Item -LiteralPath $src -Destination $dst -Force");
            script.AppendLine("        Step ('OK   ' + $dst + '  (old file moved aside)')");
            script.AppendLine("      } catch {");
            script.AppendLine("        Step ('FAIL ' + $dst + '  ' + $_.Exception.Message)");
            script.AppendLine("      }");
            script.AppendLine("    }");
            script.AppendLine("  }");
            script.AppendLine("  Step 'DONE'");
            script.AppendLine("} catch {");
            script.AppendLine("  Step ('ERROR ' + $_.Exception.Message)");
            script.AppendLine("}");
            script.AppendLine("[IO.File]::WriteAllText(" + Quote(logPath) + ", ($out -join \"`r`n\"), [Text.UTF8Encoding]::new($false))");

            // Windows PowerShell 5.1 decodes a .ps1 without a BOM as ANSI, which turns a user
            // profile path containing non-ASCII characters into mojibake and fails the whole
            // install silently. The BOM is what makes the generated script readable there.
            File.WriteAllText(scriptPath, script.ToString(), new UTF8Encoding(true));

            var arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";

            // A dry run writes only to a sandbox this process can already write, so it must not
            // raise a UAC prompt just to prove the script works.
            var waited = dryRun || IsElevated()
                ? RunDirect(arguments)
                : RunElevated(arguments);

            if (!waited)
                return null;

            try
            {
                return File.Exists(logPath) ? File.ReadAllText(logPath) : "(installer produced no log)";
            }
            finally
            {
                // A dry run leaves both files behind: they are the only way to see what the
                // generated script actually contains when it fails.
                if (!dryRun)
                {
                    try { File.Delete(scriptPath); } catch { }
                    try { File.Delete(logPath); } catch { }
                }
            }
        }

        private static bool RunDirect(string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            return process.WaitForExit(120_000);
        }

        private static bool RunElevated(string arguments)
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC,
                lpVerb = "runas",
                lpFile = "powershell.exe",
                lpParameters = arguments,
                nShow = SW_HIDE
            };

            if (!ShellExecuteExW(ref info))
                return false;

            if (info.hProcess == IntPtr.Zero)
                return true;

            try
            {
                _ = WaitForSingleObject(info.hProcess, 120_000);
                return true;
            }
            finally
            {
                CloseHandle(info.hProcess);
            }
        }

        private static bool SameContent(string source, string target)
        {
            if (!File.Exists(source) || !File.Exists(target))
                return false;

            try
            {
                return string.Equals(Hash(source), Hash(target), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        private static bool IsElevated()
        {
            try
            {
                return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static bool Declined()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PreferenceKey);
                return key?.GetValue(DeclinedValue) is int value && value != 0;
            }
            catch
            {
                return false;
            }
        }

        private static void SetDeclined(bool declined)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(PreferenceKey);
                key?.SetValue(DeclinedValue, declined ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch
            {
                // Registry state is a convenience; the file check is the source of truth.
            }
        }

        private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

        private static void Note(string line)
        {
            try
            {
                var directory = Path.Join(AppContext.BaseDirectory, "userdata");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Join(directory, "startup-notes.log"),
                    $"# {DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch
            {
                // Notes are diagnostics only.
            }
        }

        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO info);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
        private const uint SEE_MASK_NOASYNC = 0x00000100;
        private const int SW_HIDE = 0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }
    }
}
