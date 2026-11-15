using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Newtonsoft.Json.Linq;
using OpenTabletDriver.Desktop;
using OpenTabletDriver.Desktop.Interop;
using OpenTabletDriver.Desktop.Reflection;
using OpenTabletDriver.Interop;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Logging;
using OpenTabletDriver.Plugin.Tablet;
using OpenTabletDriver.UX.Controls;

namespace OpenTabletDriver.UX
{
    public class MainForm : DesktopForm
    {
        public MainForm()
        {
            this.DataContext = App.Current;

            // Call InitializeForm on ctor since DesktopForm.Show() won't be called on binary launch
            InitializeForm();
            InitializePlatform();

            SetTitle();
            base.Menu = ConstructLimitedMenu();
            fullMenu = ConstructMenu();

            base.Content = placeholder;

            trayIcon?.Indicator.Show();

            saveButton = new Button(async (s, e) => await SaveSettings())
            {
                Text = L.T("Save")
            };

            applyButton = new Button(async (s, e) => await ApplySettings())
            {
                Text = L.T("Apply")
            };

            // Input mode lives beside the tablet switcher: Ink needs no driver at all,
            // WinTab only works while the shim sits in System32, so selecting it offers
            // to install when the installed copy is missing or out of date.
            modeSelector = new DropDown { Width = 160 };
            modeSelector.Items.Add(new ListItem { Text = "Windows Ink" });
            modeSelector.Items.Add(new ListItem { Text = "WinTab" });
            modeSelector.SelectedIndexChanged += (sender, e) =>
            {
                if (syncingMode) return;
                if (modeSelector.SelectedIndex == WIN_TAB_INDEX)
                    RequestWinTab();
                else
                    ApplyInputMode("winInk");
            };
            modeLabel = new Label { Text = "模式", VerticalAlignment = VerticalAlignment.Center };

            // Start-with-Windows sits at the same strip as the mode selector. A fresh
            // copy ships it checked: nothing works until OTD is running, and a portable
            // folder is easy to forget after a reboot.
            autoStartCheck = new CheckBox
            {
                Text = "开机启动",
                Checked = AutoStartPreferenceEnabled()
            };
            autoStartCheck.CheckedChanged += (sender, e) =>
            {
                ApplyAutoStart(autoStartCheck.Checked ?? false);
            };

            // Keep the Run entry pointing at wherever this copy lives now: moving or
            // renaming the folder must not leave a stale path registered.
            if (autoStartCheck.Checked ?? false)
                ApplyAutoStart(true);

            var inputMode = FeelConfig.Load().InputMode;
            syncingMode = true;
            modeSelector.SelectedIndex = inputMode == "winTab" ? WIN_TAB_INDEX : INK_INDEX;
            syncingMode = false;

            App.Driver.Connected += HandleDaemonConnected;
            App.Driver.Disconnected += HandleDaemonDisconnected;

            Application.Instance.InvokeAsync(ConnectToDaemon);
        }

        private async Task ConnectToDaemon()
        {
            try
            {
                while (true)
                {
                    var timeout = Task.Delay(TimeSpan.FromSeconds(15));
                    var result = await Task.WhenAny(App.Driver.Connect(), timeout);

                    if (result != timeout || App.Driver.IsConnected)
                        break; // daemon connected

                    var message = SystemInterop.CurrentPlatform switch
                    {
                        PluginPlatform.Windows =>
                            "Connecting to daemon has timed out.\nVerify that OpenTabletDriver.Daemon is running or is in the same folder as OpenTabletDriver.UX\nPress OK to retry",
                        PluginPlatform.Linux =>
                            """
                            Connecting to daemon has timed out.
                            Verify that OpenTabletDriver.Daemon is running, e.g. by starting the systemd user service (usually 'systemctl --user start opentabletdriver'), or by starting 'otd-daemon'.
                            Press OK to retry
                            """,
                        _ =>
                            "Connecting to daemon has timed out. Verify that OpenTabletDriver.Daemon is running.\nPress OK to retry"
                    };

                    var dialogResult = MessageBox.Show(this, message, L.T("Daemon Connection Error"),
                        MessageBoxButtons.OKCancel, MessageBoxType.Error);

                    if (App.Driver.IsConnected) break;

                    if (dialogResult == DialogResult.Cancel)
                        Environment.Exit(1);

                    if (App.EnableDaemonWatchdog)
                        StartDaemonWatchdog();
                }

                if (!this.SkipUpdate)
                    CheckForUpdates();
            }
            catch (Exception ex)
            {
                ex.ShowMessageBox();
                Environment.Exit(2);
            }
        }

