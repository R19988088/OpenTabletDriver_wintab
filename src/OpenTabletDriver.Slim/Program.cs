using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Avalonia;
using OpenTabletDriver.Plugin.Platform.Pointer;

namespace OpenTabletDriver.Slim
{
    internal static class Program
    {
        internal static string? StartupPage { get; private set; }

        [STAThread]
        public static void Main(string[] args)
        {
            // 重启交接：等上一个实例彻底退出（它占着数位板）
            var waitArg = args.FirstOrDefault(a => a.StartsWith("--wait-pid=", StringComparison.Ordinal));
            if (waitArg != null && int.TryParse(waitArg["--wait-pid=".Length..], out var waitPid))
            {
                try
                {
                    using var previous = System.Diagnostics.Process.GetProcessById(waitPid);
                    previous.WaitForExit(15000);
                }
                catch
                {
                    // 已经退出了
                }

                Thread.Sleep(500);
            }

            var mode = args.FirstOrDefault(arg => arg.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant();

            // 诊断/接管类命令不占设备、也不该把正在用的实例顶掉，所以放在单实例守卫之前
            if (mode == "--wacom-status")
            {
                WriteReport("wacom-status.txt", WacomControl.Status());
                return;
            }

            if (mode == "--wacom-takeover")
            {
                WriteReport("wacom-takeover.txt", WacomControl.Takeover());
                return;
            }

            if (mode == "--wacom-restore")
            {
                WriteReport("wacom-restore.txt", WacomControl.Restore());
                return;
            }

            // 单实例：后开的留下，先开的自己退出（旧版本直接结束掉）
            Instances.EnforceSingle();

            // 提权清理：管理员权限下把其它实例全结束掉，然后直接退出（不开界面）
            if (mode == "--killothers")
            {
                var killed = Instances.KillOthers();
                Thread.Sleep(800);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "otd-killothers.txt"), $"killed={killed}");
                return;
            }

            // 裸读检测：绕开整套驱动，直接用 HidSharp 打开 PTH-660 的 HID 口，
            // 把每次读到的原始报告落盘。用来区分"设备只发一条"和"我们的读取只拿到一条"。
            if (mode == "--hiddump")
            {
                HidDump(args);
                return;
            }
            if (mode == "--selftest")
            {
                WriteReport("selftest.txt", SlimHost.RunSelfTest());
                return;
            }

            if (mode == "--buttontest")
            {
                WriteReport("buttontest.txt", SlimHost.RunButtonTest());
                return;
            }

            if (mode == "--feeltest")
            {
                WriteReport("feeltest.txt", SlimHost.RunFeelTest());
                return;
            }

            // 触摸/环验收：接上输出跑几秒，只统计报文种类（不开界面、不点桌面）
            if (mode == "--touchdump")
            {
                var seconds = 10;
                foreach (var arg in args)
                {
                    if (arg.StartsWith("--seconds=", StringComparison.Ordinal)
                        && int.TryParse(arg["--seconds=".Length..], out var parsed))
                        seconds = parsed;
                }

                WriteReport("touchdump.txt", SlimHost.RunTouchDump(seconds));
                return;
            }

            if (mode == "--realtest")
            {
                WriteReport("realtest.txt", RealClickTest(args));
                return;
            }

            // 调试用：--wheeltest 直接注入滚轮，配合 scrolltest.ps1 检查滚动是否真的发生
            if (mode == "--wheeltest")
            {
                Thread.Sleep(200);
                var wheelOk = HotkeyEngine.TestWheel(-120, 3);
                File.WriteAllText("wheeltest.txt", wheelOk ? "OK" : "FAIL");
                return;
            }

            // 调试/截图用：--page=映射 直接打开某一页
            var pageArg = args.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.Ordinal));
            if (pageArg != null)
                StartupPage = pageArg["--page=".Length..];

            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .StartWithClassicDesktopLifetime(args);
        }

        /// <summary>
        /// 用真实的内置指针在指定屏幕坐标上做一次移动 + 左键按下/抬起，
        /// 用来验证 SendInput 真的落到了目标窗口（正常使用时不走这里）。
        /// </summary>
        private static string RealClickTest(string[] args)
        {
            var x = args.Length > 1 ? float.Parse(args[1]) : 200f;
            var y = args.Length > 2 ? float.Parse(args[2]) : 200f;

            var pointer = new WindowsPointer();
            pointer.SetPosition(new Vector2(x, y));
            pointer.Flush();
            Thread.Sleep(150);
            pointer.MouseDown(MouseButton.Left);
            Thread.Sleep(60);
            pointer.MouseUp(MouseButton.Left);

            return $"目标坐标  : {x:0},{y:0}" + Environment.NewLine +
                   $"虚拟屏幕  : {pointer.ScreenSize.X:0}x{pointer.ScreenSize.Y:0}" + Environment.NewLine +
                   "已发送    : 移动到目标 → 左键按下 → 左键抬起";
        }

        private static void HidDump(string[] args)
        {
            var seconds = 60;
            foreach (var arg in args)
            {
                if (arg.StartsWith("--seconds=", StringComparison.Ordinal) &&
                    int.TryParse(arg["--seconds=".Length..], out var parsed))
                    seconds = parsed;
            }

            using var log = new StreamWriter("hiddump.txt", false);
            void Line(string text)
            {
                log.WriteLine(text);
                log.Flush();
            }

            foreach (var device in HidSharp.DeviceList.Local.GetHidDevices(0x056A, 0x0357))
            {
                Line($"DEV path={device.DevicePath} in={device.GetMaxInputReportLength()} out={device.GetMaxOutputReportLength()}");

                try
                {
                    using var stream = device.Open();
                    Line("OPEN ok");

                    // Wacom 需要先发 FeatureInit(0x02,0x02) 才会进入 192 字节 raw 模式
                    try
                    {
                        stream.SetFeature(new byte[] { 0x02, 0x02 });
                        Line("FEATURE sent");
                    }
                    catch (Exception ex)
                    {
                        Line("FEATURE fail " + ex.GetType().Name + " " + ex.Message);
                    }

                    var deadline = Environment.TickCount64 + seconds * 1000L;
                    while (Environment.TickCount64 < deadline)
                    {
                        var started = Environment.TickCount64;
                        var data = stream.Read();
                        var head = string.Join(" ", data.Length >= 8 ? data[..8] : data);
                        Line($"{DateTime.Now:HH:mm:ss.fff} len={data.Length} el={Environment.TickCount64 - started}ms head={head}");
                    }
                }
                catch (Exception ex)
                {
                    Line("ERR " + ex.GetType().Name + " " + ex.Message);
                }
            }

            Line("done");
        }
        private static void WriteReport(string fileName, string report)
        {
            var path = Path.Join(AppContext.BaseDirectory, fileName);
            File.WriteAllText(path, report);
            Console.WriteLine(report);
        }
    }
}
