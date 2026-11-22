using Microsoft.UI.Xaml;
using Nexsteaduser.CulvertProxy.App.Services;

namespace Nexsteaduser.CulvertProxy.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    public static AppServices Services { get; private set; } = null!;

    public static Window? MainWindowInstance { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Nexsteaduser.CulvertProxy.App", out var created);
        if (!created)
        {
            Exit();
            return;
        }

        try
        {
            Services = new AppServices();
        }
        catch (Exception ex)
        {
            try
            {
                Services?.Log.Error("App", $"启动失败：{ex.Message}", ex);
            }
            catch
            {
                // 启动早期日志系统可能不可用。
            }
        }

        _window = new MainWindow();
        MainWindowInstance = _window;
        _window.Activate();

        // 窗口先呈现，引擎随后在后台启动，避免启动阶段阻塞 UI 线程。
        if (Services is not null && Services.Config.Engine.StartProxyOnLaunch && !Services.IsFirstRun)
        {
            _ = StartEngineInBackgroundAsync(Services);
        }
    }

    private static async Task StartEngineInBackgroundAsync(AppServices services)
    {
        try
        {
            await services.StartEngineAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            services.Log.Error("App", $"后台启动代理失败：{ex.Message}", ex);
        }
    }
}
