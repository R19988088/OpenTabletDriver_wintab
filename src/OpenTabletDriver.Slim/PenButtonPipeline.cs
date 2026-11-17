using System;
using System.Threading.Tasks;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Logging;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Platform.Pointer;
using OpenTabletDriver.Plugin.Tablet;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 精简版按键管线，替代 Desktop 里的 BindingHandler。
    ///
    /// 关键语义（照原版 BindingHandler 走）：
    ///   - 笔尖/橡皮擦**不是** PenButtons 里的位，而是由压力阈值驱动。
    ///     Wacom IntuosV2 的 PenButtons 只有 bit1/bit2 两个侧键，bit0 笔尖并不在里面。
    ///   - PenButtons[0] → 下侧键、[1] → 上侧键。
    ///   - 笔离开感应区（OutOfRangeReport）时释放所有仍按住的键。
    ///
    /// 每个热点发什么由 ButtonsConfig 决定：内置动作（左/右/中键/双击/无），
    /// 或者用户在按键页里按 ＋ 录下来的自定义键盘组合 / 鼠标键。
    /// </summary>
    public sealed class PenButtonPipeline : IPositionedPipelineElement<IDeviceReport>
    {
        private const int TrackedBarrelButtons = 3;

        private readonly IPenActionHandler handler;
        private readonly ButtonsConfig config;
        private readonly bool[] previous = new bool[TrackedBarrelButtons];
        private readonly PenBinding[] barrelBindings = new PenBinding[TrackedBarrelButtons];
        private readonly bool[] barrelDouble = new bool[TrackedBarrelButtons];

        private bool eraser;
        private bool tipDown;
        private bool tipDouble;
        private PenBinding tipBinding = PenBinding.None;

        public PenButtonPipeline(IPenActionHandler handler, ButtonsConfig? config = null)
        {
            this.handler = handler;
            this.config = config ?? new ButtonsConfig();
        }

        public PipelinePosition Position => PipelinePosition.PostTransform;

        public event Action<IDeviceReport?>? Emit;

        public long PressCount { get; private set; }
        public long ReleaseCount { get; private set; }
        public long ReportCount { get; private set; }
        public uint MaxPressure { get; private set; }

        public void Consume(IDeviceReport? report)
        {
            if (report is IEraserReport eraserReport)
                eraser = eraserReport.Eraser;

            if (report is ITabletReport tabletReport)
            {
                ReportCount++;
                PenTrace(tabletReport);
                if (tabletReport.Pressure > MaxPressure)
                    MaxPressure = tabletReport.Pressure;

                HandleTip(tabletReport.Pressure);
                HandleBarrelButtons(tabletReport.PenButtons);
            }
            else if (report is OutOfRangeReport)
            {
                ReleaseAll();
            }

            Emit?.Invoke(report);
        }

        private static long _traceLines;

        /// <summary>每条笔报告都落盘一行，用来判断"报告到底还进不进得来"。</summary>
        private static void PenTrace(ITabletReport report)
        {
            if (System.Threading.Interlocked.Increment(ref _traceLines) > 6000)
                return;

            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OTDWintab");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, $"pen-{Environment.ProcessId}.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} pos={report.Position.X:0},{report.Position.Y:0} p={report.Pressure}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        /// <summary>笔尖由压力驱动：压力从 0 抬起 = 按下，回到 0 = 抬起。</summary>
        private void HandleTip(uint pressure)
        {
            if (pressure > 0)
            {
                if (tipDown)
                    return;

                tipDown = true;
                PressCount++;
                var function = eraser ? PenFunction.Eraser : config.TipFunction;
                var custom = eraser ? string.Empty : config.TipCustom;
                (tipBinding, tipDouble) = Resolve(custom, function);
                Press(tipBinding, tipDouble, "笔尖", function, custom);
            }
            else if (tipDown)
            {
                tipDown = false;
                ReleaseCount++;
                if (!tipDouble)
                    Release(tipBinding);
            }
        }

        private void HandleBarrelButtons(bool[]? states)
        {
            for (var i = 0; i < TrackedBarrelButtons; i++)
            {
                var state = states != null && i < states.Length && states[i];
                if (state == previous[i])
                    continue;

                previous[i] = state;

                var function = i switch
                {
                    0 => config.Barrel1Function,
                    1 => config.Barrel2Function,
                    _ => PenFunction.None
                };
                var custom = i switch
                {
                    0 => config.Barrel1Custom,
                    1 => config.Barrel2Custom,
                    _ => string.Empty
                };

                if (state)
                {
                    PressCount++;
                    (barrelBindings[i], barrelDouble[i]) = Resolve(custom, function);
                    Press(barrelBindings[i], barrelDouble[i], i == 0 ? "下侧键" : "上侧键", function, custom);
                }
                else
                {
                    ReleaseCount++;
                    if (!barrelDouble[i])
                        Release(barrelBindings[i]);
                }
            }
        }

        private static (PenBinding Binding, bool DoubleClick) Resolve(string custom, PenFunction function)
        {
            if (!string.IsNullOrWhiteSpace(custom))
                return (PenBinding.FromText(custom), false);

            if (function == PenFunction.DoubleClick)
                return (PenBinding.None, true);

            return (PenBinding.FromFunction(function), false);
        }

        private void Press(PenBinding binding, bool doubleClick, string source, PenFunction function, string custom)
        {
            if (doubleClick)
            {
                _ = DoubleClick();
                Log.Write("按键", $"{source} → 双击", LogLevel.Info);
                return;
            }

            if (!binding.IsValid)
            {
                Log.Write("按键", $"{source} → 无映射", LogLevel.Info);
                return;
            }

            if (binding.IsMouse)
            {
                // 修饰键必须先按住，再点鼠标，Alt+右键这类组合才认
                if (binding.Modifiers.Length > 0)
                    WindowsKeyboard.PressModifiers(binding.Modifiers);
                handler.Activate(binding.MouseAction);
            }
            else
            {
                WindowsKeyboard.Press(binding.Modifiers, binding.VirtualKey);
            }

            var text = string.IsNullOrWhiteSpace(custom) ? ButtonsConfig.Text(function) : custom;
            Log.Write("按键", $"{source} → {text}", LogLevel.Info);
        }

        private void Release(PenBinding binding)
        {
            if (!binding.IsValid)
                return;

            if (binding.IsMouse)
            {
                handler.Deactivate(binding.MouseAction);
                if (binding.Modifiers.Length > 0)
                    WindowsKeyboard.ReleaseModifiers(binding.Modifiers);
            }
            else
            {
                WindowsKeyboard.Release(binding.Modifiers, binding.VirtualKey);
            }
        }

        /// <summary>双击 = 快速两下左键按下/抬起。</summary>
        private async Task DoubleClick()
        {
            handler.Activate(PenAction.Tip);
            await Task.Delay(70);
            handler.Deactivate(PenAction.Tip);
            await Task.Delay(45);
            handler.Activate(PenAction.Tip);
            await Task.Delay(70);
            handler.Deactivate(PenAction.Tip);
        }

        private void ReleaseAll()
        {
            if (tipDown)
            {
                tipDown = false;
                ReleaseCount++;
                if (!tipDouble)
                    Release(tipBinding);
            }

            for (var i = 0; i < TrackedBarrelButtons; i++)
            {
                if (!previous[i])
                    continue;

                previous[i] = false;
                ReleaseCount++;
                if (!barrelDouble[i])
                    Release(barrelBindings[i]);
            }
        }
    }
}