        private const int DEFAULT_CLIENT_WIDTH = 960;
        private const int DEFAULT_CLIENT_HEIGHT = 760;

        private readonly MenuBar fullMenu;
        private readonly Placeholder placeholder = new()
        {
            Text = L.T("Connecting to OpenTabletDriver Daemon..."),
        };

        private TrayIcon? trayIcon;

        public bool SilenceDaemonShutdown { get; set; }
        public bool SkipUpdate { get; set; }

        protected override void InitializeForm()
        {
            var bounds = Screen.FromPoint(Mouse.Position).Bounds;

            if (this.WindowState != WindowState.Maximized)
            {
                var minWidth = Math.Min(DEFAULT_CLIENT_WIDTH, bounds.Width * 0.95);
                var minHeight = Math.Min(DEFAULT_CLIENT_HEIGHT, bounds.Height * 0.95);

                this.Size = new Size((int)minWidth, (int)minHeight);

                if (SystemInterop.CurrentPlatform == PluginPlatform.Windows)
                {
                    var x = Screen.WorkingArea.Center.X - (minWidth / 2);
                    var y = Screen.WorkingArea.Center.Y - (minHeight / 2);
                    this.Location = new Point((int)x, (int)y);
                }
            }
        }

        protected void InitializePlatform()
        {
            switch (SystemInterop.CurrentPlatform)
            {
                case PluginPlatform.Windows:
                    var programPath = AppInfo.ProgramDirectory;
                    var tempPath = Regex.Escape(Path.GetTempPath());
                    var regex = new Regex(@$"^{tempPath}Temp\d+.*?\.zip");

                    if (regex.IsMatch(programPath))
                    {
                        MessageBox.Show(this, $"You are running OpenTabletDriver.UX from a zip file.\n\nPlease extract the zip file to a folder then run OpenTabletDriver.UX from there.", "Error", MessageBoxType.Error);
                        Environment.Exit(1);
                    }
                    break;
                case PluginPlatform.MacOS:
                    this.Padding = 10;
                    break;
            }

            if (App.EnableTrayIcon)
            {
                trayIcon = new TrayIcon(this);
                if (WindowState == WindowState.Minimized)
                {
                    this.Visible = false;
                    this.ShowInTaskbar = false;
                }
                this.WindowStateChanged += (sender, e) =>
                {
                    switch (this.WindowState)
                    {
                        case WindowState.Normal:
                        case WindowState.Maximized:
                            this.Visible = true;
                            this.ShowInTaskbar = true;
                            break;
                        case WindowState.Minimized:
                            this.Visible = false;
                            this.ShowInTaskbar = false;
                            break;
                    }
                };
                Application.Instance.Terminating += (sender, e) => trayIcon.Dispose();
            }

            if (App.EnableDaemonWatchdog)
            {
                // Check if daemon is already active, if not then start it as a subprocess if it exists in the local path.
                StartDaemonWatchdog();

                AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
                {
                    App.DaemonWatchdog?.Dispose();
                    App.DaemonWatchdog = null;
                };

                this.Closing += (sender, e) =>
                {
                    App.DaemonWatchdog?.Dispose();
                    App.DaemonWatchdog = null;
                };
            }
        }

        private static void StartDaemonWatchdog()
        {
            if (Instance.Exists("OpenTabletDriver.Daemon") || !DaemonWatchdog.CanExecute) return;

            var watchdog = new DaemonWatchdog();
            watchdog.Start();
            App.DaemonWatchdog = watchdog;
        }

        private MenuBar ConstructLimitedMenu()
        {
            var quitCommand = new Command { MenuText = L.T("Quit"), Shortcut = Application.Instance.CommonModifier | Keys.Q };
            quitCommand.Executed += (sender, e) => Quit();

            var aboutCommand = new Command { MenuText = L.T("About..."), Shortcut = Keys.F1 };
            aboutCommand.Executed += (sender, e) => App.Current.AboutWindow.Show();

            var wikiUrl = new Command { MenuText = L.T("Open Wiki...") };
            wikiUrl.Executed += (sender, e) => DesktopInterop.Open(App.WikiUrl);

            var menuBar = new MenuBar
            {
                Items =
                {
                    new ButtonMenuItem
                    {
                        Text = L.T("&Help"),
                        Items =
                        {
                            wikiUrl,
                        }
                    }
                },
                QuitItem = quitCommand,
                AboutItem = aboutCommand
            };

            return menuBar;
        }

