using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nexsteaduser.CulvertProxy.Core.Configuration;

namespace Nexsteaduser.CulvertProxy.App.Dialogs;

/// <summary>三步配置向导：接管方式、可选上游、DNS 与启动。</summary>
public sealed partial class SetupWizardDialog : ContentDialog
{
    private ProxyEndpoint? _endpoint;
    private int _step = 1;
    private bool _initializing = true;

    public SetupWizardDialog(AppConfig config)
    {
        InitializeComponent();
        ProtocolBox.ItemsSource = new[] { "SOCKS5", "HTTP CONNECT" };
        ProtocolBox.SelectedIndex = 0;
        DnsModeBox.ItemsSource = new[] { "系统解析", "DoH" };
        DnsModeBox.SelectedIndex = config.Dns.Mode == DnsResolutionMode.DnsOverHttps ? 1 : 0;
        DohUrlBox.Text = config.Dns.DohUrl;
        DohProxyBox.IsChecked = config.Dns.DohThroughProxy;
        InterceptionSwitch.IsOn = config.Interception.Enabled;
        InjectExistingBox.IsChecked = config.Interception.InjectRunningProcesses;
        AutoInjectBox.IsChecked = config.Interception.AutoInjectNewProcesses;
        LocalDnsBox.IsChecked = config.Dns.EnableServer;
        FakeIpBox.IsChecked = config.Dns.FakeIpEnabled;
        _initializing = false;

        PrimaryButtonClick += OnPrimaryClick;
        SecondaryButtonClick += OnSecondaryClick;
        Interception_Toggled(null, null!);
        DnsMode_SelectionChanged(null, null!);
        UpdateStep();
    }

    public SetupWizardResult? Result { get; private set; }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ErrorText.Text = string.Empty;
        if (_step == 1)
        {
            _step = 2;
            UpdateStep();
            args.Cancel = true;
            return;
        }

        if (_step == 2)
        {
            if (!TryBuildEndpoint(out var endpoint, out var error))
            {
                ErrorText.Text = error;
                args.Cancel = true;
                return;
            }

            _endpoint = endpoint;
            _step = 3;
            UpdateStep();
            args.Cancel = true;
            return;
        }

        if (!ValidateDns())
        {
            args.Cancel = true;
            return;
        }

        Result = new SetupWizardResult(
            InterceptionSwitch.IsOn,
            InjectExistingBox.IsChecked == true,
            AutoInjectBox.IsChecked == true,
            StartOnFinishBox.IsChecked == true,
            _endpoint,
            DnsModeBox.SelectedIndex == 1,
            DohUrlBox.Text.Trim(),
            DohProxyBox.IsChecked == true,
            LocalDnsBox.IsChecked == true,
            FakeIpBox.IsChecked == true);
    }

    private void OnSecondaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_step > 1)
        {
            _step--;
            UpdateStep();
        }
    }

    private bool TryBuildEndpoint(out ProxyEndpoint? endpoint, out string error)
    {
        endpoint = null;
        error = string.Empty;
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            return true;
        }

        if (host.Any(char.IsWhiteSpace) || host.Length > 253)
        {
            error = "主机名不能包含空白字符，且长度不能超过 253。";
            return false;
        }

        if (double.IsNaN(PortBox.Value) || PortBox.Value < 1 || PortBox.Value > 65535)
        {
            error = "端口必须在 1 到 65535 之间。";
            return false;
        }

        var port = (int)PortBox.Value;
        var endpointValue = new ProxyEndpoint
        {
            Name = $"{host}:{port}",
            Protocol = ProtocolBox.SelectedIndex == 1 ? ProxyProtocol.HttpConnect : ProxyProtocol.Socks5,
            Host = host,
            Port = port,
            Username = string.IsNullOrWhiteSpace(UsernameBox.Text) ? null : UsernameBox.Text.Trim(),
            UseTls = TlsBox.IsChecked == true,
            TlsServerName = string.IsNullOrWhiteSpace(SniBox.Text) ? null : SniBox.Text.Trim(),
            Enabled = true,
        };

        if (!string.IsNullOrEmpty(PasswordBox.Password))
        {
            endpointValue.SetPassword(PasswordBox.Password);
        }

        endpoint = endpointValue;
        return true;
    }

    private bool ValidateDns()
    {
        var useDoh = DnsModeBox.SelectedIndex == 1;
        if (useDoh
            && (!Uri.TryCreate(DohUrlBox.Text.Trim(), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps))
        {
            ErrorText.Text = "DoH 地址必须是有效的 https URL。";
            return false;
        }

        if (FakeIpBox.IsChecked == true && LocalDnsBox.IsChecked != true)
        {
            ErrorText.Text = "启用 fake-IP 前必须启用本地 DNS。";
            return false;
        }

        if ((LocalDnsBox.IsChecked == true || FakeIpBox.IsChecked == true) && !InterceptionSwitch.IsOn)
        {
            ErrorText.Text = "本地 DNS + fake-IP 依赖透明接管，请在上一步启用接管，或关闭这两项。";
            return false;
        }

        return true;
    }

    private void UpdateStep()
    {
        StepOne.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepTwo.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepThree.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepLabel.Text = $"步骤 {_step} / 3";
        PrimaryButtonText = _step == 3 ? "完成" : "下一步";
        SecondaryButtonText = _step == 1 ? string.Empty : "上一步";
        if (_step == 3)
        {
            var upstream = string.IsNullOrWhiteSpace(HostBox.Text)
                ? "本地入口，不添加上游"
                : $"{ProtocolBox.SelectedItem} {HostBox.Text.Trim()}:{PortBox.Value:0}";
            var dns = DnsModeBox.SelectedIndex == 1
                ? $"DoH {DohUrlBox.Text.Trim()}"
                : "系统 DNS";
            if (LocalDnsBox.IsChecked == true)
            {
                dns += FakeIpBox.IsChecked == true ? "，本地 DNS + fake-IP" : "，本地 DNS";
            }

            SummaryText.Text = $"接管：{(InterceptionSwitch.IsOn ? "透明接管" : "仅本地入口")}\n上游：{upstream}\nDNS：{dns}\n完成后启动：{(StartOnFinishBox.IsChecked == true ? "是" : "否")}";
        }
    }

    private void Interception_Toggled(object? sender, RoutedEventArgs e)
    {
        var enabled = InterceptionSwitch.IsOn;
        InjectExistingBox.IsEnabled = enabled;
        AutoInjectBox.IsEnabled = enabled;
    }

    private void DnsMode_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DohUrlBox is null)
        {
            return;
        }

        var useDoh = DnsModeBox.SelectedIndex == 1;
        DohUrlBox.IsEnabled = useDoh;
        DohProxyBox.IsEnabled = useDoh;
    }

    private void Protocol_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || PortBox is null)
        {
            return;
        }

        if (ProtocolBox.SelectedIndex == 1 && Math.Abs(PortBox.Value - 1080) < 0.1)
        {
            PortBox.Value = 8080;
        }
        else if (ProtocolBox.SelectedIndex == 0 && Math.Abs(PortBox.Value - 8080) < 0.1)
        {
            PortBox.Value = 1080;
        }
    }
}
