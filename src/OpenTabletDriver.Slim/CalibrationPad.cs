using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 压力校准区：笔在框里画一笔，线宽跟着实时压力变；
    /// 抬笔时保留"松手前 100ms 的峰值"，这就是这块板子能达到的最大压力。
    /// 数据来自 wintab-shim 的共享内存（和旧版 feel-panel 同一个来源）。
    /// </summary>
    public sealed class CalibrationPad : Border
    {
        private readonly StrokeCanvas _canvas = new();
        private readonly TextBlock _liveLabel = new();
        private readonly WintabSharedReader _reader = new();
        private readonly DispatcherTimer _timer = new();
        private readonly List<StrokeSegment> _segments = new();
        private readonly List<(long Tick, float Pressure)> _samples = new();

        private Point? _last;
        private float _livePressure;
        private uint _buttons;
        private bool _inRange;
        private float _paintedPressure;

        public CalibrationPad()
        {
            Background = Palette.PadBrush;
            BorderBrush = Palette.PadBorderBrush;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(8);
            ClipToBounds = true;
            MinHeight = 160;

            _canvas.Segments = _segments;

            _liveLabel.Foreground = Palette.InkDimBrush;
            _liveLabel.FontSize = 11;
            _liveLabel.Margin = new Thickness(0);
            _liveLabel.VerticalAlignment = VerticalAlignment.Center;
            _liveLabel.HorizontalAlignment = HorizontalAlignment.Center;

            var panel = new Panel();
            panel.Children.Add(_canvas);
            panel.Children.Add(_liveLabel);
            Child = panel;

            PointerPressed += OnPressed;
            PointerMoved += OnMoved;
            PointerReleased += OnReleased;

            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.Tick += OnTick;
            _timer.Start();

            UpdateLiveLabel();
        }

        public event Action<double>? PeakRecorded;

        private void OnTick(object? sender, EventArgs e)
        {
            if (!_reader.TryRead(out var pressure, out var buttons, out var inRange))
                return;

            var changed = Math.Abs(pressure - _livePressure) > 0.005f || buttons != _buttons;
            _livePressure = pressure;
            _buttons = buttons;
            _inRange = inRange;

            if (inRange || buttons != 0)
            {
                _samples.Add((Environment.TickCount64, pressure));
                if (_samples.Count > 512)
                    _samples.RemoveRange(0, _samples.Count - 256);
            }

            if (changed)
                UpdateLiveLabel();
        }

        private void UpdateLiveLabel() =>
            // 只留一句提示，不再显示实时压力百分比
            _liveLabel.Text = _inRange || _segments.Count > 0 ? string.Empty : "请先用力画一笔。";

        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            _segments.Clear();
            _samples.Clear();
            _paintedPressure = _livePressure;
            _last = e.GetPosition(this);
            _canvas.InvalidateVisual();
            e.Pointer.Capture(this);
        }

        private void OnMoved(object? sender, PointerEventArgs e)
        {
            if (_last == null || e.Pointer.Captured != this)
                return;

            var point = e.GetPosition(this);
            _segments.Add(new StrokeSegment(_last.Value, point, _livePressure));
            _paintedPressure = Math.Max(_paintedPressure, _livePressure);
            _last = point;
            _canvas.InvalidateVisual();
        }

        private void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_last == null)
                return;

            _last = null;
            e.Pointer.Capture(null);

            // 抬笔时压力一定会掉下去，参考值取"松手前 100ms"的峰值
            var now = Environment.TickCount64;
            var peak = 0f;
            foreach (var sample in _samples)
            {
                if (sample.Tick <= now - 100 && sample.Pressure > peak)
                    peak = sample.Pressure;
            }

            if (peak <= 0)
                peak = _paintedPressure;

            if (peak > 0)
                PeakRecorded?.Invoke(Math.Clamp(Math.Round(peak, 2), 0.01, 1.0));

            _canvas.InvalidateVisual();
        }

        /// <summary>只负责把线段按压力画成粗细不同的笔画。</summary>
        private sealed class StrokeCanvas : Control
        {
            public List<StrokeSegment>? Segments { get; set; }

            public override void Render(DrawingContext context)
            {
                var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
                context.DrawRectangle(Palette.PadBrush, new Pen(Palette.PadBorderBrush, 1), rect, 8, 8);

                if (Segments == null)
                    return;

                foreach (var segment in Segments)
                {
                    var brush = Palette.StrokeBrush;
                    var pen = new Pen(brush, 1.2f + segment.Pressure * 7f)
                    {
                        LineCap = PenLineCap.Round
                    };
                    context.DrawLine(pen, segment.From, segment.To);
                }
            }
        }

        private readonly struct StrokeSegment
        {
            public StrokeSegment(Point from, Point to, float pressure)
            {
                From = from;
                To = to;
                Pressure = pressure;
            }

            public Point From { get; }
            public Point To { get; }
            public float Pressure { get; }
        }
    }
}
