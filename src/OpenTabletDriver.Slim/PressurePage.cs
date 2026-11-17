using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 压力页 —— 旧版 feel-panel 的等价实现：
    /// 输入模式、压力死区、弹性、笔触稳定、压力校准（保留峰值）。
    /// 所有数值都写回 %LOCALAPPDATA%\OTDWintab\feel.json，由 wintab-shim 读取生效。
    /// </summary>
    public sealed class PressurePage : Grid
    {
        private readonly FeelConfig _config = FeelConfig.Load();
        private readonly DragValueField _deadZone = new() { Min = 0, Max = 30 };
        private readonly DragValueField _elasticity = new() { Min = 1, Max = 100 };
        private readonly DragValueField _strokeFilter = new() { Min = 1, Max = 16 };
        private readonly CalibrationPad _pad = new();
        private readonly TextBlock _peakLabel = new();
        private readonly ModePill _winTab = new("WinTab");
        private readonly ModePill _windowsInk = new("Windows Ink");

        private readonly SlimHost _host;
        private readonly ModePill _buttonsLeft = new("按键在左");
        private readonly ModePill _buttonsRight = new("按键在右");

        public PressurePage(SlimHost host)
        {
            _host = host;

            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*");
            ColumnDefinitions = new ColumnDefinitions("*,Auto");
            Margin = new Thickness(0);

            Row("数位板输入模式", 0);
            var modePanel = PillPair(_winTab, _windowsInk);
            modePanel.Margin = new Thickness(0, 6, 0, 6);
            SetRow(modePanel, 0);
            SetColumn(modePanel, 1);
            Children.Add(modePanel);

            AddFieldRow("压力死区", _deadZone, 1);
            AddFieldRow("弹性", _elasticity, 2);
            AddFieldRow("笔触稳定", _strokeFilter, 3);

            // 按键在左 / 按键在右：ExpressKey 在哪一侧（右侧 = 输入区域整体转 180°）
            Row("按键位置", 4);
            var buttonPanel = PillPair(_buttonsLeft, _buttonsRight);
            buttonPanel.Margin = new Thickness(0, 6, 0, 6);
            SetRow(buttonPanel, 4);
            SetColumn(buttonPanel, 1);
            Children.Add(buttonPanel);

            var calHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            calHeader.Children.Add(new TextBlock
            {
                Text = "压力校准",
                FontSize = 14,
                Foreground = Palette.InkBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            _peakLabel.Foreground = Palette.InkDimBrush;
            _peakLabel.FontSize = 12;
            _peakLabel.HorizontalAlignment = HorizontalAlignment.Right;
            _peakLabel.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_peakLabel, 1);
            calHeader.Children.Add(_peakLabel);
            calHeader.Margin = new Thickness(0, 18, 0, 10);
            SetRow(calHeader, 5);
            SetColumnSpan(calHeader, 2);
            Children.Add(calHeader);

            _pad.Margin = new Thickness(0, 0, 0, 12);
            _pad.Height = 240;
            _pad.VerticalAlignment = VerticalAlignment.Top;
            SetRow(_pad, 6);
            SetColumnSpan(_pad, 2);
            Children.Add(_pad);



            WireEvents();
            LoadValues();
        }

        /// <summary>两个胶囊平分滑条的宽度，左右合计正好和上面的输入框一样宽。</summary>
        private static Grid PillPair(ModePill left, ModePill right)
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,8,*"),
                Width = 250,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            foreach (var pill in new[] { left, right })
            {
                pill.MinWidth = 0;
                pill.HorizontalAlignment = HorizontalAlignment.Stretch;
                pill.Margin = new Thickness(0);
            }

            grid.Children.Add(left);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
            return grid;
        }
        private void WireEvents()
        {
            _winTab.Clicked += () => SetMode("winTab");
            _windowsInk.Clicked += () => SetMode("winInk");

            _deadZone.ValueChanged += value =>
            {
                _config.DeadZone = value / 100.0;
                _config.Save();
            };

            _elasticity.ValueChanged += value =>
            {
                _config.Elasticity = value / 10.0;
                _config.Save();
            };

            _strokeFilter.ValueChanged += value =>
            {
                _config.StrokeFilter = (int)Math.Round(value);
                _config.Save();
            };

            _buttonsLeft.Clicked += () => SetButtonsOnRight(false);
            _buttonsRight.Clicked += () => SetButtonsOnRight(true);

            _pad.PeakRecorded += value =>
            {
                _config.MaxPressure = value;
                _config.Save();
                UpdatePeakLabel();
            };
        }

        private void LoadValues()
        {
            _deadZone.Value = (int)Math.Round(_config.DeadZone * 100);
            _elasticity.Value = (int)Math.Round(_config.Elasticity * 10);
            _strokeFilter.Value = _config.StrokeFilter;
            UpdateMode(_config.InputMode);
            UpdateButtons();
            UpdatePeakLabel();
        }

        private void SetMode(string mode)
        {
            _config.InputMode = mode;
            _config.Save();
            UpdateMode(mode);
        }

        private void SetButtonsOnRight(bool onRight)
        {
            _host.Mapping.ButtonsOnRight = onRight;
            _host.Mapping.Save();
            _host.ApplyMapping();
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var onRight = _host.Mapping.ButtonsOnRight;
            _buttonsLeft.IsOn = !onRight;
            _buttonsRight.IsOn = onRight;
        }

        private void UpdateMode(string mode)
        {
            var winTab = mode == "winTab";
            _winTab.IsOn = winTab;
            _windowsInk.IsOn = !winTab;
        }

        private void UpdatePeakLabel() =>
            _peakLabel.Text = $"保留峰值 {_config.MaxPressure * 100:0.0}%";

        private Control Row(string label, int row)
        {
            var text = new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = Palette.InkSoftBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 16, 6)
            };
            SetRow(text, row);
            SetColumn(text, 0);
            Children.Add(text);
            return text;
        }

        private void AddFieldRow(string label, DragValueField field, int row)
        {
            Row(label, row);
            PlaceField(field, row);
        }

        private void PlaceField(Control field, int row)
        {
            field.Margin = new Thickness(0, 6, 0, 6);
            field.VerticalAlignment = VerticalAlignment.Center;
            field.HorizontalAlignment = HorizontalAlignment.Right;
            field.Width = 250;
            SetRow(field, row);
            SetColumn(field, 1);
            Children.Add(field);
        }


    }
}
