using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Components;
using OpenTabletDriver.Plugin.Logging;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Platform.Pointer;
using OpenTabletDriver.Plugin.Tablet;
using OpenTabletDriver.Wintab;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 精简版的启动编排：只装 core + configurations + 内置 Wintab 输出三块，
    /// 不含插件管理、设置文件、RPC、自动更新。
    /// </summary>
    public sealed class SlimHost : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly Driver _driver;
        private WintabOutputMode? _outputMode;
        private WindowsPointer? _pointer;
        private PenButtonPipeline? _buttons;

        public event Action<string>? LogReceived;
        public event Action? StateChanged;

        /// <summary>显示器拓扑变了（插拔屏、改分辨率）；界面页要跟着重画。</summary>
        public event Action? DisplaysChanged;

        private IReadOnlyList<Displays.Info> _displays = Displays.Enum();
        private System.Threading.Timer? _displayWatch;

        public int ConfigCount { get; }
        public int DeviceCount { get; private set; }
        public IReadOnlyList<string> Devices { get; private set; } = [];
        public string TabletName { get; private set; } = "未检测到";

        /// <summary>数位板有效区域尺寸（毫米），图形化区域编辑器用它当画布。</summary>
        public Vector2 TabletSizeMm { get; private set; } = new(224, 148);
        public string WintabState { get; private set; } = "未检查";
        public string OutputState { get; private set; } = "未启动";

        /// <summary>显示器区域 / 数位板区域（原版 AbsoluteModeSettings 的精简版）。</summary>
        public MappingConfig Mapping { get; } = MappingConfig.Load();

        /// <summary>
        /// 触摸 / 环 管线：0x11 的环和 0x21/0xD2 的触点都从这里进来，
        /// 由 WintabOutputMode.ReportReceived 喂包（笔的报文也走同一个事件，但这里会忽略）。
        /// </summary>
        public TouchEngine Touch { get; } = new();

        public SlimHost()
        {
            Log.Output += OnLogMessage;

            var services = new DriverServiceCollection().BuildServiceProvider();
            _services = services;

            var configurations = services.GetRequiredService<IDeviceConfigurationProvider>();
            var parsers = services.GetRequiredService<IReportParserProvider>();
            var hub = services.GetRequiredService<ICompositeDeviceHub>();

            ConfigCount = configurations.TabletConfigurations.Count();
            _driver = new Driver(hub, parsers, configurations);

            DetectWintabShim();
        }

        public Task<bool> DetectAsync() => Task.Run(() =>
        {
            try
            {
                var detected = _driver.Detect();

                var endpoints = _driver.CompositeDeviceHub.GetDevices().ToArray();
                DeviceCount = endpoints.Length;
                Devices = endpoints.Select(Describe).ToArray();
                TabletName = _driver.Tablets.FirstOrDefault()?.Properties.Name ?? "未检测到";
                if (_driver.Tablets.FirstOrDefault()?.Properties.Specifications.Digitizer is { } digitizerSpec)
                    TabletSizeMm = new Vector2(digitizerSpec.Width, digitizerSpec.Height);

                Log.Write(nameof(SlimHost), $"检测结果：{(detected ? "找到数位板" : "没有匹配的设备")}", LogLevel.Info);
                StateChanged?.Invoke();
                return detected;
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                StateChanged?.Invoke();
                return false;
            }
        });

        /// <summary>
        /// 默认把映射区域里的屏幕像素发给 Wintab；现场要回退成设备单位时，
        /// 在 data 目录放一个 screencoords.txt 写 0（或 raw）。
        /// </summary>
        private static bool ScreenSpaceEnabled()
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OTDWintab",
                    "screencoords.txt");

                if (!File.Exists(path))
                    return true;

                var text = File.ReadAllText(path).Trim().ToLowerInvariant();
                return text is not ("0" or "off" or "raw" or "device");
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 把第一个检测到的数位板接到 Wintab 输出模式上。
        ///
        /// 除了绝对定位和笔压，还必须挂上按键管线（PenButtonPipeline）：
        /// 原版这一步由 Desktop 的 BindingHandler 承担，精简版把它内建化，
        /// 否则笔尖按下去只会更新压力，永远不会产生鼠标点击。
        /// </summary>
        /// <summary>笔上按键的映射（按键页里点着切换），和管线共用同一个实例。</summary>
        public ButtonsConfig Buttons { get; } = ButtonsConfig.Load();

        /// <param name="nativeOverride">测试用替身指针；正常运行时传 null。</param>
        public string StartOutput(IAbsolutePointer? nativeOverride = null)
        {
            try
            {
                // 坐标空间开关要在建共享内存之前定下来（shim 读的是它的最高位）
                OpenTabletDriver.Wintab.WintabPointer.ScreenSpace = ScreenSpaceEnabled();
                var tree = _driver.InputDevices.FirstOrDefault();
                if (tree == null)
                {
                    OutputState = "没有可用数位板，无法启动";
                    StateChanged?.Invoke();
                    return OutputState;
                }

                var digitizer = tree.Properties.Specifications.Digitizer;
                if (digitizer == null)
                {
                    OutputState = "该配置缺少数位板参数";
                    StateChanged?.Invoke();
                    return OutputState;
                }

                var native = nativeOverride ?? _pointer ?? (_pointer = new WindowsPointer());
                var screen = native is WindowsPointer windowPointer
                    ? windowPointer.ScreenSize
                    : new Vector2(1920, 1080);
                _outputMode?.Dispose();
                _buttons = null;

                var mode = new WintabOutputMode
                {
                    NativePointer = native
                };

                // 触摸/环：同一根读取线程上的旁路，不单独开 HID，也不会和笔抢接口
                mode.ReportReceived += report => Touch.Feed(report);
                Mapping.EnsureDefaults(digitizer.Width, digitizer.Height);

                // 上次退出时屏已拔掉、mapping.json 里留着比屏幕还大的区域：
                // 启动时先按当前拓扑修一次，别让旧值带着跑
                Mapping.RefitToDisplays();
                Mapping.Save();
                mode.Tablet = tree.CreateReference();
                // WintabOutputMode 的 Pointer 会把原生指针包成 WintabPointer，
                // 它同时实现 IPenActionHandler：按键走它才能同步到 Wintab 共享内存。
                if (mode.Pointer is IPenActionHandler penActionHandler)
                {
                    _buttons = new PenButtonPipeline(penActionHandler, Buttons);
                    mode.Elements = new List<IPositionedPipelineElement<IDeviceReport>> { _buttons };
                }

                tree.OutputMode = mode;
                _outputMode = mode;
                ApplyMapping();
                StartDisplayWatch();
                StartRebindWatch();

                OutputState = native is WindowsPointer pointer
                    ? $"运行中 · 屏幕 {pointer.ScreenSize.X:0}x{pointer.ScreenSize.Y:0} · 数位板 {Mapping.TabletWidth:0.#}x{Mapping.TabletHeight:0.#}mm"
                    : "运行中（测试替身指针）";
                Log.Write(nameof(SlimHost), "Wintab 输出已启动（含按键管线）", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                OutputState = "启动失败：" + ex.Message;
            }

            StateChanged?.Invoke();
            return OutputState;
        }

        /// <summary>
        /// 把 mapping.json 里的区域热更新到当前输出模式，界面上一改就生效，不用重启。
        /// </summary>
        public void ApplyMapping()
        {
            if (_outputMode == null)
                return;

            _outputMode.Input = new Area(
                (float)Mapping.TabletWidth, (float)Mapping.TabletHeight,
                new Vector2((float)Mapping.TabletX, (float)Mapping.TabletY),
                // 按键在右侧 = 把映射整体转 180°，和 Wacom 驱动里那个"左右手"选项等价
                Mapping.ButtonsOnRight ? 180f : 0f);
            _outputMode.Output = new Area(
                (float)Mapping.DisplayWidth, (float)Mapping.DisplayHeight,
                new Vector2((float)Mapping.DisplayX, (float)Mapping.DisplayY));
            _outputMode.AreaClipping = Mapping.EnableClipping;

            Log.Write(nameof(SlimHost),
                $"MAPPING display={Mapping.DisplayWidth:0}x{Mapping.DisplayHeight:0}@{Mapping.DisplayX:0},{Mapping.DisplayY:0} " +
                $"tablet={Mapping.TabletWidth:0.0}x{Mapping.TabletHeight:0.0}@{Mapping.TabletX:0.0},{Mapping.TabletY:0.0} " +
                $"buttonsOnRight={Mapping.ButtonsOnRight} lock={Mapping.LockAspectRatio} clip={Mapping.EnableClipping} " +
                $"device={Mapping.DisplayDevice}", LogLevel.Info);
        }
        public string ButtonState => _buttons == null
            ? "按键：未接线"
            : $"按键：按下 {_buttons.PressCount} / 抬起 {_buttons.ReleaseCount} · 报文 {_buttons.ReportCount} · 峰值压力 {_buttons.MaxPressure}";

        private System.Threading.Timer? _rebindWatch;
        private long _lastRebindMs;
        private readonly object _rebindSync = new();
        private volatile bool _rebinding;

        /// <summary>
        /// 拔插数位板后程序不会自己回来：设备被拔掉时 Driver 把设备树丢掉，
        /// 但没有人再调一次 Detect，插回来就一直是"没有可用数位板"，只能重启。
        /// 这里每 2 秒看一次：没有设备就重试绑定（3 秒一次），设备回来了但输出没接上就接上。
        /// </summary>
        public void StartRebindWatch()
        {
            _rebindWatch ??= new System.Threading.Timer(_ => Rebind(false), null, 1000, 2000);
        }

        /// <summary>
        /// 重新枚举并重新接上输出。force=true 时忽略节流，界面上的"重新检测"用它。
        /// </summary>
        public string Rebind(bool force)
        {
            if (_rebinding)
                return OutputState;

            var now = Environment.TickCount64;
            if (!force && now - _lastRebindMs < 3000)
                return OutputState;

            if (!System.Threading.Monitor.TryEnter(_rebindSync))
                return OutputState;

            try
            {
                if (!force && _driver.InputDevices.Length > 0 && _outputMode != null)
                    return OutputState;

                _rebinding = true;
                _lastRebindMs = now;
                Log.Write(nameof(SlimHost), "重新绑定数位板…", LogLevel.Info);

                try
                {
                    _outputMode?.Dispose();
                }
                catch
                {
                    // 旧模式可能已经随设备一起被释放了
                }

                _outputMode = null;
                _buttons = null;

                var detected = _driver.Detect();
                var endpoints = _driver.CompositeDeviceHub.GetDevices().ToArray();
                DeviceCount = endpoints.Length;
                Devices = endpoints.Select(Describe).ToArray();
                TabletName = _driver.Tablets.FirstOrDefault()?.Properties.Name ?? "未检测到";

                if (!detected)
                {
                    OutputState = "没有可用数位板，等它插回来";
                    Log.Write(nameof(SlimHost), OutputState, LogLevel.Warning);
                    StateChanged?.Invoke();
                    return OutputState;
                }

                var state = StartOutput();
                StateChanged?.Invoke();
                return state;
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                OutputState = $"重新绑定失败：{ex.Message}";
                StateChanged?.Invoke();
                return OutputState;
            }
            finally
            {
                _rebinding = false;
                System.Threading.Monitor.Exit(_rebindSync);
            }
        }

        /// <summary>
        /// 显示器热插拔不能只刷新界面文字，三样东西都得跟着重算：
        ///   · WindowsPointer 缓存的虚拟桌面尺寸/原点（注入坐标按它归一化，
        ///     拔掉一块屏后还用旧值，笔就只覆盖屏幕的一部分）
        ///   · mapping.json 的显示器区域（那块屏没了或变小了就重新铺）
        ///   · 输出管线的 Area（ApplyMapping 重新下发）
        /// 每秒比对一次拓扑，变了就按新拓扑重算，不用重启程序。
        /// </summary>
        private void StartDisplayWatch()
        {
            _displayWatch ??= new System.Threading.Timer(
                _ => WatchDisplays(), null, 1000, 1000);
        }

        public void WatchDisplays()
        {
            try
            {
                var now = Displays.Enum();
                if (SameTopology(now, _displays))
                    return;

                _displays = now;
                Log.Write(nameof(SlimHost), "显示器变化：" + DescribeTopology(now), LogLevel.Info);

                (_pointer as WindowsPointer)?.RefreshScreen();

                if (Mapping.RefitToDisplays())
                {
                    Mapping.Save();
                    ApplyMapping();
                }

                DisplaysChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private static bool SameTopology(IReadOnlyList<Displays.Info> a, IReadOnlyList<Displays.Info> b)
        {
            if (a.Count != b.Count)
                return false;

            for (var i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i].Device, b[i].Device, StringComparison.OrdinalIgnoreCase)
                    || a[i].X != b[i].X || a[i].Y != b[i].Y
                    || a[i].Width != b[i].Width || a[i].Height != b[i].Height
                    || a[i].Primary != b[i].Primary)
                {
                    return false;
                }
            }

            return true;
        }

        private static string DescribeTopology(IReadOnlyList<Displays.Info> list) =>
            string.Join(" | ", list.Select(d =>
                $"{d.Device.Replace(@"\\.\", "")} {d.Width}x{d.Height}@({d.X},{d.Y}){(d.Primary ? " 主屏" : "")}"));

        /// <summary>
        /// 端到端验证点击链路：用替身指针跑完整管线，喂真实语义的报文 ——
        /// 笔尖靠压力触发（不是 PenButtons 位），侧键才是 PenButtons。
        /// 不依赖真实硬件，也不会点桌面。
        /// </summary>
        public string TestButtonPath()
        {
            DetectAsync().GetAwaiter().GetResult();

            var probe = new ProbePointer();
            var state = StartOutput(probe);

            var tree = _driver.InputDevices.FirstOrDefault();
            var mode = tree?.OutputMode;
            if (mode == null)
                return $"启动失败：{state}";

            // 1. 笔尖触底靠压力：PenButtons 全 false，压力 4000
            mode.Read(new FakeTabletReport { Position = new Vector2(20000, 14000), Pressure = 4000, PenButtons = [false, false] });
            // 2. 笔尖抬起：压力归零
            mode.Read(new FakeTabletReport { Position = new Vector2(20000, 14000), Pressure = 0, PenButtons = [false, false] });
            // 3. 第一侧键按下/抬起：这才是 PenButtons[0]
            mode.Read(new FakeTabletReport { Position = new Vector2(20000, 14000), Pressure = 0, PenButtons = [true, false] });
            mode.Read(new FakeTabletReport { Position = new Vector2(20000, 14000), Pressure = 0, PenButtons = [false, false] });

            var leftOk = probe.LeftDown == 1 && probe.LeftUp == 1;
            var rightOk = probe.RightDown == 1 && probe.RightUp == 1;
            var pass = leftOk && rightOk;

            return string.Join(Environment.NewLine,
                $"启动状态    : {state}",
                $"按键管线    : {ButtonState}",
                $"笔尖(压力)  : 左键按下 {probe.LeftDown} / 抬起 {probe.LeftUp}  {(leftOk ? "✓" : "✗")}",
                $"侧键1       : 右键按下 {probe.RightDown} / 抬起 {probe.RightUp}  {(rightOk ? "✓" : "✗")}",
                $"指针移动    : {probe.Moves} 次 / 刷新 {probe.Flushes} 次",
                $"结论        : {(pass ? "点击链路通（笔尖走压力、侧键走按键位）" : "点击链路断开")}");
        }


        /// <summary>
        /// 命令行验收：打印当前显示器拓扑 + 映射区域，再拿"双屏铺满、然后拔掉一块"
        /// 这组数值跑一遍 RefitToDisplays，看区域会不会被拉回单屏范围内。
        /// </summary>
        public static string RunDisplayCheck()
        {
            var list = Displays.Enum();
            var lines = new List<string> { $"显示器数量  : {list.Count}" };

            foreach (var d in list)
            {
                lines.Add($"  {d.Device.Replace(@"\\.\", "")}  {d.Width}x{d.Height} at ({d.X},{d.Y}){(d.Primary ? "  主屏" : "")}");
            }

            var pointer = new WindowsPointer();
            lines.Add($"虚拟桌面    : {pointer.ScreenSize.X:0}x{pointer.ScreenSize.Y:0}");

            var cfg = MappingConfig.Load();
            lines.Add($"当前映射    : display={cfg.DisplayWidth:0}x{cfg.DisplayHeight:0}@{cfg.DisplayX:0},{cfg.DisplayY:0} device={cfg.DisplayDevice}");
            lines.Add($"区域在屏内  : {(Displays.Find(cfg.DisplayDevice) is { } target && IsInsideFor(cfg, target) ? "是" : "否/查不到该屏")}");

            // 模拟：双屏时区域铺满 7680x2160，然后是拔掉右屏（只剩 3840x2160）
            var sim = new MappingConfig
            {
                DisplayDevice = cfg.DisplayDevice,
                DisplayWidth = 7680,
                DisplayHeight = 2160,
                DisplayX = 3840,
                DisplayY = 1080,
                TabletWidth = cfg.TabletWidth,
                TabletHeight = cfg.TabletHeight,
                TabletX = cfg.TabletX,
                TabletY = cfg.TabletY,
                DigitizerWidth = cfg.DigitizerWidth,
                DigitizerHeight = cfg.DigitizerHeight,
                LockAspectRatio = cfg.LockAspectRatio
            };

            var changed = sim.RefitToDisplays();
            lines.Add($"模拟拔屏后  : changed={changed} display={sim.DisplayWidth:0}x{sim.DisplayHeight:0}@{sim.DisplayX:0},{sim.DisplayY:0} tablet={sim.TabletWidth:0.0}x{sim.TabletHeight:0.0}");

            return string.Join(Environment.NewLine, lines);
        }

        private static bool IsInsideFor(MappingConfig cfg, Displays.Info display) =>
            cfg.DisplayX - cfg.DisplayWidth / 2.0 >= display.X - 1
            && cfg.DisplayY - cfg.DisplayHeight / 2.0 >= display.Y - 1
            && cfg.DisplayX + cfg.DisplayWidth / 2.0 <= display.X + display.Width + 1
            && cfg.DisplayY + cfg.DisplayHeight / 2.0 <= display.Y + display.Height + 1;

        private void DetectWintabShim()
        {
            var candidates = new[]
            {
                Path.Join(AppContext.BaseDirectory, "WintabNative", "Wintab32.dll"),
                Path.Join(AppContext.BaseDirectory, "Wintab32.dll")
            };

            var found = candidates.FirstOrDefault(File.Exists);
            WintabState = found == null
                ? "Wintab32.dll 缺失，内置输出不可用"
                : $"已内置 Wintab32.dll（{new FileInfo(found).Length / 1024} KB）";
        }

        private static string Describe(OpenTabletDriver.Plugin.Devices.IDeviceEndpoint endpoint)
        {
            try
            {
                return $"{endpoint.VendorID:X4}:{endpoint.ProductID:X4}  {endpoint.FriendlyName ?? endpoint.DevicePath}";
            }
            catch
            {
                return $"{endpoint.VendorID:X4}:{endpoint.ProductID:X4}";
            }
        }

        private void OnLogMessage(object? sender, LogMessage message)
        {
            LogReceived?.Invoke($"[{message.Level}] {message.Group}: {message.Message}");

            // 设备/解析相关的错误只在这里出现，落盘一份，出问题才能查
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OTDWintab");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, $"app-{Environment.ProcessId}.log"),
                    $"{DateTime.Now:HH:mm:ss} [{message.Level}] {message.Group}: {message.Message}{Environment.NewLine}");
            }
            catch
            {
                // 写日志失败不影响运行
            }
        }

        /// <summary>
        /// 命令行自检：不弹窗，直接跑一遍底层并把结果拼成文本，用于验证环境。
        /// </summary>
        public static string RunSelfTest()
        {
            using var host = new SlimHost();
            host.DetectAsync().GetAwaiter().GetResult();

            var lines = new List<string>
            {
                $"配置条目      : {host.ConfigCount}",
                $"HID 设备      : {host.DeviceCount}",
                $"数位板        : {host.TabletName}",
                $"Wintab        : {host.WintabState}",
                $"输出          : {host.OutputState}",
                "---- 设备列表 ----"
            };

            var shown = host.Devices.Take(15).ToArray();
            lines.AddRange(shown);
            if (host.Devices.Count > shown.Length)
                lines.Add($"… 其余 {host.Devices.Count - shown.Length} 个省略");

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// 命令行验证点击链路，结果写入 buttontest.txt。
        /// </summary>
        public static string RunButtonTest()
        {
            using var host = new SlimHost();
            return host.TestButtonPath();
        }

        /// <summary>
        /// 验证 feel.json 的读写：读当前值 → 改成 0.12 → 存 → 再读 → 还原。
        /// 裁剪发布下如果序列化被裁坏，这里会直接露出"写进去读不出来"。
        /// </summary>
        public static string RunFeelTest()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OTDWintab",
                "feel.json");

            var original = File.Exists(path) ? File.ReadAllText(path) : null;
            var before = FeelConfig.Load();

            before.DeadZone = 0.12;
            before.Save();

            var after = FeelConfig.Load();

            if (original != null)
                File.WriteAllText(path, original);
            else
                before.Save();

            var roundTripOk = Math.Abs(after.DeadZone - 0.12) < 1e-9;

            return string.Join(Environment.NewLine,
                $"配置文件    : {path}",
                $"读取(前)    : DeadZone={before.DeadZone:0.000} Elasticity={before.Elasticity:0.0} StrokeFilter={before.StrokeFilter} MaxPressure={before.MaxPressure:0.00} Mode={before.InputMode}",
                $"写入后读取  : DeadZone={after.DeadZone:0.000}",
                $"结论        : {(roundTripOk ? "feel.json 读写正常" : "feel.json 读写异常（可能是裁剪导致序列化失效）")}");
        }

        /// <summary>
        /// 命令行验收：把输出接起来跑 N 秒，只统计触摸 / 环报文，指针用替身不点桌面。
        /// 用来回答"0x11 环到底有没有进来、0x21/0xD2 到底来没来"，不需要开界面。
        /// </summary>
        public static string RunTouchDump(int seconds)
        {
            using var host = new SlimHost();
            host.DetectAsync().GetAwaiter().GetResult();

            var state = host.StartOutput(new ProbePointer());
            if (!state.StartsWith("运行中", StringComparison.Ordinal))
                return $"输出没起来：{state}";

            var deadline = Environment.TickCount64 + seconds * 1000L;
            while (Environment.TickCount64 < deadline)
                System.Threading.Thread.Sleep(200);

            return string.Join(Environment.NewLine,
                $"时长        : {seconds}s",
                $"数位板      : {host.TabletName}",
                $"输出        : {state}",
                $"触摸统计    : {host.Touch.Describe()}",
                $"最近动作    : {host.Touch.LastAction}");
        }

        /// <summary>替身指针：只记账，不碰真实鼠标，用来验证管线是否把点击送到了指针。</summary>
        private sealed class ProbePointer : IAbsolutePointer, IMouseButtonHandler, IPressureHandler, ISynchronousPointer
        {
            public int Moves;
            public int Flushes;
            public int LeftDown;
            public int LeftUp;
            public int RightDown;
            public int RightUp;
            public int MiddleDown;
            public int MiddleUp;

            public void SetPosition(Vector2 pos) => Moves++;
            public void SetPressure(float percentage) { }

            public void MouseDown(MouseButton button)
            {
                switch (button)
                {
                    case MouseButton.Left: LeftDown++; break;
                    case MouseButton.Right: RightDown++; break;
                    case MouseButton.Middle: MiddleDown++; break;
                }
            }

            public void MouseUp(MouseButton button)
            {
                switch (button)
                {
                    case MouseButton.Left: LeftUp++; break;
                    case MouseButton.Right: RightUp++; break;
                    case MouseButton.Middle: MiddleUp++; break;
                }
            }

            public void Reset() { }
            public void Flush() => Flushes++;
        }

        private sealed class FakeTabletReport : ITabletReport
        {
            public byte[] Raw { get; set; } = new byte[8];
            public Vector2 Position { get; set; }
            public uint Pressure { get; set; }
            public bool[] PenButtons { get; set; } = [];
        }

        public void Dispose()
        {
            Log.Output -= OnLogMessage;
            _displayWatch?.Dispose();
            _displayWatch = null;
            _rebindWatch?.Dispose();
            _rebindWatch = null;
            _outputMode?.Dispose();
            _driver.Dispose();
            _services.Dispose();
        }
    }
}
