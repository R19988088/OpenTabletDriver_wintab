using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 把原来那套 AutoHotKey 脚本的功能搬进程序本体：
    /// 两个低级钩子（键盘 / 鼠标）跑在一条独立消息循环线程上，
    /// 功能开关全部读 Tweaks，界面上勾掉就立刻生效。
    ///
    /// 关键约束：低级钩子的回调必须**极快**返回。Windows 对超过
    /// LowLevelHooksTimeout 的回调会直接把钩子卸掉（表现就是"刚启动能用、几秒后全失效"）。
    /// 所以回调里只做判断和吞事件，所有 SendInput / 剪贴板 / 注册表类的动作都从这里
    /// 丢到队列，由后台线程执行。
    /// </summary>
    public static class HotkeyEngine
    {
        private const int WhKeyboardLl = 13;
        private const int WhMouseLl = 14;
        private const int WmReinstall = 0x8001;

        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int WmMouseMove = 0x0200;
        private const int WmLeftButtonDown = 0x0201;
        private const int WmRightButtonDown = 0x0204;
        private const int WmRightButtonUp = 0x0205;
        private const int WmMouseWheel = 0x020A;
        private const int WmMouseHWheel = 0x020E;

        /// <summary>我们自己 SendInput 出去的事件带这个标记，钩子里见到就放行。</summary>
        private static readonly IntPtr OtdMarker = (IntPtr)0x4F54444C;

        private const uint LlkhfInjected = 0x10;
        private const uint LlmhfInjected = 0x01;

        private const int VkSpace = 0x20;
        private const int VkReturn = 0x0D;
        private const int VkCapital = 0x14;
        private const int VkLWin = 0x5B;
        private const int VkLShift = 0xA0;
        private const int VkMenu = 0x12;
        private const int VkRButton = 0x02;
        private const int VkVolumeUp = 0xAF;
        private const int VkVolumeDown = 0xAE;

        private const uint KeyEventKeyUp = 0x0002;
        private const uint MouseEventWheel = 0x0800;
        private const uint MouseEventHWheel = 0x1000;

        private static Tweaks _cfg = new();
        private static Thread? _thread;

        private static HookProc? _keyboardProc;
        private static HookProc? _mouseProc;
        private static IntPtr _keyboardHook;
        private static IntPtr _mouseHook;

        // 回调动作队列：钩子只往这里丢，真正的 SendInput 在后台线程跑
        private static readonly BlockingCollection<Action> Actions = new(new ConcurrentQueue<Action>());

        // Win 键长按
        private static long _winDownAt;
        private static volatile bool _winForwarded;
        private static System.Threading.Timer? _winTimer;

        // 双击 Shift
        private static long _shiftDownAt;

        // Alt + 右键单方向拖动（volatile，鼠标移动时只读这个，不做任何系统调用）
        private static volatile bool _axisActive;
        private static bool _axisLocked;
        private static bool _axisAdjusting;
        private static int _axisStartX;
        private static int _axisStartY;
        private static long _axisLockAt;

        // 任务栏矩形缓存，来源是后台 2 秒刷新，回调里只做数值比较
        private static volatile int _taskbarLeft;
        private static volatile int _taskbarTop;
        private static volatile int _taskbarRight;
        private static volatile int _taskbarBottom;
        private static volatile bool _taskbarKnown;
        private static readonly List<int[]> TaskbarRects = new();

        private static long _callbackCount;
        private static System.Threading.Timer? _taskbarTimer;
        private static System.Threading.Timer? _heartbeat;
        private static System.Threading.Timer? _watchdog;
        private static uint _threadId;
        private static long _lastEventAt;

        // 各功能实际触发的次数，界面上直接显示，能一眼看出是哪一环没走到
        private static readonly bool DebugWheel =
            Environment.GetEnvironmentVariable("OTD_WHEEL_DEBUG") == "1";

        private static long _wheelProbe;
        private static long _wheelProbeSend;
        private static long _wheelInverted;
        private static long _wheelVolume;
        private static long _capsIme;
        private static long _winHeld;
        private static long _shiftCopied;
        private static long _axisLockedCount;
        private static float _lockScreenX;
        private static float _lockScreenY;
        private static int _lockedAxis;
        private static long _everythingGuard;

        public static long WheelInverted => Interlocked.Read(ref _wheelInverted);
        public static long WheelVolume => Interlocked.Read(ref _wheelVolume);
        public static long CapsIme => Interlocked.Read(ref _capsIme);
        public static long WinHeld => Interlocked.Read(ref _winHeld);
        public static long ShiftCopied => Interlocked.Read(ref _shiftCopied);
        public static long AxisLocked => Interlocked.Read(ref _axisLockedCount);
        public static long EverythingGuarded => Interlocked.Read(ref _everythingGuard);

        /// <summary>除了自己以外还跑着几个本程序实例（旧实例的钩子会抢事件，必须提示）。</summary>
        public static string OtherInstances()
        {
            try
            {
                var others = 0;
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("OpenTabletDriver.Slim"))
                {
                    // 进程列表里可能混进已经退出的句柄，必须排除，否则会虚报"还有 N 个实例"
                    if (process.Id == Environment.ProcessId || process.HasExited)
                        continue;

                    others++;
                }

                return others == 0 ? string.Empty : $"检测到还有 {others} 个旧的本程序实例在跑，它们的钩子会抢事件，先全部关掉。";
            }
            catch
            {
                return string.Empty;
            }
        }

        public static Tweaks Config => _cfg;

        /// <summary>键盘钩子是否挂上了（给界面显示状态用）。</summary>
        public static bool KeyboardHooked => _keyboardHook != IntPtr.Zero;

        /// <summary>鼠标钩子是否挂上了。</summary>
        public static bool MouseHooked => _mouseHook != IntPtr.Zero;

        /// <summary>累计收到的事件数。长时间不动说明钩子没在工作。</summary>
        public static long EventCount => Interlocked.Read(ref _callbackCount);

        /// <summary>最后一次收到事件的时间（Environment.TickCount64）。</summary>
        public static long LastEventAt => _lastEventAt;

        /// <summary>调试用：直接注入滚轮，验证 dwData 编码对不对。</summary>
        public static bool TestWheel(int delta, int count)
        {
            var ok = true;
            for (var i = 0; i < count; i++)
                ok &= SendWheel(delta, false);
            return ok;
        }

        /// <summary>
        /// 笔的坐标是先算出来再由我们自己注入的，所以锁方向要在这里夹一次，
        /// 不能指望鼠标钩子里再改（注入的事件改 lParam 无效）。
        /// </summary>
        public static (float X, float Y) ClampAxis(float x, float y)
        {
            if (!_axisActive || !_cfg.PsBrushAxis)
                return (x, y);

            switch (_lockedAxis)
            {
                case 1: // 横向移动：纵向固定在锁定点
                    return (x, _lockScreenY);
                case 2: // 纵向移动：横向固定在锁定点
                    return (_lockScreenX, y);
                default:
                    return (x, y);
            }
        }

        /// <summary>手动在钩子线程里重挂一次。</summary>
        public static void ReinstallNow()
        {
            if (_threadId != 0)
                PostThreadMessage(_threadId, WmReinstall, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>按配置整体刷新一次（界面改开关后调用）。</summary>
        public static void Apply(Tweaks cfg)
        {
            _cfg = cfg;
            TimerResolution.Set(cfg.TimerResolution);
        }

        public static void Start()
        {
            if (_thread != null)
                return;

            _cfg = Tweaks.Load();
            TimerResolution.Set(_cfg.TimerResolution);

            // 后台执行队列
            var worker = new Thread(() =>
            {
                foreach (var action in Actions.GetConsumingEnumerable())
                {
                    try
                    {
                        action();
                    }
                    catch
                    {
                        // 单个动作失败不影响其它
                    }
                }
            })
            {
                IsBackground = true,
                Name = "OTD Hotkey Actions"
            };
            worker.Start();

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "OTD Hotkey Hooks"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            // 任务栏位置会变（换分辨率、加显示器），定时刷缓存，不在钩子里现算
            _taskbarTimer = new System.Threading.Timer(_ => RefreshTaskbarRects(), null, 0, 2000);
            _heartbeat = new System.Threading.Timer(_ =>
                Log($"events={Interlocked.Read(ref _callbackCount)} hooks={_keyboardHook}/{_mouseHook}"), null, 60000, 60000);
        }

        private static void Loop()
        {
            _keyboardProc = KeyboardCallback;
            _mouseProc = MouseCallback;

            var module = GetModuleHandle(null);
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, module, 0);
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);

            Log($"hooks keyboard={_keyboardHook} mouse={_mouseHook} admin={IsElevated()} error={Marshal.GetLastWin32Error()}");

            _threadId = (uint)GetCurrentThreadId();

            // 系统偶尔会在超时后悄悄把低级钩子卸掉（表现就是"刚启动能用、过一会全失效"）。
            // 但定时无脑重挂会在滚动/绘图时卡一下，所以改成判断：系统刚有输入、我们却一个事件没收到，才重挂。
            _watchdog = new System.Threading.Timer(_ => CheckAlive(), null, 10000, 10000);

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Message == WmReinstall)
                {
                    Reinstall();
                    continue;
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        private static void Reinstall()
        {
            try
            {
                if (_keyboardHook != IntPtr.Zero)
                    UnhookWindowsHookEx(_keyboardHook);
                if (_mouseHook != IntPtr.Zero)
                    UnhookWindowsHookEx(_mouseHook);
            }
            catch
            {
                // 取消失败不影响重装
            }

            var module = GetModuleHandle(null);
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc!, module, 0);
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc!, module, 0);

            Log($"rehook keyboard={_keyboardHook} mouse={_mouseHook} events={Interlocked.Read(ref _callbackCount)}");
        }

        private static long _aliveCount;
        private static int _aliveDeadTicks;

        /// <summary>每 10 秒检查一次钩子还活着没。</summary>
        private static void CheckAlive()
        {
            try
            {
                if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
                {
                    PostThreadMessage(_threadId, WmReinstall, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                var info = new LastInputInfo();
                if (!GetLastInputInfo(ref info))
                    return;

                var idle = unchecked((uint)Environment.TickCount - info.Time);
                var count = Interlocked.Read(ref _callbackCount);

                if (idle < 10000)
                {
                    _aliveDeadTicks = count == _aliveCount ? _aliveDeadTicks + 1 : 0;
                    if (_aliveDeadTicks >= 2)
                    {
                        _aliveDeadTicks = 0;
                        Log("watchdog: 有输入但没收到事件，重挂钩子");
                        PostThreadMessage(_threadId, WmReinstall, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                else
                {
                    _aliveDeadTicks = 0;
                }

                _aliveCount = count;
            }
            catch
            {
                // 检查失败就当正常
            }
        }

        private static void Post(Action action)
        {
            try
            {
                Actions.Add(action);
            }
            catch
            {
                // 队列满了就丢掉，绝不能在钩子里等
            }
        }

        #region 键盘

        private static IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0)
                return CallNextHookEx(_keyboardHook, code, wParam, lParam);

            _callbackCount++;
            _lastEventAt = Environment.TickCount64;

            var message = (int)wParam;
            var down = message is WmKeyDown or WmSysKeyDown;
            var up = message is WmKeyUp or WmSysKeyUp;

            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var vk = (int)data.VkCode;

            // 自己发出去的事件不要再过一遍
            var injected = (data.Flags & LlkhfInjected) != 0;

            if (injected)
            {
                // 长按 Win 时我们自己补发的那一对要放行
                if (vk == VkLWin && _winForwarded)
                    return CallNextHookEx(_keyboardHook, code, wParam, lParam);

                return CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }

            if (vk == VkCapital && _cfg.CapsLockIme)
            {
                // 换成切换输入法，别再切大小写
                if (down)
                {
                    Interlocked.Increment(ref _capsIme);
                    Post(() => SendKeyChord(new[] { 0x11 }, VkSpace));
                }

                return (IntPtr)1;
            }

            if (vk == VkLWin && _cfg.WinKeyHold)
                return HandleWinKey(down, up);

            if (vk == VkLShift && down && _cfg.DoubleShiftCopyName)
                HandleShift();

            if (vk == VkSpace && down && _cfg.EverythingGuard && IsForegroundClass("EVERYTHING"))
                Post(GuardClipboardBriefly);

            if (vk == VkReturn && down && _cfg.EverythingGuard && IsForegroundClass("EVERYTHING"))
                Post(GuardClipboardBriefly);

            return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        private static IntPtr HandleWinKey(bool down, bool up)
        {
            if (down)
            {
                _winDownAt = Environment.TickCount64;
                _winForwarded = false;

                _winTimer?.Dispose();
                _winTimer = new System.Threading.Timer(_ =>
                {
                    if (_winForwarded || !IsKeyDown(VkLWin))
                        return;

                    _winForwarded = true;
                    Interlocked.Increment(ref _winHeld);
                    Post(() => SendKey(VkLWin, false));
                }, null, 1500, Timeout.Infinite);

                return (IntPtr)1;
            }

            if (up)
            {
                _winTimer?.Dispose();
                _winTimer = null;

                if (_winForwarded)
                {
                    _winForwarded = false;
                    Post(() => SendKey(VkLWin, true));
                }
            }

            return (IntPtr)1;
        }

        private static void HandleShift()
        {
            var now = Environment.TickCount64;
            var doubled = now - _shiftDownAt is > 0 and < 400;
            _shiftDownAt = now;

            if (doubled)
            {
                Interlocked.Increment(ref _shiftCopied);
                Post(() => _ = Task.Run(CopyFileNamesAsync));
            }
        }

        #endregion

        #region 鼠标

        private static IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0)
                return CallNextHookEx(_mouseHook, code, wParam, lParam);

            _callbackCount++;
            _lastEventAt = Environment.TickCount64;

            var message = (int)wParam;

            // 移动事件是最高频的，绝大多数时候什么也不做，连结构体都不解析
            if (message == WmMouseMove)
            {
                if (!_axisActive)
                    return CallNextHookEx(_mouseHook, code, wParam, lParam);

                var move = Marshal.PtrToStructure<MsLlHookStruct>(lParam);

                // 笔的移动是程序自己 SendInput 出来的，带注入标记，锁方向时照样要处理；
                // 平时（没按 Alt + 右键）这类事件直接放过，别在钩子里白干活。
                if ((move.Flags & LlmhfInjected) != 0 && _axisAdjusting)
                    return CallNextHookEx(_mouseHook, code, wParam, lParam);

                FollowAxis(move.X, move.Y);
                return CallNextHookEx(_mouseHook, code, wParam, lParam);
            }

            var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            var injected = (data.Flags & LlmhfInjected) != 0;

            // 滚轮：任务栏调音量，其它地方按开关反向
            if (message is WmMouseWheel or WmMouseHWheel)
            {
                // 自己注入的滚轮，直接放行，否则会自己套自己
                if (data.ExtraInfo == OtdMarker || (injected && !DebugWheel))
                    return CallNextHookEx(_mouseHook, code, wParam, lParam);

                var delta = (short)((data.MouseData >> 16) & 0xFFFF);
                if (delta == 0)
                    return CallNextHookEx(_mouseHook, code, wParam, lParam);

                var horizontal = message == WmMouseHWheel;
                var onTaskbar = _cfg.TaskbarVolume && !horizontal && IsTaskbarWindow(data.X, data.Y);
                var x = data.X;
                var y = data.Y;
                var taskbarFlag = onTaskbar;

                if (Interlocked.Increment(ref _wheelProbe) <= 10 || _wheelProbe % 25 == 0)
                    Post(() => Log("wheel d=" + delta + " at " + x + "," + y + " taskbar=" + taskbarFlag + " invert=" + _cfg.InvertScroll));

                if (onTaskbar)
                {
                    // 同步注入，注入成功才吞掉原始滚轮；失败就放行，绝不让滚动彻底失效
                    var volumeKey = delta > 0 ? VkVolumeUp : VkVolumeDown;
                    Interlocked.Increment(ref _wheelVolume);
                    Post(() => Log("volume d=" + delta + " at " + x + "," + y));
                    Post(() =>
                    {
                        var ok = SendKeyPair(volumeKey);
                        Log("volume d=" + delta + " send=" + ok);
                    });
                    return (IntPtr)1;
                }

                if (_cfg.InvertScroll)
                {
                    Interlocked.Increment(ref _wheelInverted);
                    Post(() =>
                    {
                        var sent = SendWheel(-delta, horizontal);
                        if (Interlocked.Increment(ref _wheelProbeSend) <= 10 || _wheelProbeSend % 25 == 0)
                            Log("invert " + delta + " -> " + (-delta) + " send=" + sent);
                    });
                    return (IntPtr)1;
                }

                return CallNextHookEx(_mouseHook, code, wParam, lParam);
            }

            if (message == WmLeftButtonDown && _cfg.EverythingGuard && IsForegroundClass("EVERYTHING"))
                Post(GuardClipboardBriefly);

            // 右键按下这一下才查 Alt / 右键状态，之后移动事件只看 _axisActive
            if (message == WmRightButtonDown && _cfg.PsBrushAxis && IsKeyDown(VkMenu))
            {
                _axisActive = true;
                _axisLocked = false;
                _axisStartX = data.X;
                _axisStartY = data.Y;
                _axisLockAt = 0;
            }
            else if (message == WmRightButtonUp)
            {
                _axisActive = false;
                _lockedAxis = 0;
            }

            return CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        /// <summary>按住 Alt + 右键拖动时，把鼠标锁到先动起来的那个方向。</summary>
        private static void FollowAxis(int x, int y)
        {
            if (!_cfg.PsBrushAxis || !IsKeyDown(VkRButton))
            {
                _axisActive = false;
                _lockedAxis = 0;
                return;
            }

            var now = Environment.TickCount64;
            var dx = x - _axisStartX;
            var dy = y - _axisStartY;

            if (!_axisLocked)
            {
                if (Math.Abs(dx) <= 5 && Math.Abs(dy) <= 5)
                    return;

                _axisLocked = true;
                Interlocked.Increment(ref _axisLockedCount);

                // 先动起来的方向就是锁定的方向，另一个轴固定在起点
                var lockHorizontal = Math.Abs(dx) >= Math.Abs(dy);
                _lockedAxis = lockHorizontal ? 1 : 2;
                _lockScreenX = _axisStartX;
                _lockScreenY = _axisStartY;

                _axisStartX = x;
                _axisStartY = y;
                _axisLockAt = now;
                return;
            }

            // 锁定后 600ms 内保持方向，之后再重新判定一次
            if (now - _axisLockAt > 600)
            {
                _axisLockAt = now;
                _axisStartX = x;
                _axisStartY = y;
                return;
            }

            var horizontal = Math.Abs(dx) >= Math.Abs(dy);

            // SetCursorPos 会在钩子里触发新一轮事件，用标记防递归，并且放到队列里做
            _axisAdjusting = true;
            Post(() =>
            {
                try
                {
                    if (horizontal)
                        SetCursorPos(x, _axisStartY);
                    else
                        SetCursorPos(_axisStartX, y);
                }
                finally
                {
                    _axisAdjusting = false;
                }
            });
        }

        /// <summary>Everything 里空格/回车/单击会把文件名塞剪贴板，这里把它撤回去。</summary>
        private static void GuardClipboardBriefly()
        {
            Interlocked.Increment(ref _everythingGuard);
            var before = ClipHelper.GetText();
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 12; i++)
                {
                    await Task.Delay(100);
                    var now = ClipHelper.GetText();
                    if (!string.IsNullOrEmpty(now) && now != before)
                    {
                        ClipHelper.SetText(before ?? string.Empty);
                        return;
                    }
                }
            });
        }

        private static async Task CopyFileNamesAsync()
        {
            try
            {
                var before = ClipHelper.GetText();
                SendKeyChord(new[] { 0x11 }, 0x43); // Ctrl+C

                string? result = null;
                for (var i = 0; i < 8; i++)
                {
                    await Task.Delay(120);

                    var files = ClipHelper.GetFileList();
                    if (files.Count > 0)
                    {
                        result = string.Join(Environment.NewLine,
                            files.Select(f => Path.GetFileName(f)));
                        break;
                    }

                    var text = ClipHelper.GetText();
                    if (!string.IsNullOrEmpty(text) && text != before)
                    {
                        var lines = text
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => l.Trim().Trim('"'))
                            .Where(l => l.Length > 0)
                            .ToList();

                        if (lines.Count > 0 && lines.Any(l => l.Contains('\\') || l.Contains(':')))
                        {
                            result = string.Join(Environment.NewLine,
                                lines.Select(l => Path.GetFileName(l)));
                        }
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(result))
                    ClipHelper.SetText(result);
            }
            catch
            {
                // 复制失败就算了，不要弹窗打扰
            }
        }

        #endregion

        #region 环境判断

        /// <summary>
        /// 鼠标下的窗口是不是任务栏：从该窗口一直往上找父窗口，撞到 Shell_TrayWnd /
        /// Shell_SecondaryTrayWnd 就算。Win10 和 Win11 的任务栏结构不同，但这两层都在。
        /// </summary>
        private static bool IsTaskbarWindow(int screenX, int screenY)
        {
            var point = new WinPoint { X = screenX, Y = screenY };
            var hwnd = WindowFromPoint(point);
            for (var level = 0; level < 16 && hwnd != IntPtr.Zero; level++)
            {
                var name = ClassNameOf(hwnd);
                if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                    return true;

                hwnd = GetAncestor(hwnd, 1); // GA_PARENT
            }

            return IsTaskbarPoint(screenX, screenY);
        }

        private static string ClassNameOf(IntPtr hwnd)
        {
            try
            {
                var buffer = new System.Text.StringBuilder(128);
                return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsTaskbarPoint(int x, int y)
        {
            if (!_taskbarKnown)
                return false;

            if (x >= _taskbarLeft && x < _taskbarRight && y >= _taskbarTop && y < _taskbarBottom)
                return true;

            lock (TaskbarRects)
            {
                foreach (var rect in TaskbarRects)
                {
                    if (x >= rect[0] && x < rect[2] && y >= rect[1] && y < rect[3])
                        return true;
                }
            }

            return false;
        }

        private static void RefreshTaskbarRects()
        {
            try
            {
                var rects = new List<int[]>();
                var main = FindWindow("Shell_TrayWnd", null);
                if (main != IntPtr.Zero && TryGetRect(main, out var primary))
                    rects.Add(primary);

                var secondary = FindWindow("Shell_SecondaryTrayWnd", null);
                while (secondary != IntPtr.Zero)
                {
                    if (TryGetRect(secondary, out var rect))
                        rects.Add(rect);
                    secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null);
                }

                if (rects.Count == 0)
                {
                    _taskbarKnown = false;
                    return;
                }

                lock (TaskbarRects)
                {
                    TaskbarRects.Clear();
                    TaskbarRects.AddRange(rects);
                }

                _taskbarLeft = rects[0][0];
                _taskbarTop = rects[0][1];
                _taskbarRight = rects[0][2];
                _taskbarBottom = rects[0][3];
                _taskbarKnown = true;
            }
            catch
            {
                _taskbarKnown = false;
            }
        }

        private static bool TryGetRect(IntPtr handle, out int[] rect)
        {
            rect = new int[4];
            if (!GetWindowRect(handle, out var native))
                return false;

            rect[0] = native.Left;
            rect[1] = native.Top;
            rect[2] = native.Right;
            rect[3] = native.Bottom;
            return native.Right > native.Left && native.Bottom > native.Top;
        }

        private static bool IsForegroundClass(string className)
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero)
                return false;

            var name = ClassName(handle);
            return name.Contains(className, StringComparison.OrdinalIgnoreCase);
        }

        private static string ClassName(IntPtr handle)
        {
            var buffer = new StringBuilder(64);
            GetClassName(handle, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        private static bool IsElevated()
        {
            try
            {
                return new System.Security.Principal.WindowsPrincipal(
                        System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
        /// <summary>钩子没挂上时静默失效最难受，所以留一行日志。</summary>
        private static void Log(string text)
        {
            try
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OTDWintab");
                Directory.CreateDirectory(root);
                File.AppendAllText(Path.Combine(root, "hotkeys.log"),
                    $"{DateTime.Now:HH:mm:ss} [{Environment.ProcessId}] {text}{Environment.NewLine}");
            }
            catch
            {
                // 日志写不了不影响功能
            }
        }

        #endregion

        #region 发键 / 发滚轮

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyInput Key;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int Dx;
            public int Dy;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        private static bool SendKey(int vk, bool up)
        {
            var inputs = new[]
            {
                new Input
                {
                    Type = 1,
                    Data = new InputUnion
                    {
                        Key = new KeyInput { VirtualKey = (ushort)vk, Flags = up ? KeyEventKeyUp : 0, ExtraInfo = OtdMarker }
                    }
                }
            };
            return SendInput(1, inputs, Marshal.SizeOf<Input>()) > 0;
        }

        private static void SendKeyChord(int[] modifiers, int key)
        {
            var list = new List<Input>();
            foreach (var modifier in modifiers)
                list.Add(KeyInputOf((ushort)modifier, false));
            list.Add(KeyInputOf((ushort)key, false));
            list.Add(KeyInputOf((ushort)key, true));
            for (var i = modifiers.Length - 1; i >= 0; i--)
                list.Add(KeyInputOf((ushort)modifiers[i], true));

            SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<Input>());
        }

        private static Input KeyInputOf(ushort vk, bool up) => new()
        {
            Type = 1,
            Data = new InputUnion { Key = new KeyInput { VirtualKey = vk, Flags = up ? KeyEventKeyUp : 0 } }
        };

        /// <summary>
        /// 直接把 WM_MOUSEWHEEL 投给鼠标下的窗口。走消息而不是注入，
        /// 别的全局钩子（比如残留的旧实例）就拦不到我们补发的这一下。
        /// </summary>
        private static bool PostWheel(int screenX, int screenY, int delta, bool horizontal)
        {
            var point = new WinPoint { X = screenX, Y = screenY };
            var hwnd = WindowFromPoint(point);
            if (hwnd == IntPtr.Zero)
                return false;

            var root = GetAncestor(hwnd, 2);
            var target = root != IntPtr.Zero ? root : hwnd;

            var wp = (IntPtr)((delta << 16) | 0);
            var lp = (IntPtr)((screenY << 16) | (screenX & 0xFFFF));
            var message = horizontal ? WmMouseHWheel : WmMouseWheel;
            return PostMessage(target, message, wp, lp);
        }

        private static bool SendWheel(int delta, bool horizontal)
        {
            var inputs = new[]
            {
                new Input
                {
                    Type = 0,
                    Data = new InputUnion
                    {
                        Mouse = new MouseInput
                        {
                            MouseData = unchecked((uint)delta),
                            Flags = horizontal ? MouseEventHWheel : MouseEventWheel,
                            ExtraInfo = OtdMarker
                        }
                    }
                }
            };
            return SendInput(1, inputs, Marshal.SizeOf<Input>()) > 0;
        }

        /// <summary>按一下再松开，返回是否注入成功（失败就别吞原始事件）。</summary>
        private static bool SendKeyPair(int vk) =>
            SendKey(vk, false) && SendKey(vk, true);

        #endregion

        #region Win32

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public uint VkCode;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MsLlHookStruct
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint Size;
            public uint Time;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LastInputInfo info);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinPoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(WinPoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
            public uint Private;
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint threadId, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Msg msg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref Msg msg);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? name);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vk);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int count);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        #endregion
    }

    /// <summary>低延迟模式：把系统计时器分辨率提到 0.5ms。</summary>
    internal static class TimerResolution
    {
        [DllImport("ntdll.dll")]
        private static extern int NtSetTimerResolution(int desired, bool set, out int actual);

        private static bool _active;

        public static void Set(bool enable)
        {
            if (enable == _active)
                return;

            try
            {
                if (enable)
                    NtSetTimerResolution(5000, true, out _);
                else
                    NtSetTimerResolution(0, false, out _);

                _active = enable;
            }
            catch
            {
                // 有些系统不让改，静默忽略
            }
        }

        public static bool Active => _active;
    }

    /// <summary>读写系统剪贴板（文本 / 文件列表）。</summary>
    internal static class ClipHelper
    {
        private const uint CfUnicodeText = 13;
        private const uint CfHDrop = 15;
        private const uint GmemMoveable = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint format, IntPtr data);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? buffer, uint length);

        public static string? GetText()
        {
            if (!OpenClipboard(IntPtr.Zero))
                return null;

            try
            {
                var handle = GetClipboardData(CfUnicodeText);
                if (handle == IntPtr.Zero)
                    return null;

                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                    return null;

                try
                {
                    return Marshal.PtrToStringUni(pointer);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        public static List<string> GetFileList()
        {
            var result = new List<string>();
            if (!OpenClipboard(IntPtr.Zero))
                return result;

            try
            {
                var handle = GetClipboardData(CfHDrop);
                if (handle == IntPtr.Zero)
                    return result;

                var count = DragQueryFile(handle, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < count; i++)
                {
                    var length = DragQueryFile(handle, i, null, 0);
                    var buffer = new StringBuilder((int)length + 1);
                    DragQueryFile(handle, i, buffer, (uint)buffer.Capacity);
                    result.Add(buffer.ToString());
                }
            }
            finally
            {
                CloseClipboard();
            }

            return result;
        }

        public static bool SetText(string text)
        {
            if (!OpenClipboard(IntPtr.Zero))
                return false;

            try
            {
                EmptyClipboard();

                var bytes = (text.Length + 1) * 2;
                var memory = GlobalAlloc(GmemMoveable, (UIntPtr)(uint)bytes);
                if (memory == IntPtr.Zero)
                    return false;

                var pointer = GlobalLock(memory);
                if (pointer == IntPtr.Zero)
                    return false;

                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * 2, 0);
                GlobalUnlock(memory);

                return SetClipboardData(CfUnicodeText, memory) != IntPtr.Zero;
            }
            finally
            {
                CloseClipboard();
            }
        }
    }
}
