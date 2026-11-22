using System.Diagnostics;
using System.Net;
using Nexsteaduser.CulvertProxy.Core.Configuration;
using Nexsteaduser.CulvertProxy.Core.Dns;
using Nexsteaduser.CulvertProxy.Core.Diagnostics;
using Nexsteaduser.CulvertProxy.Core.Engine;
using Nexsteaduser.CulvertProxy.Core.Net;

namespace Nexsteaduser.CulvertProxy.Cli;

internal static class Program
{
    private const string Version = "1.0.2";

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        return command switch
        {
            "run" => await RunAsync(args).ConfigureAwait(false),
            "resolve" => await ResolveAsync(args).ConfigureAwait(false),
            "check" => await CheckAsync(args).ConfigureAwait(false),
            "mcp" => await RunMcpAsync().ConfigureAwait(false),
            "version" or "--version" or "-v" => PrintVersion(),
            _ => PrintHelp(),
        };
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"CulvertProxy {Version}");
        Console.WriteLine("Copyright © 2026 Nexsteaduser. All Rights Reserved");
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
CulvertProxy - SOCKS5 / HTTP 高性能代理客户端

用法:
  culvert run [--config <路径>] [--listen <地址:端口>] [--proxy <URL>] [--direct]
      启动本地混合代理入口（SOCKS5 + HTTP）。
      --proxy 支持 socks5://user:pass@host:port 或 http://user:pass@host:port，可重复。
      --direct  忽略已配置服务器，全部直连（用于排错）。

  culvert resolve <域名> [--config <路径>]
      使用配置中的 DoH 设置解析域名。

  culvert check [目标主机:端口] [--config <路径>]
      测试所有已启用上游的连通性与握手延迟。

  culvert mcp
      通过 stdio 启动 MCP 服务。工具会连接正在运行的 CulvertProxy 界面进程，
      用于查询状态、连接、规则，并启动/停止代理或测试上游。

  culvert version
      显示版本信息。
