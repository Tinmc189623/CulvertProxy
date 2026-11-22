using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nexsteaduser.CulvertProxy.App.Dialogs;
using Nexsteaduser.CulvertProxy.App.Services;
using Nexsteaduser.CulvertProxy.Core.Configuration;
using Nexsteaduser.CulvertProxy.Core.Routing;

namespace Nexsteaduser.CulvertProxy.App.Pages;

public sealed partial class SettingsPage : Page
{
    private AppConfig _config = null!;
    private bool _syncingProxyToggle;
    private readonly DispatcherQueueTimer _interceptTimer;

    public SettingsPage()
    {
        InitializeComponent();
        _config = ConfigCloner.Clone(App.Services.Config);
        DnsModeBox.ItemsSource = new[] { "系统解析", "DoH (DNS over HTTPS)" };
        ThemeBox.ItemsSource = new[] { "跟随系统", "深色", "浅色" };
        ConfigPathText.Text = App.Services.ConfigPath;
        InterceptToggle.Toggled += (_, _) => UpdateInterceptControlState();
        LoadControls();

        _interceptTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _interceptTimer.Interval = TimeSpan.FromSeconds(2);
        _interceptTimer.Tick += (_, _) => RefreshInterceptStatus();
        _interceptTimer.Start();
        Unloaded += (_, _) => _interceptTimer.Stop();
    }

    private void LoadControls()
    {
        _config = ConfigCloner.Clone(App.Services.Config);
        var listener = _config.Listener;
        ListenerEnabledBox.IsChecked = listener.Enabled;
        BindBox.Text = listener.BindAddress;
        PortBox.Value = listener.Port;
        LanBox.IsChecked = listener.AllowLan;
        LocalUserBox.Text = listener.Username ?? string.Empty;
        LocalPassBox.Password = string.Empty;
        LocalPassBox.PlaceholderText = string.IsNullOrEmpty(listener.Password) ? "无密码" : "留空保持原密码";
        ClearLocalPasswordBox.IsChecked = false;
        ClearLocalPasswordBox.Visibility = string.IsNullOrEmpty(listener.Password) ? Visibility.Collapsed : Visibility.Visible;

        var dns = _config.Dns;
        DnsEnableBox.IsChecked = dns.EnableServer;
        DnsBindBox.Text = dns.BindAddress;
        DnsPortBox.Value = dns.Port;
        DnsModeBox.SelectedIndex = dns.Mode == DnsResolutionMode.DnsOverHttps ? 1 : 0;
        DohUrlBox.Text = dns.DohUrl;
        DohProxyBox.IsChecked = dns.DohThroughProxy;
        RefreshDohPolicyOptions(dns.DohPolicyId);
        FakeIpBox.IsChecked = dns.FakeIpEnabled;
        FakeRangeBox.Text = dns.FakeIpRange;
        FallbackBox.IsChecked = dns.SystemFallback;
        DnsTimeoutBox.Value = dns.QueryTimeoutMs;
        DnsCacheBox.Value = dns.CacheMinutes;

        var interception = _config.Interception;
        InterceptToggle.IsOn = interception.Enabled;
        InterceptExistingBox.IsChecked = interception.InjectRunningProcesses;
        InterceptAutoBox.IsChecked = interception.AutoInjectNewProcesses;
        InterceptFailOpenBox.IsChecked = interception.FailOpen;
        InterceptExcludeBox.Text = string.Join(Environment.NewLine, interception.ExcludeProcesses);
        UpdateInterceptControlState();
        RefreshInterceptStatus();

        var engine = _config.Engine;
        ConnectTimeoutBox.Value = engine.ConnectTimeoutMs;
        HandshakeTimeoutBox.Value = engine.HandshakeTimeoutMs;
        AttemptsBox.Value = engine.MaxRouteAttempts;
        HistoryBox.Value = engine.ConnectionHistoryLimit;
        OutboundBox.Text = engine.OutboundBindAddress ?? string.Empty;
        StartOnLaunchBox.IsChecked = engine.StartProxyOnLaunch;
        LogConnectionsBox.IsChecked = engine.LogConnections;

        var ui = _config.Ui;
        ThemeBox.SelectedIndex = (int)ui.Theme;
        CloseToTrayBox.IsChecked = ui.CloseToTray;
        MinimizeToTrayBox.IsChecked = ui.MinimizeToTray;
        StartWithWindowsBox.IsChecked = ui.StartWithWindows;
        _syncingProxyToggle = true;
        SystemProxyToggle.IsOn = App.Services.SystemProxy.IsEnabled;
        _syncingProxyToggle = false;
        SystemProxyStatus.Text = App.Services.SystemProxy.IsEnabled
            ? $"PAC 已指向 127.0.0.1:{_config.Listener.Port}/proxy.pac"
            : "未接管系统设置。";
    }

