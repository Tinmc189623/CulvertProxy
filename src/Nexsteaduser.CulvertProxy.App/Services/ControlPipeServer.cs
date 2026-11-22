using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexsteaduser.CulvertProxy.Core.Configuration;
using Nexsteaduser.CulvertProxy.Core.Control;
using Nexsteaduser.CulvertProxy.Core.Diagnostics;
using Nexsteaduser.CulvertProxy.Core.Engine;
using Nexsteaduser.CulvertProxy.Core.Net;

namespace Nexsteaduser.CulvertProxy.App.Services;

/// <summary>供本机 MCP 桥接调用的只读/受控管道，不监听网络端口。</summary>
internal sealed class ControlPipeServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppServices _services;
    private readonly ProxyEngine _engine;
    private readonly LogHub _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _disposed;

    public ControlPipeServer(AppServices services, LogHub log)
    {
        _services = services;
        _engine = services.Engine;
        _log = log;
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.Info("MCP", $"控制管道已启用：{ControlProtocol.PipeName}");
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User;
        if (user is not null)
        {
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            ControlProtocol.PipeName,
            PipeDirection.InOut,
            8,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            security);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warn("MCP", $"控制管道接受连接失败：{ex.Message}");
                try
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            _ = HandleClientAsync(pipe, cancellationToken);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true })
        {
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonObject response;
                try
                {
                    var request = JsonNode.Parse(line) as JsonObject
                        ?? throw new JsonException("请求必须是 JSON 对象。");
                    var operation = request["op"]?.GetValue<string>() ?? string.Empty;
                    var arguments = request["args"] as JsonObject ?? new JsonObject();
                    var data = await DispatchAsync(operation, arguments, cancellationToken).ConfigureAwait(false);
                    response = new JsonObject { ["ok"] = true, ["data"] = data };
                }
                catch (Exception ex)
                {
                    response = new JsonObject { ["ok"] = false, ["error"] = ex.Message };
                }

                try
                {
                    await writer.WriteLineAsync(response.ToJsonString(JsonOptions)).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    private async Task<JsonNode?> DispatchAsync(string operation, JsonObject arguments, CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case "status":
                return StatusNode();
            case "config":
                return ConfigNode();
            case "connections":
            {
                var activeOnly = arguments["activeOnly"]?.GetValue<bool>() ?? false;
                var limit = Math.Clamp(arguments["limit"]?.GetValue<int>() ?? 200, 1, 500);
                return JsonSerializer.SerializeToNode(
                    _engine.GetConnections()
                        .Where(item => !activeOnly || item.IsActive)
                        .Take(limit)
                        .Select(item => new
                        {
                            id = item.Id,
                            protocol = item.Protocol,
                            target = item.Target,
                            processName = item.ProcessName,
                            processId = item.ProcessId,
                            rule = item.RuleName,
                            policy = item.PolicyName,
                            route = item.Route,
                            state = item.State,
                            startedAt = item.StartedAt,
                            elapsedMs = item.ElapsedMs,
                            bytesUp = item.BytesUp,
                            bytesDown = item.BytesDown,
                            error = item.Error,
                            isActive = item.IsActive,
                        }),
                    JsonOptions);
            }
            case "endpoints":
                return JsonSerializer.SerializeToNode(
                    _services.Config.Endpoints.Select(SanitizeEndpoint),
                    JsonOptions);
            case "rules":
                return JsonSerializer.SerializeToNode(
                    _services.Config.Rules.Select(rule => new
                    {
                        id = rule.Id,
                        name = rule.Name,
                        enabled = rule.Enabled,
                        action = rule.Action.ToString(),
                        policyId = rule.PolicyId,
                        processes = rule.Processes,
                        hosts = rule.Hosts,
                        ipRanges = rule.IpRanges,
                        ports = rule.Ports,
                        transports = rule.Transports.Select(item => item.ToString()).ToArray(),
                    }),
                    JsonOptions);
            case "policies":
                return JsonSerializer.SerializeToNode(
                    _services.Config.Policies.Select(policy => new
                    {
                        id = policy.Id,
                        name = policy.Name,
                        enabled = policy.Enabled,
                        mode = policy.Mode.ToString(),
                        chains = policy.Chains.Select(chain => chain.EndpointIds),
                    }),
                    JsonOptions);
            case "start":
                await _services.StartEngineAsync().ConfigureAwait(false);
                return StatusNode();
            case "stop":
                await _engine.StopAsync().ConfigureAwait(false);
                return StatusNode();
            case "reload":
            {
                var loaded = _services.Store.Load();
                await _services.SaveConfigAsync(loaded).ConfigureAwait(false);
                return StatusNode();
            }
            case "test_endpoint":
            {
                var endpointId = arguments["endpointId"]?.GetValue<string>()
                    ?? throw new ArgumentException("缺少 endpointId。");
                var host = arguments["host"]?.GetValue<string>()
                    ?? throw new ArgumentException("缺少 host。");
                var port = arguments["port"]?.GetValue<int>()
                    ?? throw new ArgumentException("缺少 port。");
                var result = await _engine.TestEndpointAsync(endpointId, new ProxyTarget(host, port), cancellationToken)
                    .ConfigureAwait(false);
                return JsonSerializer.SerializeToNode(new
                {
                    success = result.Success,
                    elapsedMs = result.ElapsedMs,
                    error = result.Error,
                }, JsonOptions);
            }
            case "kill_connection":
            {
                var id = arguments["id"]?.GetValue<long>()
                    ?? throw new ArgumentException("缺少连接 id。");
                return JsonSerializer.SerializeToNode(new { killed = _engine.KillConnection(id) }, JsonOptions);
            }
            default:
                throw new ArgumentException($"未知操作：{operation}。");
        }
    }

    private JsonNode? StatusNode()
    {
        var snapshot = _engine.GetSnapshot();
        return JsonSerializer.SerializeToNode(new
        {
            running = snapshot.IsRunning,
            startedAt = snapshot.StartedAt,
            listener = new
            {
                address = snapshot.ListenAddress,
                port = snapshot.ListenPort,
                running = snapshot.ListenerRunning,
                error = snapshot.ListenerError,
            },
            dns = new
            {
                running = snapshot.DnsRunning,
                address = snapshot.DnsAddress,
                port = snapshot.DnsPort,
                error = snapshot.DnsError,
                cacheEntries = snapshot.DnsCacheEntries,
            },
            traffic = new
            {
                uploadBytesPerSecond = snapshot.Traffic.UploadBytesPerSecond,
                downloadBytesPerSecond = snapshot.Traffic.DownloadBytesPerSecond,
                totalUploadedBytes = snapshot.Traffic.TotalUploadedBytes,
                totalDownloadedBytes = snapshot.Traffic.TotalDownloadedBytes,
                totalConnections = snapshot.Traffic.TotalConnections,
                failedConnections = snapshot.Traffic.FailedConnections,
                blockedConnections = snapshot.Traffic.BlockedConnections,
            },
            activeConnections = snapshot.ActiveConnections,
            interception = new
            {
                enabled = snapshot.Interception.Enabled,
                active = snapshot.Interception.Active,
                port = snapshot.Interception.Port,
                injectedProcesses = snapshot.Interception.InjectedProcesses,
                failedInjections = snapshot.Interception.FailedInjections,
                error = snapshot.Interception.Error,
            },
            lastError = snapshot.LastError,
        }, JsonOptions);
    }

    private JsonNode? ConfigNode()
    {
        var config = _services.Config;
        return JsonSerializer.SerializeToNode(new
        {
            listener = new
            {
                enabled = config.Listener.Enabled,
                address = config.Listener.BindAddress,
                port = config.Listener.Port,
                allowLan = config.Listener.AllowLan,
                authenticationRequired = config.Listener.RequiresAuthentication,
            },
            dns = new
            {
                serverEnabled = config.Dns.EnableServer,
                address = config.Dns.BindAddress,
                port = config.Dns.Port,
                mode = config.Dns.Mode.ToString(),
                dohUrl = config.Dns.DohUrl,
                dohThroughProxy = config.Dns.DohThroughProxy,
                fakeIpEnabled = config.Dns.FakeIpEnabled,
                systemFallback = config.Dns.SystemFallback,
            },
            engine = new
            {
                defaultPolicyId = config.Engine.DefaultPolicyId,
                connectTimeoutMs = config.Engine.ConnectTimeoutMs,
                handshakeTimeoutMs = config.Engine.HandshakeTimeoutMs,
                maxRouteAttempts = config.Engine.MaxRouteAttempts,
                startProxyOnLaunch = config.Engine.StartProxyOnLaunch,
            },
            interception = new
            {
                enabled = config.Interception.Enabled,
                injectRunningProcesses = config.Interception.InjectRunningProcesses,
                autoInjectNewProcesses = config.Interception.AutoInjectNewProcesses,
                failOpen = config.Interception.FailOpen,
                excludeProcesses = config.Interception.ExcludeProcesses,
            },
            endpoints = config.Endpoints.Select(SanitizeEndpoint),
            policies = config.Policies.Select(policy => new
            {
                id = policy.Id,
                name = policy.Name,
                enabled = policy.Enabled,
                mode = policy.Mode.ToString(),
                chains = policy.Chains.Select(chain => chain.EndpointIds),
            }),
            rules = config.Rules.Select(rule => new
            {
                id = rule.Id,
                name = rule.Name,
                enabled = rule.Enabled,
                action = rule.Action.ToString(),
                policyId = rule.PolicyId,
            }),
        }, JsonOptions);
    }

    private static object SanitizeEndpoint(ProxyEndpoint endpoint) => new
    {
        id = endpoint.Id,
        name = endpoint.Name,
        protocol = endpoint.Protocol == ProxyProtocol.Socks5 ? "SOCKS5" : "HTTP",
        host = endpoint.Host,
        port = endpoint.Port,
        username = endpoint.Username,
        useTls = endpoint.UseTls,
        tlsServerName = endpoint.TlsServerName,
        pinnedCertSha256 = endpoint.PinnedCertSha256,
        enabled = endpoint.Enabled,
        hasPassword = !string.IsNullOrEmpty(endpoint.Password),
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // 停止阶段允许超时。
        }

        _cts.Dispose();
    }
}
