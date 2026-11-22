using Nexsteaduser.CulvertProxy.Core.Configuration;

namespace Nexsteaduser.CulvertProxy.App.Dialogs;

/// <summary>首次运行/配置向导收集的选项。</summary>
public sealed record SetupWizardResult(
    bool EnableInterception,
    bool InjectExisting,
    bool AutoInject,
    bool StartEngine,
    ProxyEndpoint? Endpoint,
    bool UseDoh,
    string DohUrl,
    bool DohThroughProxy,
    bool EnableLocalDns,
    bool EnableFakeIp);
