using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenTabletDriver.Plugin;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 主窗口：系统自带的标题栏 + 左右两栏（左侧导航，右侧页面）。
    /// 标题栏、圆角、投影都交给系统，应用只负责内容。
    /// </summary>
    public sealed class MainWindow : Window
    {
        private readonly SlimHost _host = new();

        private readonly TextBlock _tabletLine = new();
        private readonly TextBlock _outputLine = new();
        private readonly TextBlock _wintabLine = new();
        private readonly ToggleSwitch _autoStart = new();
        private readonly ContentControl _pageHost = new();
        private readonly StackPanel _nav = new();

        private readonly Dictionary<string, Control> _pages = new();
        private readonly Dictionary<string, Border> _navItems = new();

        private bool _outputStarted;
        private bool _installing;

        public MainWindow()
        {
            Title = "OTD Slim";
            Width = 660;
            Height = 720;
            MinWidth = 640;
            MinHeight = 600;
            CanResize = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Palette.WindowBrush;

            // 全局热键钩子要在建页面之前起来，页面拿的是同一份配置
            // 自启项的路径永远跟当前 exe 走，换版本后不会还指向旧目录
            StartupAuto.EnsurePath();
            HotkeyEngine.Start();

            Content = BuildShell();
            ShowPage(Program.StartupPage ?? "压力");

            _host.StateChanged += () => Dispatcher.UIThread.Post(Refresh);

            Activated += (_, _) => WindowTheme.UseLightTitleBar(PlatformHandle());

            Opened += async (_, _) =>
            {
                // 标题栏颜色要在窗口真正显示后设一次，Avalonia 应用主题时可能在 Created 阶段改回去，
                // 所以再挂一个延迟回调补一刀。
                WindowTheme.UseLightTitleBar(PlatformHandle());
                Avalonia.Threading.DispatcherTimer.RunOnce(
                    () => WindowTheme.UseLightTitleBar(PlatformHandle()),
                    TimeSpan.FromMilliseconds(250));

                // 旧版 OTD 的自启项会和这套驱动打架，检测到就清掉
                if (StartupAuto.HasLegacy())
                    StartupAuto.CleanLegacy();

                await _host.DetectAsync();
                if (!_outputStarted)
                {
                    _outputStarted = true;
                    _host.StartOutput();
                }

                Refresh();
            };

            // 关闭必须保证进程真的退出：释放设备可能卡住（USB 读阻塞），
            // 一旦卡住就变成"关不掉的后台进程"，所以 300ms 后无条件强退。
            Closing += (_, _) =>
            {
                new System.Threading.Thread(() =>
                {
                    System.Threading.Thread.Sleep(300);
                    Environment.Exit(0);
                })
                {
                    IsBackground = true
                }.Start();

                try
                {
                    _host.Dispose();
                }
                catch
                {
                    // 释放失败不影响退出
                }
            };

            var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tick.Tick += (_, _) => Refresh();
            tick.Start();

            Refresh();
        }

        private Control BuildShell()
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("176,*") };
            grid.Children.Add(BuildSidebar());

            var scroll = new ScrollViewer
            {
                Content = _pageHost,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };

            var content = new Border
            {
                Background = Palette.WindowBrush,
                Padding = new Thickness(24, 20, 16, 16),
                Child = scroll
            };
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);

            return grid;
        }

        private Control BuildSidebar()
        {
            _nav.Spacing = 2;
            _nav.Margin = new Thickness(12, 14, 12, 0);

            foreach (var name in new[] { "压力", "映射", "按键", "附加", "设备", "关于" })
            {
                var item = NavItem(name, name is "压力" or "映射" or "按键" or "附加");
                _navItems[name] = item;
                _nav.Children.Add(item);
            }

            // 侧栏底部：开机自启开关 + Wintab32.dll 安装状态
            var extras = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(22, 0, 16, 12)
            };
            extras.Children.Add(BuildAutoStartRow());
            extras.Children.Add(BuildWintabRow());

            _tabletLine.FontSize = 11;
            _tabletLine.Foreground = Palette.InkDimBrush;
            _tabletLine.TextTrimming = TextTrimming.CharacterEllipsis;

            _outputLine.FontSize = 11;
            _outputLine.Foreground = Palette.InkDimBrush;

            var status = new StackPanel
            {
                Spacing = 2,
                Margin = new Thickness(22, 0, 16, 16)
            };
            status.Children.Add(_tabletLine);
            status.Children.Add(_outputLine);

            var dock = new DockPanel();
            DockPanel.SetDock(status, Dock.Bottom);
            dock.Children.Add(status);
            DockPanel.SetDock(extras, Dock.Bottom);
            dock.Children.Add(extras);
            dock.Children.Add(_nav);

            return new Border
            {
                Background = Palette.SidebarBrush,
                BorderBrush = Palette.SeparatorBrush,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Child = dock
            };
        }

        private Control BuildAutoStartRow()
        {
            var label = new TextBlock
            {
                Text = "开机自启",
                FontSize = 11,
                Foreground = Palette.InkDimBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            _autoStart.IsChecked = StartupAuto.IsEnabled();
            _autoStart.OnContent = string.Empty;
            _autoStart.OffContent = string.Empty;
            _autoStart.MinWidth = 36;
            _autoStart.VerticalAlignment = VerticalAlignment.Center;
            _autoStart.HorizontalAlignment = HorizontalAlignment.Right;

            _autoStart.PropertyChanged += (_, e) =>
            {
                if (e.Property != ToggleSwitch.IsCheckedProperty)
                    return;

                StartupAuto.SetEnabled(_autoStart.IsChecked == true);
            };

            var row = new DockPanel();
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            row.Children.Add(_autoStart);
            return row;
        }

        private Control BuildWintabRow()
        {
            _wintabLine.FontSize = 11;
            _wintabLine.TextWrapping = TextWrapping.Wrap;
            _wintabLine.Cursor = new Cursor(StandardCursorType.Hand);

            var item = new Border
            {
                Background = Brushes.Transparent,
                Child = _wintabLine
            };
            item.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                if (WintabSetup.Detect() == WintabState.Ours)
                    return;

                _wintabLine.Text = "正在安装… 请在系统弹窗里允许";
                _wintabLine.Foreground = Palette.AccentBrush;
                _installing = true;

                // 提权复制要等 UAC，放后台跑；完成后无论成败都把这一行刷成真实状态
                System.Threading.Tasks.Task.Run(() => WintabSetup.Install())
                    .ContinueWith(task => Dispatcher.UIThread.Post(() =>
                    {
                        _installing = false;
                        Refresh();

                        if (WintabSetup.Detect() != WintabState.Ours)
                        {
                            _wintabLine.Text = task.Result.Message;
                            _wintabLine.Foreground = Palette.AccentBrush;
                        }

                        // 强制重排一次，避免文本变了但这一行没重画
                        _wintabLine.InvalidateMeasure();
                        _wintabLine.InvalidateVisual();
                    }));
            };

            return item;
        }

        private Border NavItem(string name, bool enabled)
        {
            var text = new TextBlock
            {
                Text = name,
                FontSize = 13,
                Foreground = enabled ? Palette.InkSoftBrush : Palette.InkDimBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };

            var item = new Border
            {
                Height = 34,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Child = text
            };

            if (enabled)
            {
                item.Cursor = new Cursor(StandardCursorType.Hand);
                item.PointerPressed += (_, e) =>
                {
                    ShowPage(name);
                    e.Handled = true;
                };
            }

            return item;
        }

        private IntPtr PlatformHandle() => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        private void ShowPage(string name)
        {
            foreach (var pair in _navItems)
            {
                var enabled = pair.Key is "压力" or "映射" or "按键" or "附加";
                var selected = pair.Key == name;
                pair.Value.Background = selected ? Palette.SelectedBrush : Brushes.Transparent;

                if (pair.Value.Child is TextBlock label)
                {
                    label.Foreground = selected
                        ? Palette.InkBrush
                        : enabled ? Palette.InkSoftBrush : Palette.InkDimBrush;
                }
            }

            _pageHost.Content = PageFor(name);
        }

        private Control PageFor(string name)
        {
            if (_pages.TryGetValue(name, out var cached))
                return cached;

            Control page = name switch
            {
                "压力" => new PressurePage(_host),
                "映射" => new MappingPage(_host),
                "按键" => new ButtonsPage(_host),
                "附加" => new TweaksPage(),
                _ => Placeholder(name)
            };

            _pages[name] = page;
            return page;
        }

        private static Control Placeholder(string name) => new TextBlock
        {
            Text = $"「{name}」这一页还没做",
            FontSize = 13,
            Foreground = Palette.InkDimBrush,
            Margin = new Thickness(0, 4, 0, 0)
        };

        private void Refresh()
        {
            _tabletLine.Text = _host.TabletName;
            _outputLine.Text = _host.OutputState.Split('·')[0].Trim();

            try
            {
                if (_installing)
                    return;

                var state = WintabSetup.Detect();
                _wintabLine.Text = WintabSetup.Describe(state);
                _wintabLine.Foreground = state == WintabState.Ours ? Palette.InkDimBrush : Palette.AccentBrush;
            }
            catch
            {
                // 检测出错（比如文件被占用）不该拖垮整个刷新
            }

            if (_autoStart.IsChecked != StartupAuto.IsEnabled())
                _autoStart.IsChecked = StartupAuto.IsEnabled();
        }
    }
}
