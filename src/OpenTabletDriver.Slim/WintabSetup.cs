using System;
using System.Diagnostics;
using System.IO;

namespace OpenTabletDriver.Slim
{
    public enum WintabState
    {
        Missing,      // 系统里没有 Wintab32.dll
        Ours,         // 已经是我们这版
        Outdated,     // 系统里的那份和随包的不一样（旧版或别的驱动）
        NoLocalFile   // 包里没带 Wintab32.dll，装不了
    }

    /// <summary>
    /// Wintab32.dll 的系统安装（PS / SAI 这类程序是去 System32 里找这个 DLL 的）。
    /// 只做检测 + 一键安装，安装靠 UAC 提权复制，不改注册表。
    /// </summary>
    public static class WintabSetup
    {
        // 调试用：设了 OTD_WINTAB_SYSTEM_DIR 就把"安装目标"指到别的目录，方便不弹 UAC 地走一遍安装流程
        private static string SystemDll
        {
            get
            {
                var fake = Environment.GetEnvironmentVariable("OTD_WINTAB_SYSTEM_DIR");
                return Path.Combine(string.IsNullOrWhiteSpace(fake)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.System)
                    : fake, "Wintab32.dll");
            }
        }

        public static string LocalDll =>
            Path.Combine(AppContext.BaseDirectory, "WintabNative", "Wintab32.dll");

        private static byte[]? Payload()
        {
            try
            {
                using var stream = typeof(WintabSetup).Assembly.GetManifestResourceStream("Wintab32.dll");
                if (stream == null)
                    return null;

                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>随包文件缺失时，从 exe 内嵌资源里释放一份出来（只拷了 exe 也能用）。</summary>
        public static bool EnsureLocal()
        {
            try
            {
                if (File.Exists(LocalDll))
                    return true;

                var payload = Payload();
                if (payload == null)
                    return false;

                Directory.CreateDirectory(Path.GetDirectoryName(LocalDll)!);
                File.WriteAllBytes(LocalDll, payload);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static WintabState Detect()
        {
            // 调试用：强制显示"未安装"，方便看提示文案
            if (Environment.GetEnvironmentVariable("OTD_FORCE_WINTAB_MISSING") == "1")
                return WintabState.Missing;

            var package = LocalDll;
            if (!File.Exists(package) && !EnsureLocal())
            {
                // 包里没这个文件，但系统里已经装着本程序那份，就当已安装（照样能用）
                try
                {
                    var payload = Payload();
                    if (payload != null && File.Exists(SystemDll) && Hash(SystemDll) == HashBytes(payload))
                        return WintabState.Ours;
                }
                catch
                {
                    // 判断不了就按缺文件处理
                }

                return WintabState.NoLocalFile;
            }

            var installed = SystemDll;
            if (!File.Exists(installed))
                return WintabState.Missing;

            try
            {
                // 光看大小不准（重新编译过大小可能撞上），直接比内容哈希
                return Hash(installed) == PackageHash.Value ? WintabState.Ours : WintabState.Outdated;
            }
            catch
            {
                return WintabState.Outdated;
            }
        }

        private static readonly Lazy<string> PackageHash = new(() =>
        {
            try
            {
                if (File.Exists(LocalDll))
                    return Hash(LocalDll);
            }
            catch
            {
                // 落到内嵌资源
            }

            var payload = Payload();
            return payload == null ? string.Empty : HashBytes(payload);
        }, true);

        private static string HashBytes(byte[] bytes)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        public static string Describe(WintabState state) => state switch
        {
            WintabState.Ours => "Wintab32.dll 已安装",
            WintabState.Outdated => "Wintab32.dll 版本不是本程序的 · 点击覆盖",
            WintabState.Missing => "Wintab32.dll 未安装 · 点击安装",
            _ => "随包缺少 Wintab32.dll"
        };

        /// <summary>
        /// 提权把随包的 Wintab32.dll 复制到 System32。成功返回 true。
        /// </summary>
        public static (bool Ok, string Message) Install()
        {
            switch (Detect())
            {
                case WintabState.NoLocalFile:
                    return (false, "随包里没有 WintabNative\\Wintab32.dll");
                case WintabState.Ours:
                    return (true, "已经是最新版本");
            }

            // 先直接拷一次：程序本身是管理员运行时，这一步就能过，不用弹 UAC
            try
            {
                File.Copy(LocalDll, SystemDll, true);
                if (Detect() == WintabState.Ours)
                    return (true, "已安装到 System32");
            }
            catch (UnauthorizedAccessException)
            {
                // 没权限，走提权
            }
            catch (IOException io)
            {
                // DLL 可能正被别的程序占用，提权也未必能过，但先如实报出来
                return (false, $"写入 System32 失败：{io.Message}");
            }

            return InstallElevated();
        }

        private static (bool Ok, string Message) InstallElevated()
        {
            var log = Path.Combine(Path.GetTempPath(), "otd-wintab-install.txt");
            try { File.Delete(log); } catch { }

            var source = LocalDll.Replace("'", "''");
            var target = SystemDll.Replace("'", "''");
            var logPath = log.Replace("'", "''");

            // 失败原因写进临时文件，提权进程关了也能读回来
            var script = $@"try {{
                Copy-Item -LiteralPath '{source}' -Destination '{target}' -Force -ErrorAction Stop
                'OK' | Out-File -LiteralPath '{logPath}' -Encoding utf8 -Force
            }} catch {{
                $_.Exception.Message | Out-File -LiteralPath '{logPath}' -Encoding utf8 -Force
                exit 1
            }}";

            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            try
            {
                using var process = Process.Start(info);
                if (process == null)
                    return (false, "没能启动安装进程");

                process.WaitForExit(20000);

                if (Detect() == WintabState.Ours)
                    return (true, "已安装到 System32");

                var detail = string.Empty;
                try
                {
                    if (File.Exists(log))
                        detail = File.ReadAllText(log).Trim();
                }
                catch { }

                if (detail.Length == 0)
                    return (false, "安装没有完成，可能没点允许，或文件被占用");

                return (false, detail == "OK" ? "复制完成但校验未通过" : $"安装失败：{detail}");
            }
            catch (Exception ex)
            {
                var cancelled = ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                                || ex.NativeErrorCode() == 1223;
                return (false, cancelled ? "已取消授权" : ex.Message);
            }
        }
    }
    internal static class ExceptionExt
    {
        public static int NativeErrorCode(this Exception ex) =>
            ex is System.ComponentModel.Win32Exception win32 ? win32.NativeErrorCode : 0;
    }
}
