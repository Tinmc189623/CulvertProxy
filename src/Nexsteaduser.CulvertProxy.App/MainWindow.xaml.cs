using System.Runtime.InteropServices;
using Microsoft.UI;
using Windows.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexsteaduser.CulvertProxy.App.Dialogs;
using Nexsteaduser.CulvertProxy.App.Pages;
using Nexsteaduser.CulvertProxy.App.Services;
using Nexsteaduser.CulvertProxy.Core.Configuration;

namespace Nexsteaduser.CulvertProxy.App;

public sealed partial class MainWindow : Window
{
    private static readonly SolidColorBrush RunningBrush = new(Color.FromArgb(255, 52, 211, 153));
    private static readonly SolidColorBrush StoppedBrush = new(Color.FromArgb(255, 248, 113, 113));
    private readonly DispatcherQueueTimer _chromeTimer;
    private TrayIcon? _tray;
    private WindowSubclass? _windowSubclass;
    private bool _exitRequested;
    private bool _shutdown;
    private bool _hideOnStartup;
    private string? _currentPage;
    private string? _lastTooltip;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Title = "CulvertProxy — Nexsteaduser";
        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "culvert.ico"));
        }
        catch (Exception ex)
        {
            App.Services.Log.Warn("Window", $"设置窗口图标失败：{ex.Message}");
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        try
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }
        catch
        {
            // 不支持的设备保持纯色背景。
        }

        ApplyTheme();
        RootGrid.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
        App.Services.ConfigChanged += OnConfigChanged;
        AppWindow.Closing += OnClosing;

        _chromeTimer = DispatcherQueue.CreateTimer();
        _chromeTimer.Interval = TimeSpan.FromSeconds(2);
        _chromeTimer.Tick += (_, _) => UpdateChrome();
        _chromeTimer.Start();

        SetupTray();
        SetupMinimizeHook();
        var startPage = NormalizePage(App.Services.Config.Ui.LastPage);
        Nav.SelectedItem = Nav.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, startPage, StringComparison.Ordinal))
            ?? Nav.MenuItems[0];
        NavigateTo(startPage);
        _hideOnStartup = Environment.GetCommandLineArgs().Contains("--startup", StringComparer.OrdinalIgnoreCase)
            && App.Services.Config.Ui.CloseToTray;
        RootGrid.Loaded += OnRootLoaded;
    }

    private static string NormalizePage(string? page) => page switch
    {
        "proxies" or "rules" or "connections" or "logs" or "settings" => page,
        _ => "dashboard",
    };

    private void SetupMinimizeHook()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _windowSubclass = new WindowSubclass(hwnd, OnWindowMinimized);
            if (!_windowSubclass.Installed)
            {
                App.Services.Log.Warn("Window", "无法安装窗口消息钩子，最小化到托盘不可用。");
            }
        }
        catch (Exception ex)
        {
            App.Services.Log.Warn("Window", $"安装最小化钩子失败：{ex.Message}");
        }
    }

    private void OnWindowMinimized()
    {
        if (_tray is null || !App.Services.Config.Ui.MinimizeToTray)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_shutdown)
            {
                AppWindow.Hide();
            }
        });
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTitleBarTheme();
        UpdateChrome();
        if (App.Services.IsFirstRun)
        {
            await ShowSetupWizardAsync(firstRun: true);
        }

        if (_hideOnStartup && _tray is not null && !App.Services.IsFirstRun)
        {
            AppWindow.Hide();
        }
    }

    private async Task ShowSetupWizardAsync(bool firstRun)
    {
        try
        {
            var dialog = new SetupWizardDialog(App.Services.Config) { XamlRoot = RootGrid.XamlRoot };
            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Primary && dialog.Result is { } setup)
            {
                await App.Services.ApplySetupWizardAsync(setup);
            }
            else if (firstRun)
            {
                // 关闭向导也要写入一次配置，避免下次启动重复弹出。
                await App.Services.SaveConfigAsync(ConfigCloner.Clone(App.Services.Config));
                if (App.Services.Config.Engine.StartProxyOnLaunch)
                {
                    await App.Services.StartEngineAsync();
                }
            }

            UpdateChrome();
        }
        catch (Exception ex)
        {
            App.Services.Log.Error("Setup", $"配置向导失败：{ex.Message}", ex);
        }
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        ApplyTheme();
        UpdateChrome();
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = App.Services.Config.Ui.Theme switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        ApplyTitleBarTheme();
    }

    private void ApplyTitleBarTheme()
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            var dark = RootGrid.ActualTheme == ElementTheme.Dark;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(120, 0, 0, 0);
            titleBar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0);
            titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(45, 255, 255, 255) : Color.FromArgb(36, 0, 0, 0);
            titleBar.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
        }
        catch
        {
            // 标题栏定制失败不影响主流程。
        }
    }

    private void UpdateChrome()
    {
        var snapshot = App.Services.Engine.GetSnapshot();
        var dot = snapshot.IsRunning ? RunningBrush : StoppedBrush;
        if (!ReferenceEquals(StatusDot.Fill, dot))
        {
            StatusDot.Fill = dot;
        }

        var label = snapshot.IsRunning ? $"运行中 · {snapshot.ActiveConnections} 连接" : "已停止";
        if (StatusLabel.Text != label)
        {
            StatusLabel.Text = label;
        }

        var pane = snapshot.ListenerRunning ? $"{snapshot.ListenAddress}:{snapshot.ListenPort}" : "未监听";
        if (PaneEndpoint.Text != pane)
        {
            PaneEndpoint.Text = pane;
        }

        var intercept = snapshot.Interception.Active ? $" · 接管 {snapshot.Interception.InjectedProcesses} 进程" : string.Empty;
        var tooltip = snapshot.IsRunning
            ? $"CulvertProxy — 运行中 · {snapshot.ActiveConnections} 连接{intercept}"
            : "CulvertProxy — 已停止";
        if (_lastTooltip != tooltip)
        {
            _lastTooltip = tooltip;
            _tray?.SetTooltip(tooltip);
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            NavigateTo(tag);
        }
    }

    private void NavigateTo(string tag)
    {
        if (_currentPage == tag || ContentFrame is null)
        {
            return;
        }

        var pageType = tag switch
        {
            "proxies" => typeof(ProxiesPage),
            "rules" => typeof(RulesPage),
            "connections" => typeof(ConnectionsPage),
            "logs" => typeof(LogsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage),
        };
        _currentPage = tag;
        App.Services.SetLastPage(tag);
        ContentFrame.Navigate(pageType);
    }

    private void SetupTray()
    {
        try
        {
            _tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "culvert.ico"));
            _tray.OpenRequested += ShowFromTray;
            _tray.ExitRequested += ExitFromTray;
        }
        catch (Exception ex)
        {
            _tray = null;
            App.Services.Log.Warn("Tray", $"托盘图标不可用：{ex.Message}");
        }
    }

    private void ShowFromTray()
    {
        AppWindow.Show();
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _ = ShowWindow(hwnd, 9 /* SW_RESTORE */);
            _ = SetForegroundWindow(hwnd);
        }
        catch
        {
            // 前台切换失败时窗口仍会显示。
        }

        Activate();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        AppWindow.Show();
        Close();
    }

    private void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (!_exitRequested && App.Services.Config.Ui.CloseToTray && _tray is not null)
        {
            args.Cancel = true;
            sender.Hide();
            return;
        }

        Shutdown();
    }

    private void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        _chromeTimer.Stop();
        App.Services.ConfigChanged -= OnConfigChanged;
        _windowSubclass?.Dispose();
        _windowSubclass = null;
        _tray?.Dispose();
        _tray = null;
        try
        {
            App.Services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // 退出阶段尽量清理，不阻塞窗口关闭。
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}