        private MenuBar ConstructMenu()
        {
            var quitCommand = new Command { MenuText = L.T("Quit"), Shortcut = Application.Instance.CommonModifier | Keys.Q };
            quitCommand.Executed += (sender, e) => Quit();

            var aboutCommand = new Command { MenuText = L.T("About..."), Shortcut = Keys.F1 };
            aboutCommand.Executed += (sender, e) => App.Current.AboutWindow.Show();

            var resetSettings = new Command { MenuText = L.T("Reset to defaults") };
            resetSettings.Executed += async (sender, e) => await ResetSettingsDialog();

            var loadSettings = new Command { MenuText = L.T("Load settings..."), Shortcut = Application.Instance.CommonModifier | Keys.O };
            loadSettings.Executed += async (sender, e) => await LoadSettingsDialog();

            var saveSettingsAs = new Command { MenuText = L.T("Save settings as..."), Shortcut = Application.Instance.CommonModifier | Keys.Shift | Keys.S };
            saveSettingsAs.Executed += async (sender, e) => await SaveSettingsDialog();

            var saveSettings = new Command { MenuText = L.T("Save settings"), Shortcut = Application.Instance.CommonModifier | Keys.S };
            saveSettings.Executed += async (sender, e) => await SaveSettings();

            var applySettings = new Command { MenuText = L.T("Apply settings"), Shortcut = Application.Instance.CommonModifier | Keys.Enter };
            applySettings.Executed += async (sender, e) => await ApplySettings();

            var refreshPresets = new Command { MenuText = L.T("Refresh presets") };
            refreshPresets.Executed += async (sender, e) => await RefreshPresets();

            var savePreset = new Command { MenuText = L.T("Save as preset...") };
            savePreset.Executed += async (sender, e) => await SavePresetDialog();

            var openPresetsDirectory = new Command { MenuText = L.T("Open presets directory...") };
            openPresetsDirectory.Executed += async (sender, e) => DesktopInterop.OpenFolder(AppInfo.Current.PresetDirectory);

            var detectTablet = new Command { MenuText = L.T("Detect tablet"), Shortcut = Application.Instance.CommonModifier | Keys.D };
            detectTablet.Executed += async (sender, e) =>
            {
                AppInfo.Current.ConfigurationDirectory = null; // force recheck on next access
                await DetectTablet();
            };

            var showTabletDebugger = new Command { MenuText = L.T("Tablet debugger...") };
            showTabletDebugger.Executed += (sender, e) => App.Current.DebuggerWindow.Show();

            var deviceStringReader = new Command { MenuText = L.T("Device string reader...") };
            deviceStringReader.Executed += (sender, e) => App.Current.StringReaderWindow.Show();

            var deviceInitSender = new Command { MenuText = L.T("Device init sender...") };
            deviceInitSender.Executed += (sender, e) => App.Current.InitSenderWindow.Show();

            var pluginManager = new Command { MenuText = L.T("FullMenu.Open Plugin Manager...") };
            pluginManager.Executed += (sender, e) => App.Current.PluginManagerWindow.Show();

            var wikiUrl = new Command { MenuText = L.T("Open Wiki...") };
            wikiUrl.Executed += (sender, e) => DesktopInterop.Open(App.WikiUrl);

            var showGuide = new Command { MenuText = L.T("FullMenu.Show guide...") };
            showGuide.Executed += (sender, e) => App.Current.StartupGreeterWindow.Show();

            var exportDiagnostics = new Command { MenuText = L.T("FullMenu.Export diagnostics...") };
            exportDiagnostics.Executed += async (sender, e) => await ExportDiagnostics();

            var exportDiagnosticsToClipboard = new Command { MenuText = L.T("FullMenu.Export diagnostics to Clipboard...") };
            exportDiagnosticsToClipboard.Executed += async (sender, e) => await ExportDiagnosticsToClipboard();

            var updater = new Command { MenuText = L.T("FullMenu.Check for updates...") };
            updater.Executed += (sender, e) => App.Current.UpdaterWindow.Show();

            var languageMenu = new ButtonMenuItem { Text = L.T("Language") };
            languageMenu.Items.Add(CreateLanguageItem("en", "English"));
            languageMenu.Items.Add(CreateLanguageItem("zh-CN", "简体中文"));
            languageMenu.Items.Add(CreateLanguageItem("ja-JP", "日本語"));

            var menuBar = new MenuBar
            {
                Items =
                {
                    // File submenu
                    new ButtonMenuItem
                    {
                        Text = L.T("&File"),
                        Items =
                        {
                            loadSettings,
                            saveSettings,
                            saveSettingsAs,
                            resetSettings,
                            applySettings,
                            new SeparatorMenuItem(),
                            savePreset,
                            refreshPresets,
                            openPresetsDirectory,
                            new ButtonMenuItem
                            {
                                Text = L.T("Presets"),
                                Items =
                                {
                                    new ButtonMenuItem
                                    {
                                        Text = L.T("No presets loaded"),
                                        Enabled = false
                                    }
                                }
                            }
                        }
                    },
                    // Tablets submenu
                    new ButtonMenuItem
                    {
                        Text = L.T("Tablets"),
                        Items =
                        {
                            detectTablet,
                            showTabletDebugger,
                            deviceStringReader,
                            deviceInitSender,
                        }
                    },
                    // Plugins submenu
                    new ButtonMenuItem
                    {
                        Text = L.T("Plugins"),
                        Items =
                        {
                            pluginManager
                        }
                    },
                    new ButtonMenuItem
                    {
                        Text = L.T("&Help"),
                        Items =
                        {
                            wikiUrl,
                            exportDiagnostics,
                            exportDiagnosticsToClipboard,
                            showGuide,
                            new SeparatorMenuItem(),
                            languageMenu
                        }
                    }
                },
                ApplicationItems =
                {
                    // application (OS X) or file menu (others)
                },
                QuitItem = quitCommand,
                AboutItem = aboutCommand
            };

            switch (SystemInterop.CurrentPlatform)
            {
                case PluginPlatform.Windows:
                case PluginPlatform.MacOS:
                {
                    menuBar.Items.GetSubmenu(L.T("&Help")).Items.Add(updater);
                    break;
                }
            }

            return menuBar;
        }

