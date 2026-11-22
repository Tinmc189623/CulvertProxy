using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexsteaduser.CulvertProxy.Core.Configuration;
using Nexsteaduser.CulvertProxy.Core.Dns;
using Nexsteaduser.CulvertProxy.Core.Diagnostics;
using Nexsteaduser.CulvertProxy.Core.Interception;
using Nexsteaduser.CulvertProxy.Core.Net;
using Nexsteaduser.CulvertProxy.Core.Routing;
using Nexsteaduser.CulvertProxy.Core.Server;
using Nexsteaduser.CulvertProxy.Core.Transport;

namespace Nexsteaduser.CulvertProxy.Core.Engine;

/// <summary>代理引擎运行状态快照。</summary>
public sealed record EngineSnapshot(
    bool IsRunning,
    DateTimeOffset? StartedAt,
    string ListenAddress,
    int ListenPort,
    bool ListenerRunning,
    string? ListenerError,
    bool DnsRunning,
    string DnsAddress,
    int DnsPort,
    string? DnsError,
    int DnsCacheEntries,
    TrafficSnapshot Traffic,
    int ActiveConnections,
    IReadOnlyList<EndpointHealthSnapshot> Health,
    string? LastError,
    InterceptionSnapshot Interception);

/// <summary>本地入口鉴权与时序参数快照。</summary>
internal sealed record LocalAccessSettings(
    bool AuthenticationRequired,
    string? Username,
    string? Password,
    TimeSpan ConnectTimeout,
    TimeSpan HandshakeTimeout,
    int MaxAttempts,
    IPAddress? OutboundBindAddress);

/// <summary>对外测试结果。</summary>
public sealed record ProxyTestResult(bool Success, double ElapsedMs, string? Error);

/// <summary>CulvertProxy 核心引擎：装配路由、DNS、入口监听与统计。</summary>
public sealed class ProxyEngine : IAsyncDisposable
{
    private readonly LogHub _log;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private volatile AppConfig _config;
    private volatile AccessState _access;
    private volatile LocalAccessSettings _accessSettings;
    private volatile FakeIpPool? _fakeIp;
    private readonly RuleEngine _rules;
    private readonly RouteTable _routes;
    private readonly OutboundConnector _connector;
    private readonly TunnelFactory _tunnelFactory;
    private readonly DohResolver _resolver;
    private readonly ProcessResolver _processResolver = new();
    private readonly ConnectionRegistry _connections = new();
    private readonly TrafficStats _stats = new();
    private readonly InterceptionCoordinator _interception;
    private readonly object _snapshotGate = new();
    private EngineSnapshot? _snapshotCache;
    private DateTime _snapshotCacheAt;
    private CancellationTokenSource? _engineCts;
    private LocalProxyServer? _localServer;
    private LocalDnsServer? _dnsServer;
    private Timer? _metricsTimer;
    private bool _running;
    private bool _disposed;
    private DateTimeOffset? _startedAt;
    private string? _listenerError;
    private string? _dnsError;
    private string? _lastError;

    public ProxyEngine(LogHub log, AppConfig config)
    {
        _log = log;
        _config = CloneAndNormalize(config);
        _rules = new RuleEngine(log);
        _routes = new RouteTable();
        _tunnelFactory = new TunnelFactory(ResolveHostAsync);
        _connector = new OutboundConnector(log, _routes, _tunnelFactory);
        _resolver = new DohResolver(log, _config.Dns, OpenDohTunnelAsync);
        _fakeIp = CreateFakeIp(_config.Dns);
        _access = BuildAccess(_config);
        _accessSettings = BuildAccessSettings(_config);
        _interception = new InterceptionCoordinator(this, log);
        ApplyLiveSettings(_config);
    }

    public bool IsRunning => _running;

    public AppConfig Config => _config;

    internal LogHub Log => _log;

    internal RuleEngine Rules => _rules;

    internal RouteTable Routes => _routes;

    internal OutboundConnector Connector => _connector;

    internal DohResolver Resolver => _resolver;

    internal FakeIpPool? FakeIp => _fakeIp;

    internal ProcessResolver Processes => _processResolver;

    internal ConnectionRegistry Connections => _connections;

    internal TrafficStats Stats => _stats;

    internal LocalAccessSettings Access => _accessSettings;

    internal CancellationToken EngineToken => _engineCts?.Token ?? CancellationToken.None;

    /// <summary>启动入口监听、DNS 与统计采样。</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }

