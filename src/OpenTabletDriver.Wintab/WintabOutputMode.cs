using System;
using System.Numerics;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.DependencyInjection;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Platform.Pointer;
using OpenTabletDriver.Plugin.Tablet;

namespace OpenTabletDriver.Wintab
{
    /// <summary>
    /// Absolute mode that feeds a Wintab32 shim while delegating cursor and click
    /// handling to OTD's own platform pointer.
    /// </summary>
    [PluginName("Wintab (No Driver)")]
    [SupportedPlatform(PluginPlatform.Windows)]
    public sealed class WintabOutputMode : AbsoluteOutputMode
    {
        private IAbsolutePointer? nativePointer;
        private WintabPointer? pointer;

        [Resolved]
        public IAbsolutePointer? NativePointer
        {
            set
            {
                nativePointer = value;
                pointer = null;
            }
        }

        public override IAbsolutePointer? Pointer
        {
            get => ResolvedPointer();
            set { }
        }

        private WintabPointer? ResolvedPointer()
        {
            if (nativePointer == null)
                return null;
            return pointer ??= new WintabPointer(nativePointer);
        }

        public override TabletReference? Tablet
        {
            set
            {
                base.Tablet = value;
                ApplyTablet(value);
            }
            get
            {
                ApplyTablet(base.Tablet);
                SyncDisplay();
                return base.Tablet;
            }
        }

        /// <summary>
        /// Every report the OTD device tree hands to this output mode, before it is interpreted.
        /// The Slim host subscribes to translate aux (0x11) and touch (0x21/0xD2) reports into
        /// ring scrolling and gestures; the pen path itself does not depend on this event.
        /// </summary>
        public event Action<IDeviceReport>? ReportReceived;

        public override void Read(IDeviceReport deviceReport)
        {
            SyncDisplay();
            StatReport(deviceReport);

            // 触摸/环的翻译绝不能影响笔：哪怕订阅方抛异常也直接吞掉，笔照常走。
            try
            {
                ReportReceived?.Invoke(deviceReport);
            }
            catch
            {
                // 手势侧出错不影响输出
            }

            base.Read(deviceReport);
        }

        // Counters behind the once-per-second STAT line. Cheap atomics-free fields: Read runs
        // on the output-mode thread only, so plain updates are enough.
        private static long _reports;
        private static long _pressured;
        private static uint _maxPressure;
        private static uint _lastPressure;
        private static string _lastKind = "-";
        private static long _statTick;
        private static bool _started;

        private void StatReport(IDeviceReport report)
        {
            _reports++;

            if (!_started)
            {
                _started = true;
                _statTick = Environment.TickCount64;
                WintabDiag.Write(
                    $"START pid={Environment.ProcessId} " +
                    $"bits={(Environment.Is64BitProcess ? 64 : 32)} " +
                    $"plugin={typeof(WintabOutputMode).Assembly.GetName().Version} " +
                    $"wintabMax={WintabPressure.Max}");
            }

            if (report is null)
            {
                _lastKind = "null";
                return;
            }

            _lastKind = report.GetType().Name;

            if (report is ITabletReport tabletReport)
            {
                _lastPressure = tabletReport.Pressure;
                if (tabletReport.Pressure > 0)
                {
                    _pressured++;
                    if (tabletReport.Pressure > _maxPressure)
                        _maxPressure = tabletReport.Pressure;
                }
            }

            var now = Environment.TickCount64;
            if (now - _statTick < 1000)
                return;

            _statTick = now;
            WintabDiag.Write(
                $"STAT reports={_reports} pressured={_pressured} maxP={_maxPressure} " +
                $"lastP={_lastPressure} lastKind={_lastKind} " +
                $"pos={WintabPointer.SetPositionCalls} pres={WintabPointer.SetPressureCalls} " +
                $"flush={WintabPointer.FlushCalls} pub={WintabPointer.PublishCalls}");
        }

        private void SyncDisplay()
        {
            if (pointer != null && Output is { } area)
                pointer.ConfigureDisplay(new Vector2(area.Width, area.Height));
        }

        /// <summary>
        /// A Wintab driver reports the digitizer's own units; it is the application's job to
        /// map them onto the screen through the context's in/out extents. Reporting screen
        /// pixels instead means the application's own tablet→screen measurement is computed
        /// from two already-transformed coordinate spaces, and every stroke lands shifted and
        /// scaled. Capture the untransformed position here, before the base class replaces it.
        /// </summary>
        protected override IAbsolutePositionReport? Transform(IAbsolutePositionReport report)
        {
            var rawPosition = report.Position;
            var transformed = base.Transform(report);
            if (transformed != null)
                ResolvedPointer()?.SetTabletPosition(rawPosition);
            return transformed;
        }

        private void ApplyTablet(TabletReference? tablet)
        {
            if (tablet?.Properties.Specifications is not { } spec)
                return;
            if (spec.Digitizer is not { } digitizer || spec.Pen is not { } pen)
                return;

            // One line per distinct configuration: the tablet the daemon resolved and the
            // numbers every later pressure value is scaled against.
            var summary = $"TABLET name={tablet.Properties.Name} " +
                $"digitizer={digitizer.MaxX}x{digitizer.MaxY} " +
                $"penMax={pen.MaxPressure} wintabMax={WintabPressure.Max}";
            if (summary != _tabletLogged)
            {
                _tabletLogged = summary;
                WintabDiag.Write(summary);
            }

            // Wintab pressure is reported in the 0..32767 range that desktop
            // applications expect, independent of the tablet's own resolution.
            ResolvedPointer()?.Configure((int)digitizer.MaxX, (int)digitizer.MaxY, WintabPressure.Max);
        }

        private static string? _tabletLogged;
    }
}
