using System;
using System.Diagnostics;
using System.Threading;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 单实例守卫。本程序会装全局钩子、独占数位板和一块共享内存，
    /// 同时跑两份的后果是滚轮被互相吞掉、笔坐标卡死。
    ///
    /// 规则按用户要求：**后开的留下，先开的自己退出**。
    /// 注意：绝不能因为"已经有实例在跑"就把自己退掉——那会表现为双击完全没反应。
    /// </summary>
    public static class Instances
    {
        private const string ProcessName = "OpenTabletDriver.Slim";
        private const string MutexName = @"Local\OTDSlim.Instance";

        /// <summary>清理过程落盘一行，用来判断到底杀掉几个、失败几个。</summary>
        private static void Note(string text)
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OTDWintab");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, $"app-{Environment.ProcessId}.log"),
                    $"{DateTime.Now:HH:mm:ss} [Cleanup] {text}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        private static Mutex? _mutex;
        private static long _killed;

        /// <summary>被关掉的旧实例数量（界面上显示用）。</summary>
        public static long Killed => Interlocked.Read(ref _killed);

        /// <summary>
        /// 启动时调用：把旧实例结束掉（后开的顶掉先开的），再占住单实例互斥体。
        /// 结束动作放后台线程，绝不拖住窗口出现。
        /// </summary>
        public static void EnforceSingle()
        {
            var killer = new Thread(() =>
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (KillOthers() == 0)
                        break;

                    Thread.Sleep(400);
                }
            })
            {
                IsBackground = true,
                Name = "single-instance-kill"
            };
            killer.Start();

            try
            {
                // 只用来占名字，不拿它做任何"让谁退出"的判断：
                // 之前的实现会在互斥体被占时给自己发退出事件，结果新实例把自己秒退，表现为双击没反应。
                _mutex = new Mutex(false, MutexName);
            }
            catch
            {
                // 互斥体拿不到也继续跑，至少钩子还能用
            }
        }

        /// <summary>把除自己以外的同程序实例全部结束，返回结束掉的数量。</summary>
        public static int KillOthers()
        {
            var killed = 0;
            var failed = 0;
            try
            {
                foreach (var process in Process.GetProcessesByName(ProcessName))
                {
                    if (process.Id == Environment.ProcessId)
                        continue;

                    try
                    {
                        if (process.HasExited)
                            continue;

                        process.Kill(true);
                        process.WaitForExit(500);
                        killed++;
                    }
                    catch
                    {
                        // 权限更高或已被保护的实例杀不掉，计数后跳过
                        failed++;
                    }
                }
            }
            catch
            {
                // 枚举失败不影响启动
            }

            if (killed > 0)
                Interlocked.Add(ref _killed, killed);

            Note($"cleanup killed={killed} failed={failed}");

            // 结束失败时再用 taskkill 兜一次（有时 .NET 的 OpenProcess 被拦，taskkill 却能过）
            if (failed > 0)
            {
                try
                {
                    using var taskkill = Process.Start(new ProcessStartInfo("taskkill", "/F /IM OpenTabletDriver.Slim.exe")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    taskkill?.WaitForExit(3000);
                    Note("cleanup taskkill executed");
                }
                catch
                {
                    Note("cleanup taskkill failed");
                }
            }

            return killed;
        }
    }
}