            _engineCts = new CancellationTokenSource();
            _startedAt = DateTimeOffset.Now;
            _running = true;
            _lastError = null;
            _listenerError = null;
            _dnsError = null;
            StartLocalServer(_engineCts.Token);
            StartDnsServer(_engineCts.Token);
            _metricsTimer = new Timer(_ => _stats.Sample(), null, 1000, 1000);
            await _interception.ApplyAsync(_config, _engineCts.Token).ConfigureAwait(false);
            InvalidateSnapshot();
            _log.Info("Engine", $"代理引擎已启动，本地入口 {DescribeListener()}。");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>停止所有监听并断开活动连接。</summary>
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            _startedAt = null;
            _metricsTimer?.Dispose();
            _metricsTimer = null;
            _engineCts?.Cancel();
            await _interception.StopAsync().ConfigureAwait(false);
            await StopLocalServerAsync().ConfigureAwait(false);
            await StopDnsServerAsync().ConfigureAwait(false);
            _connections.KillAll();
            _engineCts?.Dispose();
            _engineCts = null;
            _stats.Sample();
            InvalidateSnapshot();
            _log.Info("Engine", "代理引擎已停止。");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>热更新配置；监听地址变化时只重启入口，不影响已有连接。</summary>
    public async Task ReloadAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = _config;
            var next = CloneAndNormalize(config);
            _config = next;
            _access = BuildAccess(next);
            _accessSettings = BuildAccessSettings(next);
            ApplyLiveSettings(next);

            var listenerChanged = previous.Listener.Enabled != next.Listener.Enabled
                || !string.Equals(previous.Listener.BindAddress, next.Listener.BindAddress, StringComparison.OrdinalIgnoreCase)
                || previous.Listener.Port != next.Listener.Port
                || previous.Listener.AllowLan != next.Listener.AllowLan;
            var dnsChanged = previous.Dns.EnableServer != next.Dns.EnableServer
                || !string.Equals(previous.Dns.BindAddress, next.Dns.BindAddress, StringComparison.OrdinalIgnoreCase)
                || previous.Dns.Port != next.Dns.Port
                || previous.Dns.FakeIpEnabled != next.Dns.FakeIpEnabled
                || !string.Equals(previous.Dns.FakeIpRange, next.Dns.FakeIpRange, StringComparison.OrdinalIgnoreCase);

            if (previous.Dns.FakeIpEnabled != next.Dns.FakeIpEnabled
                || previous.Dns.FakeIpRange != next.Dns.FakeIpRange)
            {
                _fakeIp = CreateFakeIp(next.Dns);
            }

            if (_running)
            {
                if (listenerChanged)
                {
                    await StopLocalServerAsync().ConfigureAwait(false);
                    StartLocalServer(_engineCts!.Token);
                }

                if (dnsChanged)
                {
                    await StopDnsServerAsync().ConfigureAwait(false);
                    StartDnsServer(_engineCts!.Token);
                }

                await _interception.ApplyAsync(next, _engineCts!.Token).ConfigureAwait(false);
            }
            else
            {
                DisableStaleInterception();
            }

            InvalidateSnapshot();
            _log.Info("Engine", "配置已重新加载。");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>解析目标并把 fake-IP 还原为域名，同时给出路由决策。</summary>
    internal (ProxyTarget Target, RouteDecision Decision) ResolveRoute(
        ProxyTarget target,
        RuleTransport transport,
        ProcessIdentity? process)
    {
        var ip = target.AsAddress();
        var host = target.Host;
        if (ip is not null && _fakeIp is not null && _fakeIp.TryGetHost(ip, out var domain))
        {
            host = domain;
        }
        else if (ip is not null && InterceptProtocol.IsHookFakeAddress(ip))
        {
            return (target, new RouteDecision(RuleAction.Block, null, "未知的接管 fake-IP", true));
        }
        else if ((ip is not null && IPAddress.IsLoopback(ip))
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return (target, new RouteDecision(RuleAction.Direct, null, "本地回环", true));
        }

        var mapped = ReferenceEquals(host, target.Host) ? target : new ProxyTarget(host, target.Port);
        var decision = _rules.Resolve(new RouteRequest(host, target.Port, transport, process?.Name, process?.Path, ip));
        return (mapped, decision);
    }

    /// <summary>校验本地入口的用户名密码，成功后返回 null。</summary>
    internal bool ValidateLocalCredentials(string? username, string? password)
    {
        var access = _access;
        if (!access.AuthenticationRequired)
        {
            return true;
        }

        if (username is null || password is null)
        {
            return false;
        }

        var expectedUser = Encoding.UTF8.GetBytes(access.Username ?? string.Empty);
        var actualUser = Encoding.UTF8.GetBytes(username);
        var expectedPass = Encoding.UTF8.GetBytes(access.Password ?? string.Empty);
        var actualPass = Encoding.UTF8.GetBytes(password);
        return CryptographicOperations.FixedTimeEquals(expectedUser, actualUser)
            & CryptographicOperations.FixedTimeEquals(expectedPass, actualPass);
    }