""");
        return 0;
    }

    private static async Task<int> RunMcpAsync()
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await McpServer.RunAsync(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var configPath = GetOption(args, "--config");
        var listen = GetOption(args, "--listen");
        var forceDirect = args.Contains("--direct", StringComparer.OrdinalIgnoreCase);
        var proxyUrls = GetOptions(args, "--proxy");

        await using var log = new LogHub(AppPaths.LogsDirectory);
        log.EntryAdded += entry =>
        {
            var color = entry.Severity switch
            {
                LogSeverity.Error => ConsoleColor.Red,
                LogSeverity.Warn => ConsoleColor.Yellow,
                LogSeverity.Debug or LogSeverity.Trace => ConsoleColor.DarkGray,
                _ => ConsoleColor.Gray,
            };
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(entry.ToLine());
            Console.ForegroundColor = previous;
        };

        var store = new ConfigStore(log, configPath);
        var config = store.Load();
        if (forceDirect)
        {
            config.Endpoints.Clear();
            config.Policies.Clear();
            config.Engine.DefaultPolicyId = null;
        }

        foreach (var proxyUrl in proxyUrls)
        {
            if (!TryParseProxyUrl(proxyUrl, out var endpoint, out var error))
            {
                Console.Error.WriteLine($"代理地址无效：{error}");
                return 2;
            }

            config.Endpoints.Add(endpoint!);
        }

        if (listen is not null)
        {
            var target = ProxyTarget.Parse(listen);
            config.Listener.BindAddress = target.Host;
            config.Listener.Port = target.Port;
        }

        config.Listener.Enabled = true;
        ConfigStore.Normalize(config);
        if (config.Listener.AllowLan && !config.Listener.RequiresAuthentication)
        {
            Console.Error.WriteLine("允许局域网时必须配置入口用户名与密码。");
            return 2;
        }

        await using var engine = new ProxyEngine(log, config);
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sigint = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGINT,
            context =>
            {
                context.Cancel = true;
                shutdown.TrySetResult();
            });
        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM,
            context =>
            {
                context.Cancel = true;
                shutdown.TrySetResult();
            });

        await engine.StartAsync().ConfigureAwait(false);
        var snapshot = engine.GetSnapshot();
        if (snapshot.ListenerError is not null)
        {
            Console.Error.WriteLine($"本地入口启动失败：{snapshot.ListenerError}");
            return 3;
        }

        Console.WriteLine($"本地混合代理：{snapshot.ListenAddress}:{snapshot.ListenPort} (SOCKS5 / HTTP)");
        Console.WriteLine($"控制台日志目录：{AppPaths.LogsDirectory}");
        if (config.Endpoints.Count == 0)
        {
            Console.WriteLine("尚未配置上游服务器，当前全部直连。使用 --proxy 或编辑配置文件后重启。");
        }
        else
        {
            Console.WriteLine($"已加载 {config.Endpoints.Count} 个上游、{config.Policies.Count} 个策略。按 Ctrl+C 退出。");
        }

        var report = Task.Run(async () =>
        {
            while (!shutdown.Task.IsCompleted)
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(5));
                if (await Task.WhenAny(delay, shutdown.Task).ConfigureAwait(false) == shutdown.Task)
                {
                    break;
                }

                var current = engine.GetSnapshot();
                Console.WriteLine(
                    $"[状态] 活动连接 {current.ActiveConnections}  上行 {FormatRate(current.Traffic.UploadBytesPerSecond)}  下行 {FormatRate(current.Traffic.DownloadBytesPerSecond)}  累计 {FormatBytes(current.Traffic.TotalUploadedBytes + current.Traffic.TotalDownloadedBytes)}");
            }
        });

        await shutdown.Task.ConfigureAwait(false);
        await engine.StopAsync().ConfigureAwait(false);
        Console.WriteLine("代理已停止。");
        return 0;
    }

    private static async Task<int> ResolveAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法：culvert resolve <域名> [--config <路径>]");
            return 2;
        }

        var host = args[1];
        var store = new ConfigStore(new LogHub(AppPaths.LogsDirectory, writeToDisk: false), GetOption(args, "--config"));
        var config = store.Load();
        var resolver = new DohResolver(new LogHub(writeToDisk: false), config.Dns, null);
        var stopwatch = Stopwatch.StartNew();
        var aTask = resolver.QueryAsync(host, DnsWire.TypeA, CancellationToken.None).AsTask();
        var aaaaTask = resolver.QueryAsync(host, DnsWire.TypeAaaa, CancellationToken.None).AsTask();
        var results = await Task.WhenAll(aTask, aaaaTask).ConfigureAwait(false);
        stopwatch.Stop();
        foreach (var result in results)
        {
            if (!result.IsSuccess)
            {
                Console.Error.WriteLine($"{host}: {result.Error}");
                continue;
            }

            foreach (var address in result.Addresses)
            {
                Console.WriteLine($"{host} → {address} (TTL {result.TtlSeconds}s)");
            }
        }

        Console.WriteLine($"耗时 {stopwatch.ElapsedMilliseconds} ms，模式 {config.Dns.Mode}。");
        return results.Any(x => x.Addresses.Count > 0) ? 0 : 1;
    }

    private static async Task<int> CheckAsync(string[] args)
    {
        var targetText = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "1.1.1.1:443";
        ProxyTarget target;
        try
        {
            target = ProxyTarget.Parse(targetText);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            Console.Error.WriteLine($"目标地址无效：{targetText}");
            return 2;
        }

        await using var log = new LogHub(writeToDisk: false);
        var store = new ConfigStore(log, GetOption(args, "--config"));
        var config = store.Load();
        await using var engine = new ProxyEngine(log, config);
        var candidates = config.Endpoints.Where(e => e.Enabled).ToArray();
        if (candidates.Length == 0)
        {
            Console.Error.WriteLine("没有启用的上游服务器。");
            return 1;
        }

        var failed = 0;
        foreach (var endpoint in candidates)
        {
            var result = await engine.TestEndpointAsync(endpoint.Id, target).ConfigureAwait(false);
            Console.WriteLine(result.Success
                ? $"OK   {endpoint.Name,-24} {result.ElapsedMs,8:F1} ms"
                : $"FAIL {endpoint.Name,-24} {result.ElapsedMs,8:F1} ms  {result.Error}");
            if (!result.Success)
            {
                failed++;
            }
        }

        return failed == 0 ? 0 : 1;
    }

    private static bool TryParseProxyUrl(string url, out ProxyEndpoint? endpoint, out string? error)
    {
        endpoint = null;
        error = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "无法解析 URL。";
            return false;
        }

        ProxyProtocol protocol;
        if (uri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("socks5h", StringComparison.OrdinalIgnoreCase))
        {
            protocol = ProxyProtocol.Socks5;
        }
        else if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            protocol = ProxyProtocol.HttpConnect;
        }
        else
        {
            error = $"不支持的协议 {uri.Scheme}。";
            return false;
        }

        var endpointValue = new ProxyEndpoint
        {
            Name = $"{uri.Scheme}://{uri.Host}:{uri.Port}",
            Protocol = protocol,
            Host = uri.Host,
            Port = uri.IsDefaultPort ? (protocol == ProxyProtocol.Socks5 ? 1080 : 8080) : uri.Port,
            UseTls = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase),
        };
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            endpointValue.Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1)
            {
                endpointValue.SetPassword(Uri.UnescapeDataString(parts[1]));
            }
        }

        endpoint = endpointValue;
        return true;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static List<string> GetOptions(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                values.Add(args[i + 1]);
            }
        }

        return values;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):F2} GiB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):F2} MiB",
        >= 1024 => $"{bytes / 1024d:F1} KiB",
        _ => $"{bytes} B",
    };

    private static string FormatRate(double bytesPerSecond) => $"{FormatBytes((long)bytesPerSecond)}/s";
}

