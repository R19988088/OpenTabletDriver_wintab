using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 按键页：左边一支笔示意四个可映射位置，右边每行一个动作按钮。
    /// 动作按钮点一下循环换内置动作；后面的 ＋ 点一下进入录制，按下键盘或鼠标键
    /// 就把它记成自定义动作（按钮上直接显示具体按键），此时 ＋ 变成 ×，点 × 删除自定义。
    /// </summary>
    public sealed class ButtonsPage : Grid
    {
        private readonly ButtonsConfig _config;
        private readonly SlimHost _host;

        private Row? _capturing;
        private EventHandler<KeyEventArgs>? _keyHandler;
        private EventHandler<PointerPressedEventArgs>? _pointerHandler;

        public ButtonsPage(SlimHost host)
        {
            _host = host;
            _config = host.Buttons;

            ColumnDefinitions = new ColumnDefinitions("*,Auto");
            RowDefinitions = new RowDefinitions("Auto,Auto");

            var pen = new PenView { Margin = new Thickness(4, 4, 16, 0) };
            SetColumn(pen, 0);
            SetRowSpan(pen, 2);
            Children.Add(pen);

            var rows = new StackPanel
            {
                Spacing = 12,
                Width = 258,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 40, 0, 0)
            };

            rows.Children.Add(BindingRow("笔尖", () => _config.Tip, v => _config.Tip = v,
                () => _config.TipCustom, v => _config.TipCustom = v));
            rows.Children.Add(BindingRow("下侧键", () => _config.Barrel1, v => _config.Barrel1 = v,
                () => _config.Barrel1Custom, v => _config.Barrel1Custom = v));
            rows.Children.Add(BindingRow("上侧键", () => _config.Barrel2, v => _config.Barrel2 = v,
                () => _config.Barrel2Custom, v => _config.Barrel2Custom = v));
            rows.Children.Add(FixedRow("橡皮擦", "橡皮擦"));

            SetColumn(rows, 1);
            Children.Add(rows);

            DetachedFromVisualTree += (_, _) => CancelCapture();
        }

        /// <summary>普通行：名称 + 动作按钮 + ＋/×，三列都定宽，保证每行对齐。</summary>
        private Control BindingRow(string label, Func<string> read, Action<string> write,
            Func<string> readCustom, Action<string> writeCustom)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("52,8,*,8,26") };

            grid.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = Palette.InkBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            var chip = new Chip();
            Grid.SetColumn(chip, 2);
            grid.Children.Add(chip);

            var row = new Row(this, chip, read, write, readCustom, writeCustom);

            var toggle = new Chip { Width = 26, Padding = new Thickness(0) };
            toggle.Text = readCustom().Length > 0 ? "×" : "＋";
            toggle.Clicked += () =>
            {
                if (row.Custom.Length > 0)
                {
                    // 删掉自定义，回到内置动作
                    writeCustom(string.Empty);
                    row.Refresh();
                    _config.Save();
                }
                else
                {
                    StartCapture(row);
                }
            };

            Grid.SetColumn(toggle, 4);
            grid.Children.Add(toggle);
            row.Toggle = toggle;
            row.Refresh();
            return grid;
        }

        private Control FixedRow(string label, string text)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("52,8,*,8,26") };

            grid.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = Palette.InkBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            var chip = new Chip { IsEnabled = false, Text = text };
            Grid.SetColumn(chip, 2);
            grid.Children.Add(chip);
            return grid;
        }

        // ---- 录制 ----

        private void StartCapture(Row row)
        {
            CancelCapture();

            _capturing = row;
            row.ShowCapturing();

            var top = TopLevel.GetTopLevel(this);
            if (top == null)
                return;

            _keyHandler = (_, e) =>
            {
                if (e.KeyModifiers == KeyModifiers.None && IsModifierKey(e.Key))
                    return;

                var name = KeyName(e.Key);
                if (string.IsNullOrEmpty(name))
                    return;

                e.Handled = true;
                FinishCapture(BuildText(e.KeyModifiers, name));
            };

            _pointerHandler = (_, e) =>
            {
                var kind = e.GetCurrentPoint(null).Properties.PointerUpdateKind;
                var text = kind switch
                {
                    PointerUpdateKind.LeftButtonPressed => "鼠标左键",
                    PointerUpdateKind.RightButtonPressed => "鼠标右键",
                    PointerUpdateKind.MiddleButtonPressed => "鼠标中键",
                    _ => null
                };

                if (text == null)
                    return;

                e.Handled = true;
                // 按住 Alt/Ctrl 再点鼠标也要记进组合里
                FinishCapture(BuildText(e.KeyModifiers, text));
            };

            // 先让这一次点击结束，不然会立刻把鼠标左键录进去
            Dispatcher.UIThread.Post(() =>
            {
                top.AddHandler(InputElement.KeyDownEvent, _keyHandler!, RoutingStrategies.Tunnel, true);
                top.AddHandler(InputElement.PointerPressedEvent, _pointerHandler!, RoutingStrategies.Tunnel, true);
            });
        }

        private void FinishCapture(string text)
        {
            var row = _capturing;
            CancelCapture();

            if (row == null)
                return;

            row.SetCustom(text);
            _config.Save();
        }

        private void CancelCapture()
        {
            if (TopLevel.GetTopLevel(this) is TopLevel top)
            {
                if (_keyHandler != null)
                    top.RemoveHandler(InputElement.KeyDownEvent, _keyHandler);
                if (_pointerHandler != null)
                    top.RemoveHandler(InputElement.PointerPressedEvent, _pointerHandler);
            }

            _keyHandler = null;
            _pointerHandler = null;
            _capturing?.ShowIdle();
            _capturing = null;
        }

        private static bool IsModifierKey(Key key) =>
            key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

        private static string BuildText(KeyModifiers modifiers, string key)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (modifiers.HasFlag(KeyModifiers.Control))
                parts.Add("Ctrl");
            if (modifiers.HasFlag(KeyModifiers.Shift))
                parts.Add("Shift");
            if (modifiers.HasFlag(KeyModifiers.Alt))
                parts.Add("Alt");
            if (modifiers.HasFlag(KeyModifiers.Meta))
                parts.Add("Win");
            parts.Add(key);
            return string.Join("+", parts);
        }

        /// <summary>Avalonia 的 Key → 存盘时用的名字。</summary>
        private static string? KeyName(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
                return ((char)('A' + (int)key - (int)Key.A)).ToString();
            if (key >= Key.D0 && key <= Key.D9)
                return ((char)('0' + (int)key - (int)Key.D0)).ToString();
            if (key >= Key.F1 && key <= Key.F12)
                return "F" + (1 + (int)key - (int)Key.F1);

            return key switch
            {
                Key.Space => "Space",
                Key.Enter => "Enter",
                Key.Escape => "Esc",
                Key.Tab => "Tab",
                Key.Back => "Backspace",
                Key.Delete => "Delete",
                Key.Insert => "Insert",
                Key.Home => "Home",
                Key.End => "End",
                Key.PageUp => "PageUp",
                Key.PageDown => "PageDown",
                Key.Up => "Up",
                Key.Down => "Down",
                Key.Left => "Left",
                Key.Right => "Right",
                _ => null
            };
        }

        // ---- 单行状态 ----

        private sealed class Row
        {
            private readonly ButtonsPage _page;
            private readonly Chip _chip;
            private readonly Func<string> _read;
            private readonly Action<string> _write;
            private readonly Func<string> _readCustom;
            private readonly Action<string> _writeCustom;

            public Chip? Toggle { get; set; }
            public string Custom { get; private set; } = string.Empty;

            public Row(ButtonsPage page, Chip chip, Func<string> read, Action<string> write,
                Func<string> readCustom, Action<string> writeCustom)
            {
                _page = page;
                _chip = chip;
                _read = read;
                _write = write;
                _readCustom = readCustom;
                _writeCustom = writeCustom;

                chip.Clicked += () =>
                {
                    if (Custom.Length > 0)
                        return;

                    var next = ButtonsConfig.Next(_read());
                    _write(next);
                    _chip.Text = next;
                    _page._config.Save();
                    _page._host.ApplyMapping();
                };
            }

            public void ShowCapturing() => _chip.Text = "请按键…";

            public void ShowIdle() => _chip.Text = Custom.Length > 0 ? Custom : _read();

            public void SetCustom(string text)
            {
                Custom = text;
                _writeCustom(text);
                _chip.Text = text;
                if (Toggle != null)
                    Toggle.Text = "×";
            }

            public void Refresh()
            {
                Custom = _readCustom();
                _chip.Text = Custom.Length > 0 ? Custom : _read();
                if (Toggle != null)
                    Toggle.Text = Custom.Length > 0 ? "×" : "＋";
                _page._host.ApplyMapping();
            }
        }

        /// <summary>右边那个动作 / ＋ 按钮。</summary>
        private sealed class Chip : Border
        {
            private readonly TextBlock _text;

            public Chip()
            {
                Height = 30;
                CornerRadius = new CornerRadius(6);
                BorderThickness = new Thickness(1);
                BorderBrush = Palette.FieldBorderBrush;
                Background = Palette.HoverBrush;
                HorizontalAlignment = HorizontalAlignment.Stretch;
                Padding = new Thickness(8, 0, 8, 0);

                _text = new TextBlock
                {
                    FontSize = 12,
                    Foreground = Palette.InkSoftBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Child = _text;

                Cursor = new Cursor(StandardCursorType.Hand);
                PointerPressed += (_, e) =>
                {
                    Clicked?.Invoke();
                    e.Handled = true;
                };
            }

            public event Action? Clicked;

            public string Text
            {
                get => _text.Text ?? string.Empty;
                set => _text.Text = value;
            }
        }
    }
}
