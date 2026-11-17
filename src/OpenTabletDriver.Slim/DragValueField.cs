using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 旧版 DragValue 的 Avalonia 版：数值框本身就是一个滑块。
    /// 左右拖动连续调值，点左右半边 ±1 步，滚轮 ±1 步。
    /// 支持小数位（显示器区域用 0 位、数位板毫米用 1 位）。
    /// </summary>
    public sealed class DragValueField : Border
    {
        private readonly TextBlock _left;
        private readonly TextBlock _valueText;
        private readonly TextBlock _right;

        private double _value;
        private bool _dragging;
        private double _startX;
        private double _startValue;

        public DragValueField()
        {
            Min = 0;
            Max = 100;
            Step = 1;

            Background = Palette.FieldBrush;
            BorderBrush = Palette.FieldBorderBrush;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(6);
            Height = 30;
            Cursor = new Cursor(StandardCursorType.SizeWestEast);

            _left = Chevron("‹");
            _valueText = new TextBlock
            {
                Foreground = Palette.InkBrush,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _right = Chevron("›");

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*,26") };
            Grid.SetColumn(_left, 0);
            Grid.SetColumn(_valueText, 1);
            Grid.SetColumn(_right, 2);
            grid.Children.Add(_left);
            grid.Children.Add(_valueText);
            grid.Children.Add(_right);

            Child = grid;
            UpdateText();

            PointerPressed += OnPressed;
            PointerMoved += OnMoved;
            PointerReleased += OnReleased;
            PointerWheelChanged += OnWheel;
        }

        public double Min { get; set; }
        public double Max { get; set; }
        public double Step { get; set; }

        /// <summary>显示的小数位数，0 表示整数。</summary>
        public int Decimals { get; set; }

        public event Action<double>? ValueChanged;

        public double Value
        {
            get => _value;
            set => SetValue(value, notify: false);
        }

        private static TextBlock Chevron(string glyph) => new()
        {
            Text = glyph,
            Foreground = Palette.InkDimBrush,
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        private void SetValue(double value, bool notify = true)
        {
            var clamped = Math.Clamp(value, Min, Max);
            if (Decimals <= 0)
                clamped = Math.Round(clamped);
            else
                clamped = Math.Round(clamped, Decimals);

            var changed = Math.Abs(clamped - _value) > 1e-9;
            _value = clamped;
            UpdateText();
            if (changed && notify)
                ValueChanged?.Invoke(_value);
        }

        private void UpdateText() => _valueText.Text = Decimals > 0
            ? _value.ToString("F" + Decimals)
            : ((long)Math.Round(_value)).ToString();

        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            _startX = e.GetPosition(this).X;
            _startValue = _value;
            _dragging = false;
            e.Pointer.Capture(this);
        }

        private void OnMoved(object? sender, PointerEventArgs e)
        {
            var x = e.GetPosition(this).X;
            var half = Bounds.Width / 2;
            Highlight(_left, x < half - 24);
            Highlight(_right, x > half + 24);

            if (e.Pointer.Captured != this)
                return;

            var dx = x - _startX;
            if (!_dragging && Math.Abs(dx) > 8)
                _dragging = true;

            if (_dragging)
            {
                // 每 6 像素一个步进
                SetValue(_startValue + Math.Round(dx / 6.0) * Step);
            }
        }

        private void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.Pointer.Captured != this)
                return;

            e.Pointer.Capture(null);
            if (!_dragging)
            {
                var half = Bounds.Width / 2;
                SetValue(_value + (e.GetPosition(this).X < half ? -Step : Step));
            }

            _dragging = false;
        }

        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            SetValue(_value + (e.Delta.Y > 0 ? Step : -Step));
            e.Handled = true;
        }

        private static void Highlight(TextBlock chevron, bool on) =>
            chevron.Foreground = new SolidColorBrush(on ? Palette.Ink : Palette.InkDim);
    }
}
