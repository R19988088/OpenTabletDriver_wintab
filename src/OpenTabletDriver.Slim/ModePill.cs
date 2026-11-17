using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace OpenTabletDriver.Slim
{
    /// <summary>输入模式 / 开关用的胶囊按钮：选中时填主题蓝。</summary>
    internal sealed class ModePill : Border
    {
        private readonly TextBlock _text;

        public ModePill(string label)
        {
            Height = 30;
            MinWidth = 96;
            CornerRadius = new CornerRadius(6);
            BorderThickness = new Thickness(1);
            BorderBrush = Palette.FieldBorderBrush;
            Background = Palette.HoverBrush;
            Cursor = new Cursor(StandardCursorType.Hand);
            Padding = new Avalonia.Thickness(12, 0, 12, 0);

            _text = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = Palette.InkSoftBrush,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Child = _text;

            PointerPressed += (_, e) =>
            {
                Clicked?.Invoke();
                e.Handled = true;
            };
        }

        public event Action? Clicked;

        public bool IsOn
        {
            get => _on;
            set
            {
                _on = value;
                Background = value ? Palette.AccentBrush : Palette.HoverBrush;
                BorderBrush = value ? Palette.AccentBrush : Palette.FieldBorderBrush;
                _text.Foreground = value ? Brushes.White : Palette.InkSoftBrush;
            }
        }

        private bool _on;
    }
}