        private static Command CreateLanguageItem(string language, string name)
        {
            var command = new Command { MenuText = name };
            command.Executed += (_, _) =>
            {
                if (LanguagePreferences.Current == language)
                    return;

                LanguagePreferences.Save(language);
                MessageBox.Show(
                    L.T("Restart the application for the new language to take effect."),
                    L.T("Language changed"),
                    MessageBoxButtons.OK,
                    MessageBoxType.Information);
            };
            return command;
        }

        private void SetTitle(IEnumerable<TabletReference>? tablets = null)
        {
            string prefix = $"OpenTabletDriver v{App.Version}";
            string affix = string.Empty;

            if (tablets?.Any() ?? false)
            {
                // Limit to 3 tablets in the title
                int numTablets = Math.Min(tablets.Count(), 3);
                affix = string.Join(", ", tablets.Take(numTablets).Select(t => t.Properties.Name));
            }

            this.Title = !string.IsNullOrEmpty(affix)
                ? $"{prefix} - {affix}"
                : prefix;
        }

        // ReSharper disable once AsyncVoidMethod
        private void HandleDaemonConnected(object? sender, EventArgs e) => Application.Instance.AsyncInvoke(async void () =>
        {
            Debug.Assert(App.Driver.IsConnected);
            // Hook events after the instance is (re)instantiated
            Log.Output += LogToDriver;
            App.Driver.TabletsChanged += (sender, tablet) => SetTitle(tablet);

            // Load full menu
            this.Menu = fullMenu;

            // Load the application information from the daemon
            AppInfo.Current = await App.Driver.Instance.GetApplicationInfo();

            AppInfo.PluginManager = new DesktopPluginManager();
            AppInfo.PresetManager = new PresetManager();

            // Load any new plugins
            AppInfo.PluginManager.Load();

            // Show the startup greeter
            if (!File.Exists(AppInfo.Current.SettingsFile) && this.WindowState != WindowState.Minimized)
                App.Current.StartupGreeterWindow.Show();

            // Synchronize settings
            await SyncSettings();
            App.Driver.Resynchronize += async (sender, e) => await SyncSettings();

            // Set window content
            base.Content = tabletSwitcherPanel = new TabletSwitcherPanel
            {
                CommandsControl = new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalContentAlignment = HorizontalAlignment.Right,
                    Spacing = 5,
                    Items =
                    {
                        modeLabel,
                        modeSelector,
                        saveButton,
                        applyButton,
                        autoStartCheck,
                    }
                }
            };

            // Update preset options in File menu and tray icon
            await RefreshPresets();

            // Reflect the saved mode on the feel group before anything is drawn
            SyncFeelAvailability(FeelConfig.Load().InputMode);

            // Update title to new instance
            if (await App.Driver.Instance.GetTablets() is IEnumerable<TabletReference> tablets)
                SetTitle(tablets);

            // Having a mismatch between the version of the daemon and the application will cause issues
            MatchDaemonVersion();
        });

