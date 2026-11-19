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

        /// <summary>
        /// 接管前 System32 里那份 Wintab32.dll 的留底。只有它不是本程序的 shim 时才会写这个文件，
        /// 恢复时原样放回去；早前就已经被换成 shim 的话这台机器上本来就没有 Wacom 原版可备份。
        /// </summary>
        public static string DllBackup =>
            Path.Combine(AppContext.BaseDirectory, "data", "Wintab32-before-wacom-takeover.dll");

        private static string SystemDll =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Wintab32.dll");

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

            parts.Add(DescribeDll());

            return string.Join(" · ", parts);
        }

        /// <summary>Wintab32.dll 一眼状态：本程序 / 别人（Wacom 原版） / 没有，再加上有没有留底。</summary>
        private static string DescribeDll()
        {
            var state = WintabSetup.Detect();
            var text = state switch
            {
                WintabState.Ours => "Wintab32.dll：本程序的 shim",
                WintabState.Outdated => "Wintab32.dll：不是本程序（Wacom 原版）",
                WintabState.Missing => "Wintab32.dll：System32 里没有",
                _ => "Wintab32.dll：随包缺失"
            };

            text += File.Exists(DllBackup) ? "（有接管前留底）" : "（无留底）";
            return text;
        }

        /// <summary>四步接管 + 换 Wintab32.dll。返回提权脚本的完整输出（成功与否都在里面）。</summary>
        public static string Takeover()
        {
            // 随包 shim 缺了就先用内嵌那份释放出来，接管时要拿它覆盖 System32
            WintabSetup.EnsureLocal();

            var text = RunElevated(TakeoverScript(), "otd-wacom-takeover");
            if (string.IsNullOrWhiteSpace(text))
                return "接管失败：没有拿到执行结果（UAC 可能被拒绝，或被安全软件拦下）。";

            var served = text.Contains("DONE OK");
            var dll = text.Contains("DLL OURS");
            var verdict = served && dll
                ? "接管完成：服务已禁用、进程已清、Wintab32.dll 已换成本程序的。"
                : "接管未完成，看下面哪一步没成。";

            return verdict + Environment.NewLine + text.Trim();
        }

        /// <summary>按 data\wacom-state.ini 把服务写回去并启动，并把接管前的 Wintab32.dll 还回去。</summary>
        public static string Restore()
        {
            var text = RunElevated(RestoreScript(), "otd-wacom-restore");
            if (string.IsNullOrWhiteSpace(text))
                return "恢复失败：没有拿到执行结果（UAC 可能被拒绝，或被安全软件拦下）。";

            var served = text.Contains("DONE OK");
            var dll = text.Contains("DLL RESTORED");
            var verdict = served && dll
                ? "恢复完成：服务已按原值启动，Wintab32.dll 已还原为接管前那份。"
                : served && text.Contains("DLL NO-BACKUP")
                    ? "服务已恢复；Wintab32.dll 没有留底（接管前就已换成本程序 shim，机器上没有 Wacom 原版），保持现状。"
                    : "恢复未完成，看下面哪一步没成。";

            return verdict + Environment.NewLine + text.Trim();
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
            var shim = WintabSetup.LocalDll;
            var backup = DllBackup;

            return $$"""
                $svc = '{{ServiceName}}'
                $state = '{{Escape(state)}}'
                $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $svc
                $shim = '{{Escape(shim)}}'
                $backup = '{{Escape(backup)}}'
                $sys = Join-Path $env:SystemRoot 'System32\Wintab32.dll'
                $lines = @()

                function Get-WacomProc {
                    Get-Process | Where-Object { ($_.ProcessName -like 'Wacom*' -or $_.ProcessName -like 'ExpressKey*' -or $_.ProcessName -like '*TabletUser*') -and $_.Id -ne $PID }
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

                # 4. Wintab32.dll：先把"不是本程序的"那份留底，再换成我们的 shim
                if (Test-Path -LiteralPath $shim) {
                    $shimHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $shim).Hash
                    if (Test-Path -LiteralPath $sys) {
                        $sysHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sys).Hash
                        if ($sysHash -ne $shimHash -and -not (Test-Path -LiteralPath $backup)) {
                            $dir = Split-Path -Parent $backup
                            if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
                            Copy-Item -LiteralPath $sys -Destination $backup -Force
                            $lines += 'DLL BACKUP ok'
                        }
                    }
                    try {
                        Copy-Item -LiteralPath $shim -Destination $sys -Force
                    } catch {
                        # System32 里那份可能正被 PS/SAI 加载，映像文件允许改名
                        try {
                            Move-Item -LiteralPath $sys -Destination ($sys + '.old') -Force
                            Copy-Item -LiteralPath $shim -Destination $sys -Force
                        } catch {
                            $lines += 'DLL COPY FAIL ' + $_.Exception.Message
                        }
                    }
                    if (Test-Path -LiteralPath $sys) {
                        $nowHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sys).Hash
                        $lines += $(if ($nowHash -eq $shimHash) { 'DLL OURS' } else { 'DLL MISMATCH' })
                    }
                } else {
                    $lines += 'DLL NO-SHIM'
                }

                # 5. 回读 Start 确认
                $now = (Get-ItemProperty -LiteralPath $key -Name Start).Start
                $lines += 'START ' + $now
                $left = @(Get-WacomProc).Count
                $lines += 'PROCS ' + $left
                $lines += $(if ($now -eq 4) { 'DONE OK' } else { 'DONE NOT-DISABLED' })

                $lines | Set-Content -LiteralPath $log -Encoding utf8
                """;
        }

        private static string RestoreScript()
        {
            var log = LogPath("otd-wacom-restore");
            var state = StateFile;
            var backup = DllBackup;

            return $$"""
                $svc = '{{ServiceName}}'
                $state = '{{Escape(state)}}'
                $backup = '{{Escape(backup)}}'
                $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $svc
                $sys = Join-Path $env:SystemRoot 'System32\Wintab32.dll'
                $lines = @()

                # 1. 服务按接管前的 Start 值写回去并启起来
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

                # 2. Wintab32.dll：把接管前留底的那份放回去（有的话）
                if (Test-Path -LiteralPath $backup) {
                    try {
                        Copy-Item -LiteralPath $backup -Destination $sys -Force
                    } catch {
                        try {
                            if (Test-Path -LiteralPath $sys) {
                                Move-Item -LiteralPath $sys -Destination ($sys + '.old') -Force
                            }
                            Copy-Item -LiteralPath $backup -Destination $sys -Force
                        } catch {
                            $lines += 'DLL RESTORE FAIL ' + $_.Exception.Message
                        }
                    }
                    if (Test-Path -LiteralPath $sys) {
                        $backHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $backup).Hash
                        $nowHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sys).Hash
                        $lines += $(if ($nowHash -eq $backHash) { 'DLL RESTORED' } else { 'DLL MISMATCH' })
                    }
                } else {
                    # 没有留底：说明接管前本来就是我们自己的 shim，机器上没有 Wacom 原版可还
                    $lines += 'DLL NO-BACKUP'
                }

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
