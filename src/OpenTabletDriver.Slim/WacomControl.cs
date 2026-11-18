using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// Wacom 用户态接管 / 恢复。
    ///
    /// 为什么需要它：Wacom 自己的服务（PTH-660 上是 WTabletServicePro）和触摸进程
    /// （Wacom_TouchUser 等）会同时打开同一个 HID 接口，OTD 抢到笔、抢不到触摸，
    /// 才会出现"0x11 环能收到、0x21/0xD2 一个都没有"。只把服务设成 Disabled 还不够：
    /// 服务停了之后它注册的进程会立刻把它拉起来，所以必须先杀进程、再禁服务、再补杀。
    ///
    /// 顺序（v34 修正后的四步，见 docs/otd-slim.md 896-917 行）：
    ///   1. 先杀全部 Wacom 用户态进程
    ///   2. sc config &lt;服务&gt; start= disabled，再 sc stop
    ///   3. 再补杀三轮，每轮间隔 300ms
    ///   4. 回读 HKLM\...\Services\&lt;服务&gt;\Start，确认 = 4（Disabled）
    ///
    /// 只动用户态服务（Type &amp; 0x30 为 0x10/0x20 的那些），不碰内核驱动
    /// （WacomPen / WacHidRouterPro / wacomrouterfilter），否则笔本身会一起掉。
    ///
    /// 原始 Start 值写在 data\wacom-state.ini（"服务名=Start数值"），恢复时按它写回。
    /// 所有需要管理员权限的步骤都塞进一个提权 PowerShell 里一次跑完，只弹一次 UAC。
    /// </summary>
    public static class WacomControl
    {
        public const string ServiceName = "WTabletServicePro";

        private static readonly string ServiceKey =
            @"SYSTEM\CurrentControlSet\Services\" + ServiceName;

        /// <summary>接管前的原始 Start 值，恢复时照它写回（格式：WTabletServicePro=2）。</summary>
        public static string StateFile =>
            Path.Combine(AppContext.BaseDirectory, "data", "wacom-state.ini");

        /// <summary>一行状态，界面每两秒刷一次。</summary>
        public static string Status()
        {
            var parts = new List<string>();

            var start = QueryStart();
            parts.Add(start == null
                ? $"服务 {ServiceName}：读不到（可能没装 Wacom 驱动）"
                : $"服务 {ServiceName}：{DescribeStart(start.Value)}");

            var processes = CountWacomProcesses();
            parts.Add(processes < 0 ? "Wacom 进程：查询失败" : $"Wacom 进程：{processes} 个");

            var saved = SavedStart();
            parts.Add(saved == null ? "接管记录：无" : $"接管记录：原始 Start={saved.Value}");

            return string.Join(" · ", parts);
        }

        /// <summary>四步接管。返回提权脚本的完整输出（成功与否都在里面）。</summary>
        public static string Takeover()
        {
            var text = RunElevated(TakeoverScript(), "otd-wacom-takeover");
            if (string.IsNullOrWhiteSpace(text))
                return "接管失败：没有拿到执行结果（UAC 可能被拒绝，或被安全软件拦下）。";

            return (text.Contains("DONE OK") ? "接管完成。" : "接管未完成。")
                + Environment.NewLine + text.Trim();
        }

        /// <summary>按 data\wacom-state.ini 把服务写回去并启动。</summary>
        public static string Restore()
        {
            var text = RunElevated(RestoreScript(), "otd-wacom-restore");
            if (string.IsNullOrWhiteSpace(text))
                return "恢复失败：没有拿到执行结果（UAC 可能被拒绝，或被安全软件拦下）。";

            return (text.Contains("DONE OK") ? "恢复完成。" : "恢复未完成。")
                + Environment.NewLine + text.Trim();
        }

        private static string DescribeStart(int start) => start switch
        {
            0 => "0 引导（内核阶段）",
            1 => "1 系统（内核阶段）",
            2 => "2 自动",
            3 => "3 手动",
            4 => "4 已禁用",
            _ => $"Start={start}"
        };

        /// <summary>读注册表里的 Start；读不到返回 null。</summary>
        public static int? QueryStart()
        {
            var output = Run("reg.exe", ["query", @"HKLM\" + ServiceKey, "/v", "Start"], 8000);
            if (output == null)
                return null;

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("Start", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                var last = parts[^1];
                if (last.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        return Convert.ToInt32(last, 16);
                    }
                    catch
                    {
                        // 格式不认识就继续找下一行
                    }
                }
                else if (int.TryParse(last, out var value))
                {
                    return value;
                }
            }

            return null;
        }

        private static int? SavedStart()
        {
            try
            {
                if (!File.Exists(StateFile))
                    return null;

                foreach (var raw in File.ReadAllLines(StateFile))
                {
                    var line = raw.Trim();
                    if (!line.StartsWith(ServiceName + "=", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var value = line[(ServiceName.Length + 1)..].Trim();
                    return int.TryParse(value, out var parsed) ? parsed : null;
                }
            }
            catch
            {
                // 文件被占用/损坏时当作没有记录
            }

            return null;
        }

        private static int CountWacomProcesses()
        {
            try
            {
                var count = 0;
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        var name = process.ProcessName;
                        if (name.StartsWith("Wacom", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("ExpressKey", StringComparison.OrdinalIgnoreCase))
                            count++;
                    }
                    catch
                    {
                        // 受保护/已退出的进程跳过
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                return count;
            }
            catch
            {
                return -1;
            }
        }

        private static string TakeoverScript()
        {
            var log = LogPath("otd-wacom-takeover");
            var state = StateFile;

            return $$"""
                $svc = '{{ServiceName}}'
                $state = '{{Escape(state)}}'
                $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $svc
                $lines = @()

                function Get-WacomProc {
                    Get-Process | Where-Object {
                        ($_.ProcessName -like 'Wacom*' -or $_.ProcessName -like 'ExpressKey*') -and $_.Id -ne $PID
                    }
                }

                # 1. 先杀用户态进程
                $procs = @(Get-WacomProc)
                $lines += 'STEP1 proc=' + $procs.Count
                $procs | Stop-Process -Force
                Start-Sleep -Milliseconds 300

                # 2. 禁服务 + 停服务（原始值只在第一次接管时落盘）
                $orig = (Get-ItemProperty -LiteralPath $key -Name Start).Start
                $lines += 'ORIG ' + $orig
                if (-not (Test-Path -LiteralPath $state)) {
                    # 之前已经被手工禁过（Start 已经是 4）时不能把 4 当成"原始值"记下来，
                    # 否则"恢复"会把服务一直留在禁用状态；Wacom 驱动的出厂默认是 2（自动）。
                    $record = $(if ($orig -eq 4) { 2 } else { $orig })
                    $lines += 'RECORD ' + $record
                    $dir = Split-Path -Parent $state
                    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
                    ($svc + '=' + $record) | Set-Content -LiteralPath $state -Encoding ascii
                }

                $out = (& sc.exe config $svc start= disabled) 2>&1 | Out-String
                $lines += 'CFG ' + $out.Trim()
                $out = (& sc.exe stop $svc) 2>&1 | Out-String
                $lines += 'STOP ' + $out.Trim()
                Start-Sleep -Milliseconds 500

                # 3. 三轮补杀（服务停了之后它注册的进程还会把它拉起来）
                for ($i = 0; $i -lt 3; $i++) {
                    Start-Sleep -Milliseconds 300
                    $procs = @(Get-WacomProc)
                    $lines += ('KILL' + ($i + 1) + ' proc=' + $procs.Count)
                    $procs | Stop-Process -Force
                }

                # 4. 回读 Start 确认
                $now = (Get-ItemProperty -LiteralPath $key -Name Start).Start
                $lines += 'START ' + $now
                $lines += $(if ($now -eq 4) { 'DONE OK' } else { 'DONE NOT-DISABLED' })

                $lines | Set-Content -LiteralPath $log -Encoding utf8
                """;
        }

        private static string RestoreScript()
        {
            var log = LogPath("otd-wacom-restore");
            var state = StateFile;

            return $$"""
                $svc = '{{ServiceName}}'
                $state = '{{Escape(state)}}'
                $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $svc
                $lines = @()

                # 没记录就按 Wacom 驱动的默认值（自动）恢复
                $start = 2
                if (Test-Path -LiteralPath $state) {
                    $line = Get-Content -LiteralPath $state |
                        Where-Object { $_ -match '^\s*[^=]+=\s*\d+\s*$' } |
                        Select-Object -First 1
                    if ($line) { $start = [int]($line.Split('=')[1].Trim()) }
                }
                $lines += 'RESTORE start=' + $start

                $out = (& sc.exe config $svc start= $start) 2>&1 | Out-String
                $lines += 'CFG ' + $out.Trim()
                $out = (& sc.exe start $svc) 2>&1 | Out-String
                $lines += 'RUN ' + $out.Trim()

                $now = (Get-ItemProperty -LiteralPath $key -Name Start).Start
                $lines += 'START ' + $now
                $lines += $(if ($now -eq $start) { 'DONE OK' } else { 'DONE MISMATCH' })

                $lines | Set-Content -LiteralPath $log -Encoding utf8
                """;
        }

        /// <summary>
        /// 用一次 UAC 跑完整个脚本：脚本自己把结果写进日志，这里等进程退出后读回来。
        /// （和装 Wintab32.dll / 建计划任务是一套做法，避免多次提权。）
        /// </summary>
        private static string RunElevated(string script, string name)
        {
            var log = LogPath(name);
            try { File.Delete(log); } catch { }

            // 结果文件名也交给脚本
            var full = $"$ErrorActionPreference = 'SilentlyContinue'{Environment.NewLine}" +
                       $"$log = '{Escape(log)}'{Environment.NewLine}" +
                       script;

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));

            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            try
            {
                using var process = Process.Start(info);
                if (process == null)
                    return string.Empty;

                if (!process.WaitForExit(60000))
                {
                    try { process.Kill(true); } catch { }
                    return "脚本 60 秒没跑完（sc stop 可能被卡住）。";
                }
            }
            catch (Exception ex)
            {
                return "提权失败：" + ex.Message;
            }

            try
            {
                return File.Exists(log) ? File.ReadAllText(log, Encoding.UTF8) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string LogPath(string name) =>
            Path.Combine(Path.GetTempPath(), name + ".txt");

        private static string Escape(string value) => value.Replace("'", "''");

        /// <summary>跑一个控制台命令并把 stdout+stderr 一起返回；失败/超时返回 null。</summary>
        private static string? Run(string file, string[] arguments, int timeoutMs)
        {
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
                    return null;

                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(true); } catch { }
                    return output.Length > 0 ? output : null;
                }

                return output;
            }
            catch
            {
                return null;
            }
        }
    }
}
