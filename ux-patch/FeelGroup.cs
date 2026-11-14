using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Eto.Drawing;
using Eto.Forms;
using OpenTabletDriver.UX.Controls.Generic;

namespace OpenTabletDriver.UX.Controls
{
    /// <summary>
    /// Feel tuning: dead zone, elasticity curve, stroke stabilisation and pressure
    /// calibration. Values are shared with the Wintab shim through
    /// %LOCALAPPDATA%\OTDWintab\feel.json, the same file the standalone panel used.
    /// </summary>
    public class FeelGroup : Panel
    {
        private readonly FeelConfig config = FeelConfig.Load();

        private readonly Label tabletLabel = new();
        private readonly Label peakLabel = new();
        private readonly Label modeHint = new()
        {
            Text = "当前模式：Windows Ink —— 手感处理已停用，死区、弹性、压力放大都不生效，笔只输出原始压力。",
            Visible = false
        };
        private readonly FloatSlider deadZone = new();
        private readonly FloatSlider elasticity = new();
        private readonly FloatSlider strokeFilter = new();
        private readonly CalibrationPad pad = new();

        private bool loading;

        public FeelGroup()
        {
            deadZone.Minimum = 0;
            deadZone.Maximum = 30;
            deadZone.StepSize = 1;

            elasticity.Minimum = 1;
            elasticity.Maximum = 100;
            elasticity.StepSize = 1;

            strokeFilter.Minimum = 1;
            strokeFilter.Maximum = 16;
            strokeFilter.StepSize = 1;

            deadZone.ValueChanged += (_, _) =>
            {
                if (loading) return;
                config.DeadZone = deadZone.Value / 100.0;
                config.Save();
            };
            elasticity.ValueChanged += (_, _) =>
            {
                if (loading) return;
                config.Elasticity = elasticity.Value / 10.0;
                config.Save();
            };
            strokeFilter.ValueChanged += (_, _) =>
            {
                if (loading) return;
                config.StrokeFilter = (int)strokeFilter.Value;
                config.Save();
            };

            pad.PeakRecorded += value =>
            {
                if (loading) return;
                config.MaxPressure = value;
                config.Save();
                UpdatePeak();
            };

            BuildUi();
            LoadValues();
        }

        private void BuildUi()
        {
            this.Content = new Scrollable
            {
                Border = BorderType.None,
                Content = new StackLayout
                {
                    Padding = new Padding(10),
                    Spacing = 5,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Items =
                    {
                        modeHint,
                        new Group
                        {
                            Text = "数位板",
                            Orientation = Orientation.Horizontal,
                            ExpandContent = false,
                            Content = tabletLabel
                        },
                        new UnitGroup
                        {
                            Text = "压力死区",
                            Orientation = Orientation.Horizontal,
                            ToolTip = "低于这个压力的输入被当作 0，用于消除落笔前的悬停压力。",
                            Content = deadZone,
                            Unit = "%"
                        },
                        new UnitGroup
                        {
                            Text = "弹性",
                            Orientation = Orientation.Horizontal,
                            ToolTip = "压力曲线的回弹强度，值越大曲线越软。",
                            Content = elasticity
                        },
                        new UnitGroup
                        {
                            Text = "笔触稳定",
                            Orientation = Orientation.Horizontal,
                            ToolTip = "对输入压力做平滑的强度，值越大越稳、延迟越明显。",
                            Content = strokeFilter
                        },
                        new Group
                        {
                            Text = "压力校准",
                            Content = new StackLayout
                            {
                                Spacing = 5,
                                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                                Items =
                                {
                                    new StackLayout
                                    {
                                        Orientation = Orientation.Horizontal,
                                        Items =
                                        {
                                            new StackLayoutItem(new Panel(), true),
                                            peakLabel
                                        }
                                    },
                                    new StackLayoutItem(pad, true),
                                    new Label
                                    {
                                        Text = "在校准区用最大力气画一笔，松手后「保留峰值」就是能达到的最大压力，之后按这个值等比放大到 100%。"
                                    }
                                }
                            }
                        }
                    }
                }
            };
        }

        private void LoadValues()
        {
            loading = true;
            deadZone.Value = (float)Math.Round(config.DeadZone * 100);
            elasticity.Value = (float)Math.Round(config.Elasticity * 10);
            strokeFilter.Value = config.StrokeFilter;
            loading = false;
            UpdatePeak();
        }

        private void UpdatePeak()
        {
            peakLabel.Text = $"保留峰值 {config.MaxPressure * 100:0.0}%";
        }

        /// <summary>Shows the tablet the daemon currently has open.</summary>
        public string TabletName
        {
            set => tabletLabel.Text = string.IsNullOrEmpty(value) ? "（未连接）" : value;
        }

        /// <summary>
        /// The feel pipeline lives inside the Wintab shim, so it only runs when the tablet
        /// is driven through WinTab. Grey everything out otherwise, and stop the calibration
        /// pad from polling, rather than let the sliders write values nothing reads.
        /// </summary>
        public void SetModeEnabled(bool enabled)
        {
            modeHint.Visible = !enabled;
            deadZone.Enabled = enabled;
            elasticity.Enabled = enabled;
            strokeFilter.Enabled = enabled;
            pad.Enabled = enabled;
        }

        /// <summary>
        /// Draw here with the pen; the pressure 100 ms before release is kept as the
        /// peak the shim scales up to full range. Repaints ride OTD's own composition
        /// scheduler instead of a private timer, and a single pen is reused per frame,
        /// so a long stroke cannot leak GDI objects.
        /// </summary>
        public sealed class CalibrationPad : ScheduledDrawable
        {
            private const double PressureMax = 32767.0;
            private const int Capacity = 1024;

