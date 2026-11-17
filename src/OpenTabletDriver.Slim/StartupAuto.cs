using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 开机自启：写在 HKCU 的 Run 键下。
    /// 顺手清掉旧版 OTD 留下的自启项（OpenTabletDriver.Daemon / OpenTabletDriver.UX 之类），
    /// 免得开机同时拉起两套驱动。
    /// </summary>
    public static class StartupAuto
    {
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "OTD Slim";

        public static string ExePath =>
            Environment.ProcessPath ?? AppContext.BaseDirectory + "OpenTabletDriver.Slim.exe";

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath, false);
            var value = key?.GetValue(ValueName) as string;
            return value != null && value.Contains("OpenTabletDriver.Slim", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>如果已经开了自启，把注册表里的路径改成当前这个 exe（换版本后自启还指向旧目录是致命问题）。</summary>
        public static void EnsurePath()
        {
            try
            {
                if (!IsEnabled())
                    return;

                using var key = Registry.CurrentUser.OpenSubKey(RunPath, true);
                key?.SetValue(ValueName, $"\"{ExePath}\"", RegistryValueKind.String);
            }
            catch
            {
                // 改不了就算了
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunPath, true);
            if (key == null)
                return;

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{ExePath}\"", RegistryValueKind.String);
                CleanLegacy();
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, false);
            }
        }

        /// <summary>
        /// 清掉旧版 OTD / 旧版自建的 OTD 自启项，返回清掉的条数。
        /// 只动 Run 键下明显指向 OTD 的项，别的不碰。
        /// </summary>
        public static int CleanLegacy()
        {
            var removed = 0;

            using var key = Registry.CurrentUser.OpenSubKey(RunPath, true);
            if (key == null)
                return 0;

            foreach (var name in new List<string>(GetNames(key)))
            {
                if (name == ValueName)
                    continue;

                var value = key.GetValue(name) as string ?? string.Empty;
                var looksLikeOtd = value.Contains("OpenTabletDriver", StringComparison.OrdinalIgnoreCase)
                                   || name.Contains("OpenTabletDriver", StringComparison.OrdinalIgnoreCase)
                                   || name.Equals("OTD", StringComparison.OrdinalIgnoreCase)
                                   || name.Equals("OTD Slim", StringComparison.OrdinalIgnoreCase);

                if (!looksLikeOtd)
                    continue;

                try
                {
                    key.DeleteValue(name, false);
                    removed++;
                }
                catch
                {
                    // 删不掉就算了，不影响自启本身
                }
            }

            return removed;
        }

        public static bool HasLegacy()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath, false);
            if (key == null)
                return false;

            foreach (var name in GetNames(key))
            {
                if (name == ValueName)
                    continue;

                var value = key.GetValue(name) as string ?? string.Empty;
                if (value.Contains("OpenTabletDriver", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("OpenTabletDriver", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string[] GetNames(RegistryKey key)
        {
            try
            {
                return key.GetValueNames();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
