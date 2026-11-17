using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// "最高权限开机启动"——用任务计划程序建一个 ONLOGON + HIGHEST 的任务。
    /// 这样开机就是管理员权限、也不再弹 UAC（UAC 只在你**创建/删除**这个任务时弹一次）。
    /// 只给自己用的机器做这个就行：任务在"任务计划程序"里是可见的，安全软件也可能报。
    /// </summary>
    public static class StartupHigh
    {
        private const string TaskName = "OTD Slim";

        public static bool Exists()
        {
            try
            {
                var result = Run("schtasks.exe", new[] { "/Query", "/TN", TaskName }, false, out _);
                return result;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>建任务。需要一次提权，之后开机静默以管理员身份启动。</summary>
        public static bool Install(string exePath)
        {
            var command = $"\"{exePath}\" --page=附加";
            var arguments = new[] { "/Create", "/TN", TaskName, "/TR", command, "/SC", "ONLOGON", "/RL", "HIGHEST", "/F" };
            return RunElevated("schtasks.exe", arguments);
        }

        public static bool Remove() =>
            RunElevated("schtasks.exe", new[] { "/Delete", "/TN", TaskName, "/F" });

        private static bool Run(string file, string[] arguments, bool wait, out string output)
        {
            output = string.Empty;
            try
            {
                var info = new ProcessStartInfo(file)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in arguments)
                    info.ArgumentList.Add(argument);

                using var process = Process.Start(info);
                if (process == null)
                    return false;

                if (wait)
                    process.WaitForExit(10000);

                output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                return process.HasExited && process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool RunElevated(string file, string[] arguments)
        {
            // 用提权 powershell 调一次，结果写临时文件读回来（和装 Wintab32.dll 一样的做法）
            var log = Path.Combine(Path.GetTempPath(), "otd-task.txt");
            try { File.Delete(log); } catch { }

            var list = string.Join(",", arguments.Select(a => "'" + a.Replace("'", "''") + "'"));
            var script =
                $"$out = & '{file}' {list} 2>&1 | Out-String; " +
                $"$out + $(if ($LASTEXITCODE -eq 0) {{ 'OK' }} else {{ 'FAIL' }}) | Out-File -LiteralPath '{log}' -Encoding utf8 -Force";

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
                    return false;

                process.WaitForExit(20000);
                return File.Exists(log) && File.ReadAllText(log).Contains("OK");
            }
            catch
            {
                return false;
            }
        }
    }
}