    public EngineSnapshot GetSnapshot()
    {
        // 多个页面各自轮询状态；250 ms 合并窗口避免每秒重复拼装完整快照。
        lock (_snapshotGate)
        {
            var now = DateTime.UtcNow;
            if (_snapshotCache is not null && (now - _snapshotCacheAt).TotalMilliseconds < 250)
            {
                // 计数器和采样点保持实时；健康度与注入进程列表允许短时间复用。
                return _snapshotCache with
                {
                    Traffic = _stats.Snapshot(),
                    ActiveConnections = _connections.ActiveCount,
                };
            }

            var snapshot = BuildSnapshot();
            _snapshotCache = snapshot;
            _snapshotCacheAt = now;
            return snapshot;
        }
    }

    private EngineSnapshot BuildSnapshot()
    {
        var config = _config;
        var traffic = _stats.Snapshot();
        var listenAddress = config.Listener.AllowLan && config.Listener.BindAddress is "127.0.0.1" or "localhost"
            ? "0.0.0.0"
            : config.Listener.BindAddress;
        return new EngineSnapshot(
            _running,
            _startedAt,
            listenAddress,
            config.Listener.Port,
            _localServer?.IsRunning == true,
            _listenerError,
            _dnsServer?.IsRunning == true,
            config.Dns.BindAddress,
            config.Dns.Port,
            _dnsError,
            _resolver.CacheCount,
            traffic,
            _connections.ActiveCount,
            _routes.SnapshotHealth(),
            _lastError,
            _interception.Snapshot(config));
    }

    private void InvalidateSnapshot()
    {
        lock (_snapshotGate)
        {
            _snapshotCache = null;
            _snapshotCacheAt = default;
        }
    }

    public IReadOnlyList<ConnectionSnapshot> GetConnections() => _connections.Snapshot();

    public bool KillConnection(long id) => _connections.Kill(id);

    /// <summary>立即扫描并接管当前用户进程。</summary>
    public Task<InjectionReport> InjectRunningProcessesAsync() => _interception.InjectNowAsync();

    /// <summary>向指定进程注入 Hook；用于启动器与端到端诊断。</summary>
    public Task<InjectionReport> InjectProcessAsync(int processId) => _interception.InjectProcessAsync(processId);

    /// <summary>清理崩溃或异常退出遗留的启用配置；其他实例持有时不触碰。</summary>
    public static void DisableStaleInterception()
    {
        using var ownership = new Semaphore(1, 1, InterceptionCoordinator.OwnershipSemaphoreName);
        if (!ownership.WaitOne(0))
        {
            return;
        }

        try
        {
            HookConfig.WriteDisabled();
        }
        finally
        {
            ownership.Release();
        }
    }

    internal string? InterceptTokenHex => _interception.TokenHex;

    internal string InterceptHookPath => _interception.HookPath;