    private void RefreshDohPolicyOptions(string? policyId)
    {
        var items = new List<string> { "跟随路由规则" };
        items.AddRange(_config.Policies.Select(policy => policy.Name));
        DohPolicyBox.ItemsSource = items;
        var index = policyId is null
            ? 0
            : _config.Policies.FindIndex(policy => policy.Id == policyId) + 1;
        DohPolicyBox.SelectedIndex = Math.Clamp(index, 0, items.Count - 1);
    }

    private async void Wizard_Click(object sender, RoutedEventArgs e)
    {
        SaveBar.IsOpen = false;
        try
        {
            var dialog = new SetupWizardDialog(App.Services.Config) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Result is null)
            {
                return;
            }

            await App.Services.ApplySetupWizardAsync(dialog.Result);
            LoadControls();
            SaveBar.Severity = InfoBarSeverity.Success;
            SaveBar.Message = "配置向导已应用，引擎已重新加载。";
            SaveBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            SaveBar.Severity = InfoBarSeverity.Error;
            SaveBar.Message = ex.Message;
            SaveBar.IsOpen = true;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveBar.IsOpen = false;
        try
        {
            var next = ConfigCloner.Clone(_config);
            var listener = next.Listener;
            listener.Enabled = ListenerEnabledBox.IsChecked == true;
            listener.BindAddress = string.IsNullOrWhiteSpace(BindBox.Text) ? "127.0.0.1" : BindBox.Text.Trim();
            listener.Port = ReadInt(PortBox, 1080);
            listener.AllowLan = LanBox.IsChecked == true;
            var user = LocalUserBox.Text.Trim();
            listener.Username = user.Length == 0 ? null : user;
            if (ClearLocalPasswordBox.IsChecked == true)
            {
                listener.Password = null;
            }
            else if (!string.IsNullOrEmpty(LocalPassBox.Password))
            {
                listener.SetPassword(LocalPassBox.Password);
            }

            if (listener.AllowLan && !listener.RequiresAuthentication)
            {
                throw new InvalidOperationException("允许局域网访问时必须设置入口用户名与密码。");
            }

            if (!IPAddress.TryParse(listener.BindAddress, out _))
            {
                throw new InvalidOperationException("入口监听地址必须是 IP 字面量，例如 127.0.0.1 或 0.0.0.0。");
            }

            var dns = next.Dns;
            dns.EnableServer = DnsEnableBox.IsChecked == true;
            dns.BindAddress = string.IsNullOrWhiteSpace(DnsBindBox.Text) ? "127.0.0.1" : DnsBindBox.Text.Trim();
            dns.Port = ReadInt(DnsPortBox, 5353);
            dns.Mode = DnsModeBox.SelectedIndex == 1 ? DnsResolutionMode.DnsOverHttps : DnsResolutionMode.System;
            dns.DohUrl = DohUrlBox.Text.Trim();
            dns.DohThroughProxy = DohProxyBox.IsChecked == true;
            dns.DohPolicyId = DohPolicyBox.SelectedIndex switch
            {
                <= 0 => null,
                var index when index - 1 < next.Policies.Count => next.Policies[index - 1].Id,
                _ => null,
            };
            dns.FakeIpEnabled = FakeIpBox.IsChecked == true;
            dns.FakeIpRange = string.IsNullOrWhiteSpace(FakeRangeBox.Text) ? "198.18.0.0/16" : FakeRangeBox.Text.Trim();
            dns.SystemFallback = FallbackBox.IsChecked == true;
            dns.QueryTimeoutMs = ReadInt(DnsTimeoutBox, 5000);
            dns.CacheMinutes = ReadInt(DnsCacheBox, 10);

            if (!IPAddress.TryParse(dns.BindAddress, out _))
            {
                throw new InvalidOperationException("DNS 监听地址必须是 IP 字面量，例如 127.0.0.1。");
            }

            if (dns.Mode == DnsResolutionMode.DnsOverHttps
                && (!Uri.TryCreate(dns.DohUrl, UriKind.Absolute, out var dohUri) || dohUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("DoH 地址必须是有效的 https URL。");
            }

            if (dns.FakeIpEnabled
                && (!CidrRange.TryParse(dns.FakeIpRange, out var fakeRange)
                    || fakeRange.AddressFamily != AddressFamily.InterNetwork
                    || fakeRange.PrefixLength is < 8 or > 30
                    || dns.FakeIpRange.StartsWith("198.19.", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("fake-IP 网段必须是 /8 到 /30 的 IPv4 CIDR，例如 198.18.0.0/16；198.19.0.0/16 保留给透明接管。");
            }

            var interception = next.Interception;
            interception.Enabled = InterceptToggle.IsOn;
            interception.InjectRunningProcesses = InterceptExistingBox.IsChecked == true;
            interception.AutoInjectNewProcesses = InterceptAutoBox.IsChecked == true;
            interception.FailOpen = InterceptFailOpenBox.IsChecked == true;
            interception.ExcludeProcesses = InterceptExcludeBox.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var engine = next.Engine;
            engine.ConnectTimeoutMs = ReadInt(ConnectTimeoutBox, 8000);
            engine.HandshakeTimeoutMs = ReadInt(HandshakeTimeoutBox, 8000);
            engine.MaxRouteAttempts = ReadInt(AttemptsBox, 3);
            engine.ConnectionHistoryLimit = ReadInt(HistoryBox, 500);
            engine.OutboundBindAddress = string.IsNullOrWhiteSpace(OutboundBox.Text) ? null : OutboundBox.Text.Trim();
            if (engine.OutboundBindAddress is not null && !IPAddress.TryParse(engine.OutboundBindAddress, out _))
            {
                throw new InvalidOperationException("出站绑定地址必须是 IP 字面量，或留空自动选择。");
            }

            engine.StartProxyOnLaunch = StartOnLaunchBox.IsChecked == true;
            engine.LogConnections = LogConnectionsBox.IsChecked == true;

            var ui = next.Ui;
            ui.Theme = (AppTheme)Math.Clamp(ThemeBox.SelectedIndex, 0, 2);
            ui.CloseToTray = CloseToTrayBox.IsChecked == true;
            ui.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
            ui.StartWithWindows = StartWithWindowsBox.IsChecked == true;

            await App.Services.SaveConfigAsync(next);
            _config = ConfigCloner.Clone(App.Services.Config);
            LoadControls();
            SaveBar.Severity = InfoBarSeverity.Success;
            SaveBar.Message = "设置已保存，引擎已重新加载。";
            SaveBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            SaveBar.Severity = InfoBarSeverity.Error;
            SaveBar.Message = ex.Message;
            SaveBar.IsOpen = true;
        }
    }

    private async void SystemProxy_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingProxyToggle)
        {
            return;
        }

        var enabled = SystemProxyToggle.IsOn;
        try
        {
            if (enabled)
            {
                if (App.Services.LocalAuthenticationEnabled)
                {
                    throw new InvalidOperationException("本地入口启用了用户名密码，PAC 无法携带 SOCKS5 凭据；请先关闭入口认证，或手动配置代理。");
                }

                if (!App.Services.Engine.IsRunning)
                {
                    await App.Services.StartEngineAsync();
                }

                var snapshot = App.Services.Engine.GetSnapshot();
                if (!snapshot.ListenerRunning)
                {
                    throw new InvalidOperationException(snapshot.ListenerError ?? "本地入口未运行。");
                }

                if (!App.Services.SystemProxy.Enable(snapshot.ListenPort))
                {
                    throw new InvalidOperationException("写入系统代理失败，请查看日志。");
                }
            }
            else
            {
                App.Services.SystemProxy.Disable();
            }

            SystemProxyStatus.Text = App.Services.SystemProxy.IsEnabled
                ? $"PAC 已指向 127.0.0.1:{App.Services.Engine.GetSnapshot().ListenPort}/proxy.pac"
                : "未接管系统设置。";
            SaveBar.Severity = InfoBarSeverity.Success;
            SaveBar.Message = enabled ? "系统代理已启用。" : "系统代理已关闭并恢复原设置。";
            SaveBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            _syncingProxyToggle = true;
            SystemProxyToggle.IsOn = App.Services.SystemProxy.IsEnabled;
            _syncingProxyToggle = false;
            SaveBar.Severity = InfoBarSeverity.Error;
            SaveBar.Message = ex.Message;
            SaveBar.IsOpen = true;
        }
    }

    private void UpdateInterceptControlState()
    {
        var enabled = InterceptToggle.IsOn;
        InterceptExistingBox.IsEnabled = enabled;
        InterceptAutoBox.IsEnabled = enabled;
        InterceptFailOpenBox.IsEnabled = enabled;
        InterceptExcludeBox.IsEnabled = enabled;
    }

    private void RefreshInterceptStatus()
    {
        var snapshot = App.Services.Engine.GetSnapshot().Interception;
        var parts = new List<string>();
        if (!snapshot.Enabled)
        {
            parts.Add("未启用");
        }
        else if (snapshot.Active)
        {
            parts.Add(snapshot.PortV6 > 0
                ? $"入口 127.0.0.1:{snapshot.Port} / [::1]:{snapshot.PortV6}"
                : $"入口 127.0.0.1:{snapshot.Port}");
            parts.Add($"已注入 {snapshot.InjectedProcesses} 个进程");
            parts.Add(snapshot.Elevated ? "管理员权限" : "标准权限");
        }
        else
        {
            parts.Add("已配置，代理未运行");
        }

        if (snapshot.FailedInjections > 0)
        {
            parts.Add($"{snapshot.FailedInjections} 个进程待重试");
        }

        if (!snapshot.HookAvailable)
        {
            parts.Add("缺少 CulvertHook.dll");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            parts.Add(snapshot.Error);
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.LastInjectionError))
        {
            parts.Add(snapshot.LastInjectionError);
        }

        var statusText = string.Join(" · ", parts);
        if (InterceptStatusText.Text != statusText)
        {
            InterceptStatusText.Text = statusText;
        }

        InjectNowButton.IsEnabled = snapshot.Active;
    }