            private readonly TabletShared shared = new();
            private readonly List<(PointF From, PointF To, float Pressure)> segments = new();
            private readonly List<(long Ms, float Pressure)> samples = new();
            private readonly Pen strokePen = new(SystemColors.ControlText, 1.0f);

            private PointF? last;
            private float livePressure;
            private int lastSerial;

            public event Action<double>? PeakRecorded;

            public CalibrationPad()
            {
                this.Size = new Size(-1, 220);

                this.MouseDown += (_, e) =>
                {
                    segments.Clear();
                    samples.Clear();
                    last = e.Location;
                    Invalidate();
                };
                this.MouseMove += (_, e) =>
                {
                    if (last == null || e.Buttons != MouseButtons.Primary)
                        return;
                    segments.Add((last.Value, e.Location, livePressure));
                    last = e.Location;
                };
                this.MouseUp += (_, _) =>
                {
                    last = null;
                    float peak = 0;
                    long now = Environment.TickCount64;
                    // Lifting the pen always ramps pressure down, so the samples right
                    // before release are useless; take the peak from 100 ms earlier.
                    foreach (var sample in samples)
                        if (sample.Ms <= now - 100 && sample.Pressure > peak)
                            peak = sample.Pressure;
                    if (peak <= 0)
                        foreach (var sample in samples)
                            peak = Math.Max(peak, sample.Pressure);
                    if (peak > 0)
                        PeakRecorded?.Invoke(Math.Clamp(Math.Round((double)peak, 2), 0.01, 1.0));
                };

                // Registers with the composition scheduler in the setter.
                this.Enabled = true;
            }

            protected override void OnNextFrame(PaintEventArgs e)
            {
                PollSample();

                var graphics = e.Graphics;
                var bounds = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);

                graphics.FillRectangle(SystemColors.ControlBackground, bounds);
                graphics.DrawRectangle(SystemColors.Control, bounds);

                foreach (var segment in segments)
                {
                    strokePen.Thickness = 1.0f + segment.Pressure * 6.0f;
                    graphics.DrawLine(strokePen, segment.From, segment.To);
                }
            }

            private void PollSample()
            {
                if (!shared.ReadLatest(out var pressure, out var buttons, out var serial))
                    return;
                if (serial == lastSerial)
                    return;
                lastSerial = serial;

                livePressure = (float)Math.Clamp(pressure / PressureMax, 0.0, 1.0);
                if (buttons != 0 || last != null)
                    samples.Add((Environment.TickCount64, livePressure));
                if (samples.Count > Capacity)
                    samples.RemoveRange(0, samples.Count - Capacity / 2);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    strokePen.Dispose();
                base.Dispose(disposing);
            }
        }
    }

    internal sealed class FeelConfig
    {
        public double DeadZone { get; set; } = 0.0;      // 0..0.3
        public double Elasticity { get; set; } = 6.0;    // 0.1..10
        public int StrokeFilter { get; set; } = 2;       // 1..16
        public double MaxPressure { get; set; } = 1.0;   // 0.01..1 (recorded peak)
        public string InputMode { get; set; } = "winInk";

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OTDWintab", "feel.json");

        public static FeelConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var config = JsonSerializer.Deserialize<FeelConfig>(File.ReadAllText(FilePath));
                    if (config != null)
                        return config;
                }
            }
            catch
            {
                // A malformed file must never keep the panel from opening.
            }
            return new FeelConfig();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                // The shim reads the file once per second; a failed write is retried
                // on the next change instead of disturbing the editor.
            }
        }
    }

    /// <summary>Newest pen sample from the shared memory the Wintab plugin publishes.</summary>
    internal sealed class TabletShared
    {
        private const string MapName = "Local\\OpenTabletDriverWintab";
        private const int PacketBase = 32;
        private const int PacketSize = 48;
        private const int RingCapacity = 512;

        private IntPtr handle = IntPtr.Zero;
        private IntPtr view = IntPtr.Zero;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenFileMappingW(int access, bool inherit, string name);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, int access, int offsetHigh, int offsetLow, IntPtr bytes);

        [DllImport("kernel32")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32")]
        private static extern bool UnmapViewOfFile(IntPtr address);

        private bool TryOpen()
        {
            if (view != IntPtr.Zero)
                return true;
            handle = OpenFileMappingW(0x0004 /* FILE_MAP_READ */, false, MapName);
            if (handle == IntPtr.Zero)
                return false;
            view = MapViewOfFile(handle, 0x0004, 0, 0, (IntPtr)(PacketBase + PacketSize * RingCapacity));
            if (view == IntPtr.Zero)
            {
                CloseHandle(handle);
                handle = IntPtr.Zero;
                return false;
            }
            return true;
        }

        public bool ReadLatest(out int pressure, out int buttons, out int serial)
        {
            pressure = 0;
            buttons = 0;
            serial = 0;
            if (!TryOpen())
                return false;
            if (Marshal.ReadInt32(view, 0) != 0x4F545754) // "OTWT"
                return false;
            var writeIndex = Marshal.ReadInt32(view, 28);
            if (writeIndex <= 0)
                return false;
            serial = writeIndex;
            var slot = (writeIndex - 1) % RingCapacity;
            var at = PacketBase + slot * PacketSize;
            pressure = Marshal.ReadInt32(view, at + 16);
            buttons = Marshal.ReadInt32(view, at + 20);
            return true;
        }
    }
}
