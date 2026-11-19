using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 「输入」页 —— v33 起把原来的「压力」页改名，并在底部加上三个触摸开关。
    ///
    /// 上半部分还是旧版 feel-panel 的等价实现（输入模式、压力死区、弹性、笔触稳定、按键位置、
    /// 压力校准），数值全部写回 %LOCALAPPDATA%\OTDWintab\feel.json，由 wintab-shim 读取生效。
    ///
    /// 下半部分是 v33 新增的两块：
    ///   · 触摸控制：撤销 / 旋转 / 缩放，写 feel.json 的 TouchUndo / TouchRotate / TouchZoom，
    ///     由 TouchEngine 在收到 0x11 / 0x21 / 0xD2 报文时读取；
    ///   · Wacom 接管：四步接管按钮 + 恢复按钮 + 实时状态（服务 Start 值、Wacom 进程数量）。
    /// </summary>
    public sealed class InputPage : Grid
    {
        private readonly FeelConfig _config = FeelConfig.Load();
        private readonly DragValueField _deadZone = new() { Min = 0, Max = 30 };
        private readonly DragValueField _elasticity = new() { Min = 1, Max = 100 };
        private readonly DragValueField _strokeFilter = new() { Min = 1, Max = 16 };
        private readonly CalibrationPad _pad = new();
        private readonly ModePill _buttonsLeft = new("按键在左");
        private readonly ModePill _buttonsRight = new("按键在右");
        private readonly TextBlock _peakLabel = new();

        private readonly SlimHost _host;

        private readonly TextBlock _touchStatus = new();




        public InputPage(SlimHost host)
        {
            _host = host;

            RowDefinitions = new RowDefinitions(
                "Auto,Auto,Auto,Auto,Auto,Auto,Auto," +      // 0..6 压力相关
                "Auto,Auto,Auto,Auto,Auto," +                // 7..11 触摸控制
                "*");                                        // 12 起余量
            ColumnDefinitions = new ColumnDefinitions("*,Auto");
            Margin = new Thickness(0);

            AddFieldRow("压力死区", _deadZone, 1);
            AddFieldRow("弹性", _elasticity, 2);
            AddFieldRow("笔触稳定", _strokeFilter, 3);

            // 按键在左 / 按键在右：ExpressKey 在哪一侧（右侧 = 映射区域整体转 180°）
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
            _pad.Height = 192;
            _pad.VerticalAlignment = VerticalAlignment.Top;
            SetRow(_pad, 6);
            SetColumnSpan(_pad, 2);
            Children.Add(_pad);

            // ---- 触摸控制（v33）----
            Children.Add(SectionHeader("触摸控制", 7));

            AddToggleRow("三指撤销（Ctrl+Z）", _config.TouchUndo,
                value => { _config.TouchUndo = value; _config.Save(); }, 8);
            AddToggleRow("双指旋转（Ctrl+Alt+← / →）", _config.TouchRotate,
                value => { _config.TouchRotate = value; _config.Save(); }, 9);
            AddToggleRow("双指缩放（Ctrl + / -）", _config.TouchZoom,
                value => { _config.TouchZoom = value; _config.Save(); }, 10);

            _touchStatus.FontSize = 11;
            _touchStatus.Foreground = Palette.InkDimBrush;
            _touchStatus.TextWrapping = TextWrapping.Wrap;
            _touchStatus.Margin = new Thickness(0, 8, 0, 4);
            SetRow(_touchStatus, 11);
            SetColumnSpan(_touchStatus, 2);
            Children.Add(_touchStatus);


            WireEvents();
            LoadValues();

            UpdateStatus();

            var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            tick.Tick += (_, _) => UpdateStatus();
            tick.Start();
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
            _buttonsLeft.Clicked += () => SetButtonsOnRight(false);
            _buttonsRight.Clicked += () => SetButtonsOnRight(true);

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

            UpdateButtons();
            UpdatePeakLabel();
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


        private void UpdatePeakLabel() =>
            _peakLabel.Text = $"保留峰值 {_config.MaxPressure * 100:0.0}%";

        private void UpdateStatus()
        {
            _touchStatus.Text = "触摸：" + _host.Touch.Describe();
        }


        private Control SectionHeader(string text, int row)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 14,
                Foreground = Palette.InkBrush,
                Margin = new Thickness(0, 22, 0, 8)
            };
            SetRow(label, row);
            SetColumnSpan(label, 2);
            return label;
        }

        private void AddToggleRow(string label, bool value, Action<bool> changed, int row)
        {
            Row(label, row);

            var toggle = new ToggleSwitch
            {
                OnContent = string.Empty,
                OffContent = string.Empty,
                MinWidth = 36,
                IsChecked = value,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 4)
            };

            toggle.PropertyChanged += (_, e) =>
            {
                if (e.Property != ToggleSwitch.IsCheckedProperty)
                    return;

                changed(toggle.IsChecked == true);
            };

            SetRow(toggle, row);
            SetColumn(toggle, 1);
            Children.Add(toggle);
        }

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
