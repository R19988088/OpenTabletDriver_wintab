using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 「附加」页 —— 原来自己写的 AutoHotKey 脚本搬进程序里，只保留全局快捷键那几项：
    /// 反向滚动、低延迟计时、Win 键长按、CapsLock 切输入法、任务栏滚轮调音量、
    /// 双击 Shift 复制文件名、Everything 剪贴板防抢、Alt+右键单方向拖动。
    /// 压力相关的全部留在「压力」页，这里不碰。
    /// </summary>
    public sealed class TweaksPage : Grid
    {
        private static readonly (string Label, string Field)[] Rows =
        {
            ("反向（自然）滚动", nameof(Tweaks.InvertScroll)),
            ("低延迟模式（0.5ms 计时）", nameof(Tweaks.TimerResolution)),
            ("Win 键需要长按 1.5 秒", nameof(Tweaks.WinKeyHold)),
            ("CapsLock 切换输入法", nameof(Tweaks.CapsLockIme)),
            ("任务栏滚轮控制音量", nameof(Tweaks.TaskbarVolume)),
            ("双击 Shift 复制文件名", nameof(Tweaks.DoubleShiftCopyName)),
            ("Alt + 右键拖动锁单方向", nameof(Tweaks.PsBrushAxis)),
            ("Everything 不被抢剪贴板", nameof(Tweaks.EverythingGuard))
        };

        private readonly Tweaks _config = HotkeyEngine.Config;
        private readonly TextBlock _status = new();
        private readonly TextBlock _wacomStatus = new();
        private readonly TextBlock _wacomResult = new();
        private bool _wacomBusy;

        public TweaksPage()
        {
            RowDefinitions = new RowDefinitions(
                "Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto," + // 0..11 开关/状态/按钮
                "Auto,Auto,Auto,Auto,*");                                        // 12..15 Wacom 接管 + 余量
            ColumnDefinitions = new ColumnDefinitions("*,Auto");

            for (var i = 0; i < Rows.Length; i++)
            {
                Children.Add(Label(Rows[i].Label, i));
                Children.Add(Switch(Rows[i].Field, i));
            }

            // 第九行：最高权限开机启动（计划任务，登录即管理员、不再弹 UAC）
            Children.Add(Label("最高权限开机启动（无 UAC）", Rows.Length));
            Children.Add(HighStartSwitch(Rows.Length));

            var hint = new TextBlock
            {
                Text = "这些和别的快捷键工具同时开会互相吃掉按键，只留一边比较好。",
                FontSize = 11,
                Foreground = Palette.InkDimBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 14)
            };
            SetRow(hint, Rows.Length + 1);
            SetColumnSpan(hint, 2);
            Children.Add(hint);

            _status.FontSize = 11;
            _status.Foreground = Palette.InkDimBrush;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.Margin = new Thickness(0, 0, 0, 10);
            SetRow(_status, Rows.Length + 2);
            SetColumnSpan(_status, 2);
            Children.Add(_status);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10
            };
            actions.Children.Add(Button("打开自启目录", OpenStartupFolder));
            actions.Children.Add(Button("重新挂载钩子", HotkeyEngine.ReinstallNow));
            actions.Children.Add(Button("结束其它实例", CleanOthers));
            actions.Children.Add(Button("重启程序", RestartApp));

            SetRow(actions, Rows.Length + 3);
            SetColumnSpan(actions, 2);
            Children.Add(actions);

            // ---- Wacom 接管：完整的暂停（服务 + 进程 + Wintab32.dll）与恢复 ----
            var wacomHeader = new TextBlock
            {
                Text = "Wacom 接管（暂停 = 停服务、清进程、换成本程序的 Wintab32.dll）",
                FontSize = 14,
                Foreground = Palette.InkBrush,
                Margin = new Thickness(0, 18, 0, 4)
            };
            SetRow(wacomHeader, 12);
            SetColumnSpan(wacomHeader, 2);
            Children.Add(wacomHeader);

            _wacomStatus.FontSize = 11;
            _wacomStatus.Foreground = Palette.InkDimBrush;
            _wacomStatus.TextWrapping = TextWrapping.Wrap;
            _wacomStatus.Margin = new Thickness(0, 2, 0, 8);
            SetRow(_wacomStatus, 13);
            SetColumnSpan(_wacomStatus, 2);
            Children.Add(_wacomStatus);

            var wacomActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 0, 0, 8)
            };
            wacomActions.Children.Add(WacomButton("接管 Wacom", WacomControl.Takeover));
            wacomActions.Children.Add(WacomButton("恢复 Wacom", WacomControl.Restore));
            SetRow(wacomActions, 14);
            SetColumnSpan(wacomActions, 2);
            Children.Add(wacomActions);

            _wacomResult.FontSize = 11;
            _wacomResult.Foreground = Palette.InkDimBrush;
            _wacomResult.TextWrapping = TextWrapping.Wrap;
            _wacomResult.Margin = new Thickness(0, 0, 0, 8);
            SetRow(_wacomResult, 15);
            SetColumnSpan(_wacomResult, 2);
            Children.Add(_wacomResult);

            UpdateStatus();
            UpdateWacomStatus();

            var tick = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tick.Tick += (_, _) =>
            {
                UpdateStatus();
                if (_ticks % 5 == 0)
                    UpdateWacomStatus();
            };
            tick.Start();
        }

        private int _ticks;
        private string _conflict = string.Empty;

        private Control HighStartSwitch(int row)
        {
            var toggle = new ToggleSwitch
            {
                OnContent = string.Empty,
                OffContent = string.Empty,
                MinWidth = 36,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 4)
            };
            toggle.IsChecked = StartupHigh.Exists();

            toggle.PropertyChanged += (_, e) =>
            {
                if (e.Property != ToggleSwitch.IsCheckedProperty)
                    return;

                var wanted = toggle.IsChecked == true;
                if (wanted)
                {
                    var exe = Environment.ProcessPath;
                    if (exe == null || !StartupHigh.Install(exe))
                    {
                        toggle.IsChecked = false;
                        _status.Text = "创建计划任务失败（需要一次 UAC 授权；被安全软件拦下也会失败）。";
                        _status.Foreground = Palette.AccentBrush;
                    }
                    else
                    {
                        // 计划任务已经负责开机启动，普通自启项要关掉，避免开两份
                        StartupAuto.SetEnabled(false);
                    }
                }
                else
                {
                    StartupHigh.Remove();
                }
            };

            SetRow(toggle, row);
            SetColumn(toggle, 1);
            return toggle;
        }

        /// <summary>接管/恢复都要提权，UAC 会卡住界面线程，所以整个动作放后台跑。</summary>
        private Button WacomButton(string text, Func<string> action)
        {
            var button = new Button
            {
                Content = text,
                FontSize = 12,
                Height = 30,
                Padding = new Thickness(14, 0, 14, 0),
                CornerRadius = new CornerRadius(6),
                Background = Palette.FieldBrush,
                BorderBrush = Palette.FieldBorderBrush,
                BorderThickness = new Thickness(1),
                Foreground = Palette.InkSoftBrush
            };

            button.Click += (_, _) =>
            {
                if (_wacomBusy)
                    return;

                _wacomBusy = true;
                _wacomResult.Text = "正在提权执行（请在系统弹窗里允许）…";
                _wacomResult.Foreground = Palette.AccentBrush;

                System.Threading.Tasks.Task.Run(action).ContinueWith(task => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _wacomBusy = false;
                    _wacomResult.Text = task.Result;
                    _wacomResult.Foreground = task.Result.StartsWith("接管完成") || task.Result.StartsWith("恢复完成")
                        ? Palette.InkDimBrush
                        : Palette.AccentBrush;
                    UpdateWacomStatus();
                }));
            };

            return button;
        }

        /// <summary>服务 Start 要调 reg.exe，5 秒刷一次就够。</summary>
        private void UpdateWacomStatus()
        {
            if (_wacomBusy)
                return;

            try
            {
                _wacomStatus.Text = WacomControl.Status();
            }
            catch
            {
                // 注册表/进程查询失败不该拖垮整页刷新
            }
        }

        private void UpdateStatus()
        {
            // 每 5 秒才扫一次进程，别每秒扫
            if (_ticks++ % 5 == 0)
                _conflict = ConflictText() + HotkeyEngine.OtherInstances();

            var keyboard = HotkeyEngine.KeyboardHooked ? "已挂载" : "未挂载";
            var mouse = HotkeyEngine.MouseHooked ? "已挂载" : "未挂载";

            var text = $"钩子：键盘{keyboard} · 鼠标{mouse} · 事件 {HotkeyEngine.EventCount}";
            text += $" · 滚动{HotkeyEngine.WheelInverted} · 音量{HotkeyEngine.WheelVolume}";
            text += $" · Caps{HotkeyEngine.CapsIme} · Win{HotkeyEngine.WinHeld}";
            text += $" · Shift{HotkeyEngine.ShiftCopied} · 锁轴{HotkeyEngine.AxisLocked}";

            if (HotkeyEngine.EventCount == 0)
                text += Environment.NewLine + "事件一直是 0：钩子没收到任何输入，通常是安全软件拦了全局钩子。";

            if (_conflict.Length > 0)
            {
                text += Environment.NewLine + _conflict;
                _status.Foreground = Palette.AccentBrush;
            }
            else
            {
                _status.Foreground = Palette.InkDimBrush;
            }

            _status.Text = text;
        }

        private static string ConflictText()
        {
            try
            {
                foreach (var process in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        var name = process.ProcessName;
                        if (name.Contains("AutoHotkey", StringComparison.OrdinalIgnoreCase))
                            return $"检测到 {name} 正在运行（PID {process.Id}）：两套全局钩子会互相吞事件，先退出它再试。";
                    }
                    catch
                    {
                        // 有的进程查不到名字，跳过
                    }
                }
            }
            catch
            {
                // 扫进程失败就不提示
            }

            return string.Empty;
        }

        private Control Label(string text, int row)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = Palette.InkSoftBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 7, 16, 7)
            };
            SetRow(label, row);
            SetColumn(label, 0);
            return label;
        }

        private Control Switch(string property, int row)
        {
            var toggle = new ToggleSwitch
            {
                OnContent = string.Empty,
                OffContent = string.Empty,
                MinWidth = 36,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 4)
            };
            toggle.IsChecked = ReadSwitch(property);

            toggle.PropertyChanged += (_, e) =>
            {
                if (e.Property != ToggleSwitch.IsCheckedProperty)
                    return;

                WriteSwitch(property, toggle.IsChecked == true);
            };

            SetRow(toggle, row);
            SetColumn(toggle, 1);
            return toggle;
        }

        private bool ReadSwitch(string property) => property switch
        {
            nameof(Tweaks.InvertScroll) => _config.InvertScroll,
            nameof(Tweaks.TimerResolution) => _config.TimerResolution,
            nameof(Tweaks.WinKeyHold) => _config.WinKeyHold,
            nameof(Tweaks.CapsLockIme) => _config.CapsLockIme,
            nameof(Tweaks.TaskbarVolume) => _config.TaskbarVolume,
            nameof(Tweaks.DoubleShiftCopyName) => _config.DoubleShiftCopyName,
            nameof(Tweaks.PsBrushAxis) => _config.PsBrushAxis,
            _ => _config.EverythingGuard
        };

        private void WriteSwitch(string property, bool value)
        {
            switch (property)
            {
                case nameof(Tweaks.InvertScroll):
                    _config.InvertScroll = value;
                    break;
                case nameof(Tweaks.TimerResolution):
                    _config.TimerResolution = value;
                    break;
                case nameof(Tweaks.WinKeyHold):
                    _config.WinKeyHold = value;
                    break;
                case nameof(Tweaks.CapsLockIme):
                    _config.CapsLockIme = value;
                    break;
                case nameof(Tweaks.TaskbarVolume):
                    _config.TaskbarVolume = value;
                    break;
                case nameof(Tweaks.DoubleShiftCopyName):
                    _config.DoubleShiftCopyName = value;
                    break;
                case nameof(Tweaks.PsBrushAxis):
                    _config.PsBrushAxis = value;
                    break;
                default:
                    _config.EverythingGuard = value;
                    break;
            }

            _config.Save();
            HotkeyEngine.Apply(_config);
        }

        private static Button Button(string text, Action action)
        {
            var button = new Button
            {
                Content = text,
                FontSize = 12,
                Height = 30,
                Padding = new Thickness(14, 0, 14, 0),
                CornerRadius = new CornerRadius(6),
                Background = Palette.FieldBrush,
                BorderBrush = Palette.FieldBorderBrush,
                BorderThickness = new Thickness(1),
                Foreground = Palette.InkSoftBrush
            };
            button.Click += (_, _) => action();
            return button;
        }

        private static void OpenStartupFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:startup")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // 打不开就算了
            }
        }

        private static int CountOthers()
        {
            var count = 0;
            try
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("OpenTabletDriver.Slim"))
                {
                    if (process.Id != Environment.ProcessId)
                        count++;
                }
            }
            catch
            {
                // 数不出来就当 0
            }

            return count;
        }

        /// <summary>先按当前权限结束一遍；还剩的说明权限不够，再弹一次 UAC 用管理员权限清理。</summary>
        private static void CleanOthers()
        {
            Instances.KillOthers();
            if (CountOthers() == 0)
                return;

            try
            {
                var exe = Environment.ProcessPath;
                if (exe == null)
                    return;

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--killothers")
                {
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch
            {
                // 用户取消了 UAC 就算了
            }
        }

        private static void RestartApp()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (exe == null)
                    return;

                // 让新进程等我们彻底退出再去占数位板，否则它会抢不到设备（表现就是笔不能动）
                Process.Start(new ProcessStartInfo(exe, $"--page=附加 --wait-pid={Environment.ProcessId}")
                {
                    UseShellExecute = true
                });
                // 硬退出：Shutdown 可能卡在设备释放里，旧进程不退新进程就抢不到数位板
                Environment.Exit(0);
            }
            catch
            {
                // 重启失败就留在原进程
            }
        }
    }
}