    private async void InjectNow_Click(object sender, RoutedEventArgs e)
    {
        InterceptBar.IsOpen = false;
        try
        {
            if (!App.Services.Engine.IsRunning)
            {
                await App.Services.StartEngineAsync();
            }

            var snapshot = App.Services.Engine.GetSnapshot().Interception;
            if (!snapshot.Active)
            {
                InterceptBar.Severity = InfoBarSeverity.Warning;
                InterceptBar.Message = snapshot.Error ?? "透明接管尚未启动，请勾选并保存设置后再试。";
                InterceptBar.IsOpen = true;
                return;
            }

            var report = await App.Services.Engine.InjectRunningProcessesAsync();
            InterceptBar.Severity = report.Failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            InterceptBar.Message = $"扫描 {report.Scanned} 个进程，新注入 {report.Injected} 个，已在管 {report.AlreadyInjected} 个，跳过 {report.Skipped} 个，失败 {report.Failed} 个。"
                + (report.LastError is null ? string.Empty : $" {report.LastError}");
            InterceptBar.IsOpen = true;
            RefreshInterceptStatus();
        }
        catch (Exception ex)
        {
            InterceptBar.Severity = InfoBarSeverity.Error;
            InterceptBar.Message = ex.Message;
            InterceptBar.IsOpen = true;
        }
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e) => OpenFolder(Path.GetDirectoryName(App.Services.ConfigPath)!);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(App.Services.LogsPath);

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // 资源管理器无法启动时忽略。
        }
    }

    private static int ReadInt(NumberBox box, int fallback) =>
        double.IsNaN(box.Value) ? fallback : (int)Math.Clamp(box.Value, box.Minimum, box.Maximum);
}
