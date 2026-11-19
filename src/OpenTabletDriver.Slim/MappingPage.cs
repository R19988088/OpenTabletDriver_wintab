using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 映射页 —— 对应原版 OTD 的「显示器区域」+「数位板区域」，全部图形化：
    ///   · 上画布：整个虚拟桌面（多块屏都画出来），拖动里面的矩形决定映射到哪块屏、多大
    ///   · 下画布：整块数位板（毫米），拖动里面的矩形决定用板子的哪一块
    ///   · 锁定比例：数位板区域的宽高比跟随显示器区域
    ///
    /// 没有下拉框、没有数值输入框。拖动时只重绘，松手才写 mapping.json 并热更新管线。
    /// </summary>
    public sealed class MappingPage : Grid
    {
        private readonly SlimHost _host;
        private readonly MappingConfig _config;

        private readonly AreaEditor _displayEditor = new(allowRotation: false) { Height = 260 };
        private readonly AreaEditor _tabletEditor = new(allowRotation: false) { Height = 200 };
        private readonly ModePill _lockRatio = new("锁定比例");
        private readonly ModePill _buttonsLeft = new("按键在左");
        private readonly ModePill _buttonsRight = new("按键在右");

        private IReadOnlyList<Displays.Info> _displays = [];
        private bool _loading;

        public MappingPage(SlimHost host)
        {
            _host = host;
            _config = host.Mapping;
            _displays = Displays.Enum();

            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*");
            ColumnDefinitions = new ColumnDefinitions("*,Auto");
            Margin = new Thickness(0);

            Children.Add(SectionHeader("显示器区域", 0));

            _displayEditor.Margin = new Thickness(0, 0, 0, 16);
            SetRow(_displayEditor, 1);
            SetColumnSpan(_displayEditor, 2);
            Children.Add(_displayEditor);

            Children.Add(SectionHeader("数位板区域", 2));

            _tabletEditor.Margin = new Thickness(0, 0, 0, 14);
            SetRow(_tabletEditor, 3);
            SetColumnSpan(_tabletEditor, 2);
            Children.Add(_tabletEditor);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            buttons.Children.Add(_lockRatio);
            buttons.Children.Add(MakeTextButton("铺满该显示器", () =>
            {
                var display = CurrentDisplay();
                if (display == null)
                    return;

                _config.FillDisplay(display);
                SyncEditors();
                Commit();
            }));

            SetRow(buttons, 5);
            SetColumnSpan(buttons, 2);
            Children.Add(buttons);

            // 按键在左 / 按键在右：ExpressKey 在哪一侧（右侧 = 绘制区域整体转 180°）
            var buttonRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 4, 0, 0)
            };

            buttonRow.Children.Add(new TextBlock
            {
                Text = "按键位置",
                FontSize = 14,
                Foreground = Palette.InkBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            var buttonPair = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,8,*"),
                Width = 250,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            foreach (var pill in new[] { _buttonsLeft, _buttonsRight })
            {
                pill.MinWidth = 0;
                pill.HorizontalAlignment = HorizontalAlignment.Stretch;
                pill.Margin = new Thickness(0);
            }

            buttonPair.Children.Add(_buttonsLeft);
            Grid.SetColumn(_buttonsRight, 2);
            buttonPair.Children.Add(_buttonsRight);
            Grid.SetColumn(buttonPair, 1);
            buttonRow.Children.Add(buttonPair);

            SetRow(buttonRow, 4);
            SetColumnSpan(buttonRow, 2);
            Children.Add(buttonRow);



            WireEvents();
            SyncEditors();
            UpdatePills();


            // 数位板/输出启动之后默认值才有，界面要跟着再同步一次
            _host.StateChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _displays = Displays.Enum();
                SyncEditors();
            });

            // 显示器热插拔：Host 那边已经把窗口尺寸和映射都重算完了，这里只负责重画
            _host.DisplaysChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _displays = Displays.Enum();
                SyncEditors();
                UpdatePills();
            });
        }


        private void WireEvents()
        {
            _buttonsLeft.Clicked += () => SetButtonsOnRight(false);
            _buttonsRight.Clicked += () => SetButtonsOnRight(true);

            _displayEditor.Changed += () =>
            {
                if (_loading)
                    return;

                var bounds = DesktopBounds();
                _config.DisplayWidth = _displayEditor.AreaWidth;
                _config.DisplayHeight = _displayEditor.AreaHeight;
                _config.DisplayX = bounds.X + _displayEditor.CenterX;
                _config.DisplayY = bounds.Y + _displayEditor.CenterY;

                // 选区中心落在哪块屏，就把那块屏记成当前显示器
                for (var i = 0; i < _displays.Count; i++)
                {
                    var d = _displays[i];
                    if (_config.DisplayX >= d.X && _config.DisplayX < d.X + d.Width &&
                        _config.DisplayY >= d.Y && _config.DisplayY < d.Y + d.Height)
                    {
                        _config.DisplayDevice = d.Device;
                        break;
                    }
                }

                if (_config.LockAspectRatio)
                {
                    _config.LockTabletToDisplay();
                    SyncTabletEditor();
                }

                Commit();
            };

            _tabletEditor.Changed += () =>
            {
                if (_loading)
                    return;

                _config.TabletWidth = _tabletEditor.AreaWidth;
                _config.TabletHeight = _tabletEditor.AreaHeight;
                _config.TabletX = _tabletEditor.CenterX;
                _config.TabletY = _tabletEditor.CenterY;
                Commit();
            };

            _lockRatio.Clicked += () =>
            {
                _config.LockAspectRatio = !_config.LockAspectRatio;
                if (_config.LockAspectRatio)
                    _config.LockTabletToDisplay();
                UpdatePills();
                SyncTabletEditor();
                Commit();
            };
        }

        private void SetButtonsOnRight(bool onRight)
        {
            _config.ButtonsOnRight = onRight;
            _config.Save();
            _host.ApplyMapping();
            UpdatePills();
        }

        private (double X, double Y, double W, double H) DesktopBounds()
        {
            if (_displays.Count == 0)
                return (0, 0, 1920, 1080);

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            foreach (var d in _displays)
            {
                minX = Math.Min(minX, d.X);
                minY = Math.Min(minY, d.Y);
                maxX = Math.Max(maxX, d.X + d.Width);
                maxY = Math.Max(maxY, d.Y + d.Height);
            }

            return (minX, minY, maxX - minX, maxY - minY);
        }

        private Displays.Info? CurrentDisplay()
        {
            var found = Displays.Find(_config.DisplayDevice);
            if (found != null)
                return found;

            return Displays.Primary() ?? (_displays.Count > 0 ? _displays[0] : null);
        }

        private void SyncEditors()
        {
            _loading = true;

            var bounds = DesktopBounds();
            _displayEditor.SetFull(bounds.W, bounds.H);

            var regions = new List<AreaEditor.Region>();
            var edgesX = new List<double>();
            var edgesY = new List<double>();

            foreach (var display in _displays)
            {
                regions.Add(new AreaEditor.Region
                {
                    X = display.X - bounds.X,
                    Y = display.Y - bounds.Y,
                    Width = display.Width,
                    Height = display.Height,
                    Primary = display.Primary,
                    Label = ShortLabel(display)
                });

                edgesX.Add(display.X - bounds.X);
                edgesX.Add(display.X + display.Width - bounds.X);
                edgesY.Add(display.Y - bounds.Y);
                edgesY.Add(display.Y + display.Height - bounds.Y);
            }

            _displayEditor.SetRegions(regions);
            _displayEditor.SetSnapEdges(edgesX, edgesY);
            _displayEditor.SetArea(
                _config.DisplayWidth,
                _config.DisplayHeight,
                _config.DisplayX - bounds.X,
                _config.DisplayY - bounds.Y);

            SyncTabletEditor();
            _loading = false;
        }

        private void SyncTabletEditor()
        {
            var size = _host.TabletSizeMm;
            _tabletEditor.SetFull(size.X, size.Y);
            _tabletEditor.LockAspect = _config.LockAspectRatio;
            _tabletEditor.AllowResize = !_config.LockAspectRatio;
            _tabletEditor.AspectRatio = _config.DisplayRatio;
            _tabletEditor.SetArea(
                _config.TabletWidth,
                _config.TabletHeight,
                _config.TabletX,
                _config.TabletY);
        }

        private void UpdatePills()
        {
            _lockRatio.IsOn = _config.LockAspectRatio;
            _buttonsLeft.IsOn = !_config.ButtonsOnRight;
            _buttonsRight.IsOn = _config.ButtonsOnRight;
        }

        private void Commit()
        {
            _config.Save();
            _host.ApplyMapping();
        }

        private static string ShortLabel(Displays.Info display)
        {
            var cut = display.Label.IndexOf('·');
            var name = cut > 0 ? display.Label[..cut].Trim() : display.Label;
            return display.Primary ? name + "（主）" : name;
        }

        private Control SectionHeader(string text, int row)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 14,
                Foreground = Palette.InkBrush,
                Margin = new Thickness(0, 0, 0, 8)
            };
            SetRow(block, row);
            SetColumnSpan(block, 2);
            return block;
        }

        private static Control MakeTextButton(string text, Action onClick)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = Palette.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            var button = new Border
            {
                Height = 30,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = Palette.FieldBorderBrush,
                Background = Palette.FieldBrush,
                Padding = new Thickness(12, 0, 12, 0),
                Child = label,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center
            };

            button.PointerPressed += (_, e) =>
            {
                onClick();
                e.Handled = true;
            };

            return button;
        }
    }
}
