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

        public override void Read(IDeviceReport deviceReport)
        {
            SyncDisplay();
            base.Read(deviceReport);
        }

        private int _n;
        private readonly System.Collections.Generic.Dictionary<string,int> _kinds = new();

        private void DiagReport(IDeviceReport r)
        {
            var name = r?.GetType().Name ?? "null";
            string extra = "";
            if (r is ITabletReport tr) extra = $" P={tr.Pressure}";
            if (r is IProximityReport pr) extra += $" prox={pr.NearProximity}";
            _kinds.TryGetValue(name + extra.Split(' ')[0], out var c);
            _kinds[name + extra.Split(' ')[0]] = c + 1;
            if (++_n % 50 == 0)
            {
                Log.Write("Wintab", $"COUNT read={_n} pos={WintabPointer.SetPositionCalls} pres={WintabPointer.SetPressureCalls} flush={WintabPointer.FlushCalls} pub={WintabPointer.PublishCalls} kinds=[{string.Join(",", _kinds)}]");
                var sb = new System.Text.StringBuilder("DIAG ");
                foreach (var kv in _kinds) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
                Log.Write("Wintab", sb.ToString());
            }
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
            // Wintab pressure is reported in the 0..32767 range that desktop
            // applications expect, independent of the tablet's own resolution.
            ResolvedPointer()?.Configure((int)digitizer.MaxX, (int)digitizer.MaxY, WintabPressure.Max);
        }
    }
}