    /// <summary>连接单个上游做延迟探测。</summary>
    public async Task<ProxyTestResult> TestEndpointAsync(
        string endpointId,
        ProxyTarget target,
        CancellationToken cancellationToken = default)
    {
        var endpoint = _routes.GetEndpoint(endpointId);
        if (endpoint is null)
        {
            return new ProxyTestResult(false, 0, "服务器不存在。");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var tunnel = await _tunnelFactory.OpenAsync(
                [endpoint],
                target,
                TimeSpan.FromMilliseconds(_config.Engine.ConnectTimeoutMs),
                TimeSpan.FromMilliseconds(_config.Engine.HandshakeTimeoutMs),
                null,
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            _routes.ReportSuccess(endpoint.Id, stopwatch.Elapsed.TotalMilliseconds);
            return new ProxyTestResult(true, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _routes.ReportFailure(endpoint.Id, ex.Message);
            return new ProxyTestResult(false, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), ex.Message);
        }
    }

    private async ValueTask<Tunnel> OpenDohTunnelAsync(string host, int port, CancellationToken cancellationToken)
    {
        var config = _config;
        var target = new ProxyTarget(host, port);
        RouteDecision decision;
        if (!string.IsNullOrEmpty(config.Dns.DohPolicyId) && _routes.GetPolicy(config.Dns.DohPolicyId) is not null)
        {
            decision = new RouteDecision(RuleAction.Proxy, config.Dns.DohPolicyId, "DoH 指定策略");
        }
        else
        {
            decision = _rules.Resolve(new RouteRequest(host, port, RuleTransport.Tcp));
        }

        if (decision.Action == RuleAction.Direct)
        {
            // DoH 端点自身只能走系统解析，否则会形成自举递归。
            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            var socket = await SocketFactory.ConnectAnyAsync(addresses, port, TimeSpan.FromSeconds(8), null, cancellationToken).ConfigureAwait(false);
            return new Tunnel(new NetworkStream(socket, ownsSocket: true), socket, $"DoH 直连 {host}:{port}");
        }

        return await _connector.ConnectAsync(
            decision,
            target,
            TimeSpan.FromMilliseconds(config.Engine.ConnectTimeoutMs),
            TimeSpan.FromMilliseconds(config.Engine.HandshakeTimeoutMs),
            _accessSettings.OutboundBindAddress,
            config.Engine.MaxRouteAttempts,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<IPAddress>> ResolveHostAsync(string host, CancellationToken cancellationToken)
    {
        _stats.RecordDnsQuery();
        return await _resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
    }

    private void StartLocalServer(CancellationToken token)
    {
        _listenerError = null;
        var config = _config;
        if (!config.Listener.Enabled)
        {
            _log.Info("Listener", "本地入口已禁用。");
            return;
        }

        if (config.Listener.AllowLan && !config.Listener.RequiresAuthentication)
        {
            _listenerError = "允许局域网访问时必须设置入口用户名和密码。";
            _log.Error("Listener", _listenerError);
            return;
        }

        try
        {
            var bindAddress = config.Listener.AllowLan && config.Listener.BindAddress is "127.0.0.1" or "localhost"
                ? IPAddress.Any
                : IPAddress.Parse(config.Listener.BindAddress);
            _localServer = new LocalProxyServer(this, new IPEndPoint(bindAddress, config.Listener.Port));
            _localServer.Start(token);
        }
        catch (Exception ex)
        {
            _listenerError = ex.Message;
            _lastError = ex.Message;
            _ = _localServer?.DisposeAsync();
            _localServer = null;
            _log.Error("Listener", $"本地入口启动失败：{ex.Message}", ex);
        }
    }

    private async ValueTask StopLocalServerAsync()
    {
        if (_localServer is not null)
        {
            await _localServer.DisposeAsync().ConfigureAwait(false);
            _localServer = null;
        }
    }

    private void StartDnsServer(CancellationToken token)
    {
        _dnsError = null;
        var config = _config;
        if (!config.Dns.EnableServer)
        {
            return;
        }

        try
        {
            _dnsServer = new LocalDnsServer(_log, _resolver, _fakeIp, config.Dns, () => _interception.IsActive);
            _dnsServer.Start(token);
        }
        catch (Exception ex)
        {
            _dnsError = ex.Message;
            _log.Error("DNS", $"本地 DNS 启动失败：{ex.Message}", ex);
            _dnsServer = null;
        }
    }

    private async ValueTask StopDnsServerAsync()
    {
        if (_dnsServer is not null)
        {
            await _dnsServer.DisposeAsync().ConfigureAwait(false);
            _dnsServer = null;
        }
    }

    private void ApplyLiveSettings(AppConfig config)
    {
        _rules.Update(config);
        _routes.Update(config);
        _resolver.Update(config.Dns);
        _connections.UpdateHistoryLimit(config.Engine.ConnectionHistoryLimit);
    }

    private static FakeIpPool? CreateFakeIp(DnsConfig config)
    {
        if (!config.FakeIpEnabled)
        {
            return null;
        }

        try
        {
            return new FakeIpPool(config.FakeIpRange);
        }
        catch
        {
            return null;
        }
    }

    private static AccessState BuildAccess(AppConfig config)
    {
        var required = config.Listener.RequiresAuthentication;
        return new AccessState(required, config.Listener.Username, config.Listener.PlainPassword);
    }

    private static LocalAccessSettings BuildAccessSettings(AppConfig config)
    {
        IPAddress? bind = null;
        if (!string.IsNullOrWhiteSpace(config.Engine.OutboundBindAddress)
            && IPAddress.TryParse(config.Engine.OutboundBindAddress, out var parsed))
        {
            bind = parsed;
        }

        return new LocalAccessSettings(
            config.Listener.RequiresAuthentication,
            config.Listener.Username,
            config.Listener.PlainPassword,
            TimeSpan.FromMilliseconds(config.Engine.ConnectTimeoutMs),
            TimeSpan.FromMilliseconds(config.Engine.HandshakeTimeoutMs),
            config.Engine.MaxRouteAttempts,
            bind);
    }

    private static AppConfig CloneAndNormalize(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        var clone = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig) ?? AppConfig.CreateDefault();
        ConfigStore.Normalize(clone);
        return clone;
    }

    private string DescribeListener()
    {
        var config = _config;
        var address = config.Listener.AllowLan && config.Listener.BindAddress is "127.0.0.1" or "localhost"
            ? "0.0.0.0"
            : config.Listener.BindAddress;
        return $"{address}:{config.Listener.Port}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        await _interception.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        _lifecycle.Dispose();
        _processResolver.ClearCache();
    }

    private sealed record AccessState(bool AuthenticationRequired, string? Username, string? Password);
}