        private Button saveButton;
        private Button applyButton;
        private Label modeLabel;
        private DropDown modeSelector;
        private CheckBox autoStartCheck;
        private TabletSwitcherPanel tabletSwitcherPanel;
        private bool syncingMode;

        private const int INK_INDEX = 0;
        private const int WIN_TAB_INDEX = 1;

        private void ApplyInputMode(string mode)
        {
            var config = FeelConfig.Load();
            config.InputMode = mode;
            config.Save();

            // Read it back: a failed write used to be swallowed and the switch then looked
            // like it did nothing at all.
            if (FeelConfig.Load().InputMode != mode)
            {
                MessageBox.Show(
                    $"写入 {Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OTDWintab", "feel.json")} 失败，模式没有生效。",
                    MessageBoxType.Error);
                return;
            }

            SyncFeelAvailability(mode);
        }

        /// <summary>
        /// Feel values are applied by the shim, which only runs on the WinTab path, so the
        /// whole feel group is unusable in Ink mode.
        /// </summary>
        private void SyncFeelAvailability(string mode)
        {
            var feel = tabletSwitcherPanel?.ControlPanel?.FeelGroup;
            if (feel != null)
                feel.SetModeEnabled(mode == "winTab");
        }

        /// <summary>
        /// WinTab is only useful with the shim inside System32, so check the installed copy
        /// first and offer to copy the bundled one when it is missing or a different build.
        /// </summary>
        private void RequestWinTab()
        {
            var source = ResolveBundledShim();
            if (source == null)
            {
                MessageBox.Show("找不到随附的 Wintab32.dll，无法安装 WinTab。", MessageBoxType.Error);
                RevertModeSelection();
                return;
            }

            var target = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Wintab32.dll");
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length)
            {
                ApplyInputMode("winTab");
                return;
            }

            var result = MessageBox.Show(
                "WinTab 需要把 Wintab32.dll 放进 System32，现在安装吗？（会弹出管理员权限提示）",
                MessageBoxButtons.YesNo,
                MessageBoxType.Question);

            if (result != DialogResult.Yes)
            {
                RevertModeSelection();
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    var info = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c copy /y \"{source}\" \"{target}\"",
                        Verb = "runas",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using var process = Process.Start(info);
                    process?.WaitForExit();

                    var installed = File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length;
                    Application.Instance.AsyncInvoke(() =>
                    {
                        if (installed)
                        {
                            ApplyInputMode("winTab");
                            MessageBox.Show("已安装 Wintab32.dll 到 System32。正在运行的程序需要重启才能加载它。",
                                "安装完成", MessageBoxType.Information);
                        }
                        else
                        {
                            RevertModeSelection();
                            MessageBox.Show("安装没有完成，已保持 Windows Ink。", "安装失败", MessageBoxType.Error);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Application.Instance.AsyncInvoke(() =>
                    {
                        RevertModeSelection();
                        MessageBox.Show($"安装被取消或失败：{ex.Message}", "安装失败", MessageBoxType.Error);
                    });
                }
            });
        }

        private void RevertModeSelection()
        {
            syncingMode = true;
            modeSelector.SelectedIndex = INK_INDEX;
            syncingMode = false;
        }

        private static string? ResolveBundledShim()
        {
            var bundled = Path.Join(AppInfo.ProgramDirectory, "WintabNative", "Wintab32.dll");
            if (File.Exists(bundled))
                return bundled;
            var adjacent = Path.Join(AppInfo.ProgramDirectory, "Wintab32.dll");
            return File.Exists(adjacent) ? adjacent : null;
        }

        /// <summary>Leaves the application for real; closing the window only hides it.</summary>
        public void Quit()
        {
            allowClose = true;
            Application.Instance.Quit();
        }

        private bool allowClose;

        private static async void LogToDriver(object? sender, LogMessage message)
        {
            if (App.Driver.IsConnected)
                await App.Driver.Instance.WriteMessage(message);
        }

        private void HandleDaemonDisconnected(object? sender, EventArgs e)
        {
            Log.Output -= LogToDriver;
            if (SilenceDaemonShutdown)
                return;

            // Hide all controls until reconnected
            Application.Instance.Invoke(() =>
            {
                base.Content = placeholder;
                base.Menu = null;
                SetTitle(); // reset title to avoid last connected tablets from showing up

                Application.Instance.InvokeAsync(ConnectToDaemon).ConfigureAwait(false);
            });
        }

        private static async Task ResetSettings()
        {
            Debug.Assert(App.Driver.IsConnected);
            await App.Driver.Instance.ResetSettings();
            await SyncSettings();
        }

        private static async Task ResetSettingsDialog()
        {
            if (MessageBox.Show(L.T("Reset settings to default?"), L.T("Reset to defaults"), MessageBoxButtons.OKCancel, MessageBoxType.Question) == DialogResult.Ok)
                await ResetSettings();
        }

        private static async Task SyncSettings()
        {
            Debug.Assert(App.Driver.IsConnected);
            App.Current.Settings = await App.Driver.Instance.GetSettings();
        }

        private async Task LoadSettingsDialog()
        {
            var fileDialog = Extensions.OpenFileDialog(
                "Load OpenTabletDriver settings...",
                Eto.EtoEnvironment.GetFolderPath(Eto.EtoSpecialFolder.Documents),
                [new FileFilter("OpenTabletDriver Settings (*.json)", ".json")]
            );

            switch (fileDialog.ShowDialog(this))
            {
                case DialogResult.Ok:
                case DialogResult.Yes:
                    var file = new FileInfo(fileDialog.FileName);
                    if (file.Exists)
                    {
                        if (Settings.TryDeserialize(file, out var settings))
                        {
                            App.Current.Settings = settings;
                            await App.Driver.Instance!.SetSettings(settings);
                        }
                        else
                        {
                            MessageBox.Show(
                                L.T("Invalid settings file."),
                                MessageBoxType.Error
                            );
                        }
                    }
                    break;
            }
        }

        private async Task SaveSettingsDialog()
        {
            var fileDialog = Extensions.SaveFileDialog(
                "Save OpenTabletDriver settings...",
                Eto.EtoEnvironment.GetFolderPath(Eto.EtoSpecialFolder.Documents),
                [new FileFilter("OpenTabletDriver Settings (*.json)", ".json")],
                "opentabletdriver-settings.json"
            );

            switch (fileDialog.ShowDialog(this))
            {
                case DialogResult.Ok:
                case DialogResult.Yes:
                    var file = new FileInfo(fileDialog.FileName);
                    if (App.Current.Settings is Settings settings)
                    {
                        settings.Serialize(file);
                        await ApplySettings();
                    }
                    break;
            }
        }

        private async Task SaveSettings()
        {
            DisableApplySaveButtons();

            Debug.Assert(App.Driver.IsConnected, "Save should be disabled when no driver is connected");

            if (App.Current.Settings is Settings settings)
            {
                if (settings.Profiles.Any(p => p.AbsoluteModeSettings?.Tablet.Width + p.AbsoluteModeSettings?.Tablet.Height == 0))
                {
                    var result = MessageBox.Show(
                        "Warning: Your tablet area is invalid. Saving this configuration may cause problems." + Environment.NewLine +
                        "Are you sure you want to save your configuration?",
                        MessageBoxButtons.YesNo,
                        MessageBoxType.Warning
                    );
                    if (result != DialogResult.Yes)
                        return;
                }

                var appInfo = await App.Driver.Instance.GetApplicationInfo();
                settings.Serialize(new FileInfo(appInfo.SettingsFile));
                await ApplySettings();
            }
        }

        private CancellationTokenSource _disableApplySaveButtons = new();

        private void DisableApplySaveButtons(bool disableSave = true)
        {
            Application.Instance.InvokeAsync(async () =>
            {
                await _disableApplySaveButtons.CancelAsync();
                _disableApplySaveButtons = new CancellationTokenSource();
                if (disableSave)
                    saveButton.Enabled = false;

                applyButton.Enabled = false;

                // debounce re-enabling task
                await Task.Delay(1000, _disableApplySaveButtons.Token).ContinueWith(task =>
                {
                    if (task.IsCanceled) return;

                    saveButton.Enabled = true;
                    applyButton.Enabled = true;
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }).ConfigureAwait(false);
        }

        private async Task ApplySettings()
        {
            DisableApplySaveButtons(false);

            Debug.Assert(App.Driver.IsConnected, "Apply should be disabled when no driver is connected");

            try
            {
                if (App.Current.Settings is Settings settings)
                    await App.Driver.Instance.SetSettings(settings);
            }
            catch (StreamJsonRpc.RemoteInvocationException riex) when (riex.ErrorData is JObject err)
            {
                var type = (string)err["type"]!;
                var message = (string)err["message"]!;
                var stack = (string)err["stack"]!;
                var logMessage = new LogMessage
                {
                    Group = type,
                    Message = message,
                    StackTrace = stack
                };
                Log.Write(logMessage);
            }
        }

        private static void LoadPresets() => AppInfo.PresetManager.Refresh();

        private Task RefreshPresets()
        {
            LoadPresets();

            if (trayIcon != null) // Check non-Linux
                trayIcon.RefreshMenuItems();

            // Update File submenu
            var presets = AppInfo.PresetManager.GetPresets();
            var presetsMenu = fullMenu.Items.GetSubmenu(L.T("&File")).Items.GetSubmenu(L.T("Presets")) as ButtonMenuItem;
            presetsMenu.Items.Clear();

            if (presets.Count != 0)
            {
                foreach (var preset in presets)
                {
                    var presetItem = new ButtonMenuItem
                    {
                        Text = preset.Name
                    };
                    presetItem.Click += PresetButtonHandler;

                    presetsMenu.Items.Add(presetItem);
                }
            }
            else
            {
                var emptyPresetsItem = new ButtonMenuItem
                {
                    Text = L.T("No presets loaded"),
                    Enabled = false
                };

                presetsMenu.Items.Add(emptyPresetsItem);
            }

            return Task.CompletedTask;
        }

        private async Task SavePresetDialog()
        {
            // TODO: this should probably use a modal dialog instead, as presets are only readable from the Preset directory
            var fileDialog = Extensions.SaveFileDialog(
                "Save OpenTabletDriver settings as preset...",
                AppInfo.Current.PresetDirectory,
                [new FileFilter("OpenTabletDriver Settings (*.json)", ".json")],
                "mypreset.json"
            );

            switch (fileDialog.ShowDialog(this))
            {
                case DialogResult.Ok:
                case DialogResult.Yes:
                    var file = new FileInfo(fileDialog.FileName + (fileDialog.FileName.EndsWith(".json") ? "" : ".json"));
                    if (App.Current.Settings is Settings settings)
                        settings.Serialize(file);
                    await RefreshPresets();
                    break;
            }
        }

        public static void PresetButtonHandler(object? sender, EventArgs e)
        {
            var buttonMenuItem = sender as ButtonMenuItem;
            Debug.Assert(buttonMenuItem != null, "Invalid sender");
            Debug.Assert(App.Driver.IsConnected, "Preset buttons should not be available when daemon isn't connected");

            var presetName = buttonMenuItem.Text;
            var preset = AppInfo.PresetManager.FindPreset(presetName);
            Debug.Assert(preset != null, "It should be impossible to select a preset that doesn't exist");

            App.Current.Settings = preset.Settings;
            App.Driver.Instance.SetSettings(App.Current.Settings);
            Log.Write("Settings", $"Applied preset '{preset.Name}'");
        }

        private static async Task DetectTablet()
        {
            Debug.Assert(App.Driver.IsConnected, "It should not be possible to request tablet detection when daemon isn't connected");
            await App.Driver.Instance.DetectTablets();
            await App.Driver.Instance.SetSettings(await App.Driver.Instance.GetSettings());
        }

        private readonly string _diagnosticsPrefix = $"Diagnostics-{App.Version.Replace(".", "")}";

        private async Task ExportDiagnostics()
        {
            Debug.Assert(App.Driver.IsConnected, "It should not be possible to export diagnostics without a connected daemon");

            try
            {
                var diagnosticDump = await App.Driver.Instance.GetDiagnosticInfo();

                var tablets = await App.Driver.Instance.GetTablets();
                var tabletReferences = tablets as TabletReference[] ?? tablets.ToArray();
                string tabletNames = tabletReferences.Length != 0
                    ? " " + string.Join(", ", tabletReferences.Select(x => x.Properties.Name))
                    : string.Empty;

                var fileDialog = Extensions.SaveFileDialog(
                    "Save diagnostic information to...",
                    Eto.EtoEnvironment.GetFolderPath(Eto.EtoSpecialFolder.Documents),
                    [new FileFilter("Diagnostic information", ".json")],
                    $"{_diagnosticsPrefix}{tabletNames}.json"
                );

                switch (fileDialog.ShowDialog(this))
                {
                    case DialogResult.Ok:
                    case DialogResult.Yes:
                        string[] options = [".json", ".txt", ".log"];
                        var file = new FileInfo(fileDialog.FileName + (options.Any(fileDialog.FileName.EndsWith) ? "" : ".json"));
                        if (file.Exists)
                            file.Delete();

                        await using (var fs = file.OpenWrite())
                        await using (var sw = new StreamWriter(fs))
                            await sw.WriteLineAsync(diagnosticDump.ToString());
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                ex.ShowMessageBox();
            }
        }
        private static async Task ExportDiagnosticsToClipboard()
        {
            Debug.Assert(App.Driver.IsConnected, "It should not be possible to export diagnostics without a connected daemon");

            try
            {
                var diagnosticDump = await App.Driver.Instance.GetDiagnosticInfo();

                Clipboard.Instance.Clear();
                Clipboard.Instance.Text = diagnosticDump.ToString();
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                ex.ShowMessageBox();
            }
        }

        private static void MatchDaemonVersion()
        {
            Debug.Assert(App.Driver.IsConnected, "It should not be possible to fetch the daemon version without a connected daemon");

            var daemonVersionString = AppInfo.Current.Version?.ToString() ?? "Outdated";
            var versionMatch = AppInfo.Current.Version != null && AppInfo.Current.Version == App.AssemblyVersion;

            if (versionMatch == false)
            {
                var specificsTip = "You may need to restart the daemon.";

                if (App.DaemonWatchdog != null || App.AssemblyVersion < AppInfo.Current.Version)
                    specificsTip = "You may need to restart the UX.";
                else if (OperatingSystem.IsLinux())
                    specificsTip = "You may need to restart the user service or edit symlinks.";

                Log.WriteNotify("UX", $"Daemon version ({daemonVersionString}) does not match the UX version ({App.AssemblyVersion})." +
                                        Environment.NewLine + specificsTip, LogLevel.Warning);
            }
        }

        private static void CheckForUpdates()
        {
            // ReSharper disable once AsyncVoidMethod
            Application.Instance.AsyncInvoke(async void () =>
            {
                if (await App.Current.UpdaterWindow.GetWindow().HasUpdates())
                {
                    App.Current.UpdaterWindow.Show();
                }
            });
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // The close button sends the window to the tray instead of ending the session:
            // the daemon keeps the tablet mapped and the shim keeps feeding applications.
            // Quitting goes through Quit() (menu, tray menu) which allows the close.
            if (App.EnableTrayIcon && !allowClose)
            {
                e.Cancel = true;
                this.WindowState = WindowState.Minimized;
                return;
            }

            App.Driver.Disconnected -= HandleDaemonDisconnected;
            base.OnClosing(e);
        }

        private const string AutoStartRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AutoStartValueName = "OpenTabletDriver Wintab";
        private const string AutoStartTrayValueName = "OpenTabletDriver Wintab Tray";
        private const string AutoStartPreferenceKey = @"Software\OpenTabletDriver\Wintab";

        /// <summary>
        /// The user's decision, defaulting to enabled on a copy that has never been run.
        /// Writing the Run entry immediately makes that default real rather than cosmetic.
        /// </summary>
        private static bool AutoStartPreferenceEnabled()
        {
            try
            {
                using var preference = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutoStartPreferenceKey);
                if (preference?.GetValue("AutoStart") is int stored)
                    return stored != 0;

                ApplyAutoStart(true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? AutoStartDaemon()
        {
            var directory = Environment.ProcessPath is { Length: > 0 } path
                ? System.IO.Path.GetDirectoryName(path)
                : null;

            if (directory != null)
            {
                var daemon = System.IO.Path.Join(directory, "OpenTabletDriver.Daemon.exe");
                if (System.IO.File.Exists(daemon))
                    return daemon;
            }

            return null;
        }

        private static void ApplyAutoStart(bool enabled)
        {
            try
            {
                using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutoStartRunKey, true);
                if (run != null)
                {
                    if (enabled)
                    {
                        // Two entries, because a logon has two separate jobs. The daemon makes the
                        // pen work about a second after logon and must not wait for the panel, while
                        // the panel only needs to sit in the tray with its window hidden: it is the
                        // 170 MB WPF app and starting it visibly is what made logon feel slow.
                        if (AutoStartDaemon() is { Length: > 0 } daemon)
                            run.SetValue(AutoStartValueName, $"\"{daemon}\"");

                        if (Environment.ProcessPath is { Length: > 0 } panel)
                            run.SetValue(AutoStartTrayValueName, $"\"{panel}\" --minimized --skipupdate");
                    }
                    else
                    {
                        if (run.GetValue(AutoStartValueName) != null)
                            run.DeleteValue(AutoStartValueName, false);
                        if (run.GetValue(AutoStartTrayValueName) != null)
                            run.DeleteValue(AutoStartTrayValueName, false);
                    }
                }

                using var preference = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AutoStartPreferenceKey);
                preference?.SetValue("AutoStart", enabled ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch
            {
                // A locked-down registry must not take the control panel down with it.
            }
        }
    }
}
