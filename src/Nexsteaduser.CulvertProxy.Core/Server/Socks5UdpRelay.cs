using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Nexsteaduser.CulvertProxy.Core.Configuration;
using Nexsteaduser.CulvertProxy.Core.Engine;
using Nexsteaduser.CulvertProxy.Core.Net;
using Nexsteaduser.CulvertProxy.Core.Protocols;
using Nexsteaduser.CulvertProxy.Core.Routing;

namespace Nexsteaduser.CulvertProxy.Core.Server;

/// <summary>SOCKS5 UDP ASSOCIATE 中继：支持直连与单跳 SOCKS5 上游。</summary>
internal sealed class Socks5UdpRelay : IAsyncDisposable
{
    private const int MaxDatagram = 65535;
    private readonly ProxyEngine _engine;
    private readonly Socket _clientUdp;
    private readonly IPAddress _tcpClientAddress;
    private readonly int _requestedClientPort;
    private readonly ActiveConnection _entry;
    private readonly ProcessIdentity? _process;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _upstreamLock = new(1, 1);
    private readonly SemaphoreSlim _dispatchLimit = new(64, 64);
    private readonly Dictionary<string, Socks5UdpUpstream> _upstreams = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<Task> _background = [];
    private readonly object _directLock = new();
    private readonly List<Socket> _directSockets = [];
    private readonly ConcurrentDictionary<IPEndPoint, DateTimeOffset> _directExpectedReplies = new();
    private IPEndPoint? _clientEndPoint;
    private int _unsupportedLogged;
    private int _disposed;

    private Socks5UdpRelay(
        ProxyEngine engine,
        Socket clientUdp,
        IPAddress tcpClientAddress,
        int requestedClientPort,
        ActiveConnection entry,
        ProcessIdentity? process,
        CancellationTokenSource cts)
    {
        _engine = engine;
        _clientUdp = clientUdp;
        _tcpClientAddress = tcpClientAddress;
        _requestedClientPort = requestedClientPort;
        _entry = entry;
        _process = process;
        _cts = cts;
        RelayEndPoint = (IPEndPoint)clientUdp.LocalEndPoint!;
    }

    public IPEndPoint RelayEndPoint { get; }

    public static Socks5UdpRelay Create(
        ProxyEngine engine,
        Socket tcpClient,
        ProxyTarget requestedClient,
        ActiveConnection entry,
        ProcessIdentity? process,
        CancellationToken cancellationToken)
    {
        var local = (IPEndPoint)tcpClient.LocalEndPoint!;
        var remote = (IPEndPoint)tcpClient.RemoteEndPoint!;
        var udp = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            udp.Bind(new IPEndPoint(local.Address, 0));
            udp.ReceiveBufferSize = 512 * 1024;
            udp.SendBufferSize = 512 * 1024;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.CancellationToken);
            var requestedPort = requestedClient.IsIpLiteral ? requestedClient.Port : 0;
            return new Socks5UdpRelay(engine, udp, remote.Address, requestedPort, entry, process, linked);
        }
        catch
        {
            udp.Dispose();
            throw;
        }
    }

    /// <summary>保持 UDP 中继直到控制 TCP 连接关闭。</summary>
    public async Task RunAsync(Stream control, CancellationToken cancellationToken)
    {
        _background.Add(Task.Run(() => ClientLoopAsync(_cts.Token)));
        try
        {
            var buffer = ArrayPool<byte>.Shared.Rent(512);
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var read = await control.ReadAsync(buffer.AsMemory(0, 512), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常结束。
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _engine.Log.Debug("UDP", $"UDP 控制连接结束：{ex.Message}");
        }

        _cts.Cancel();
        try
        {
            await Task.WhenAll(_background.ToArray()).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // 后台循环允许超时退出。
        }

        await DisposeAsync().ConfigureAwait(false);
    }

    private async Task ClientLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxDatagram);
        try
        {
            var any = new IPEndPoint(_clientUdp.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _clientUdp.ReceiveFromAsync(buffer.AsMemory(0, MaxDatagram), SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    _engine.Log.Debug("UDP", $"接收客户端数据报失败：{ex.SocketErrorCode}");
                    continue;
                }

                if (result.ReceivedBytes < 4 || result.RemoteEndPoint is not IPEndPoint remote)
                {
                    continue;
                }

                if (!remote.Address.Equals(_tcpClientAddress))
                {
                    continue;
                }

                if (_requestedClientPort != 0 && remote.Port != _requestedClientPort)
                {
                    continue;
                }

                var current = _clientEndPoint;
                if (current is null)
                {
                    _clientEndPoint = remote;
                }
                else if (!current.Equals(remote))
                {
                    continue;
                }

                if (!Socks5UdpCodec.TryParseHeader(buffer.AsSpan(0, result.ReceivedBytes), out var source, out var offset))
                {
                    continue;
                }

                var payloadLength = result.ReceivedBytes - offset;
                var payload = ArrayPool<byte>.Shared.Rent(payloadLength);
                buffer.AsSpan(offset, payloadLength).CopyTo(payload);
                _ = DispatchGuardedAsync(source, payload, payloadLength, cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task DispatchGuardedAsync(ProxyTarget source, byte[] payload, int length, CancellationToken cancellationToken)
    {
        try
        {
            await _dispatchLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DispatchAsync(source, payload.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _dispatchLimit.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 会话结束。
        }
        catch (Exception ex)
        {
            _engine.Log.Debug("UDP", $"转发 UDP 数据报失败：{ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    private async ValueTask DispatchAsync(ProxyTarget source, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var (target, decision) = _engine.ResolveRoute(source, RuleTransport.Udp, _process);
        switch (decision.Action)
        {
            case RuleAction.Block:
                _engine.Stats.RecordBlocked();
                return;
            case RuleAction.Direct:
                await SendDirectAsync(target, payload, cancellationToken).ConfigureAwait(false);
                break;
            case RuleAction.Proxy:
            {
                var upstream = await GetUpstreamAsync(decision, target, cancellationToken).ConfigureAwait(false);
                if (upstream is null)
                {
                    return;
                }

                await upstream.SendAsync(target, payload, cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        _entry.AddUploaded(payload.Length);
        _engine.Stats.AddUploaded(payload.Length);
    }

    private async ValueTask SendDirectAsync(ProxyTarget target, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        IReadOnlyList<IPAddress> addresses = target.IsIpLiteral
            ? [target.AsAddress()!]
            : await _engine.Resolver.ResolveAsync(target.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Count == 0)
        {
            return;
        }

        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        var destination = new IPEndPoint(address, target.Port);
        RegisterDirectExpectedReply(destination);
        var socket = GetDirectSocket(address.AddressFamily);
        await socket.SendToAsync(payload, SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>直连 UDP 应答的有效窗口：只接受窗口内发往该目标的回包，过期条目在校验时淘汰。</summary>
    internal static readonly TimeSpan DirectReplyWindow = TimeSpan.FromSeconds(120);

    /// <summary>登记一次直连发送的期望回包来源（地址+端口）。</summary>
    internal void RegisterDirectExpectedReply(IPEndPoint destination)
    {
        _directExpectedReplies[destination] = DateTimeOffset.UtcNow + DirectReplyWindow;
    }

    /// <summary>直连回包是否来自已登记且未过期的期望来源；过期条目会被淘汰。</summary>
    internal static bool IsExpectedDirectReply(
        IPEndPoint remote,
        ConcurrentDictionary<IPEndPoint, DateTimeOffset> expected,
        DateTimeOffset now)
    {
        foreach (var pair in expected)
        {
            if (pair.Value <= now)
            {
                expected.TryRemove(pair.Key, out _);
            }
        }

        return expected.TryGetValue(remote, out var expiresAt) && expiresAt > now;
    }

    private Socket GetDirectSocket(AddressFamily family)
    {
        lock (_directLock)
        {
            foreach (var existing in _directSockets)
            {
                if (existing.AddressFamily == family)
                {
                    return existing;
                }
            }

            var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            socket.ReceiveBufferSize = 512 * 1024;
            socket.SendBufferSize = 512 * 1024;
            _directSockets.Add(socket);
            _background.Add(Task.Run(() => DirectReceiveLoopAsync(socket, _cts.Token)));
            return socket;
        }
    }

    private async Task DirectReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxDatagram);
        try
        {
            var any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await socket.ReceiveFromAsync(buffer.AsMemory(0, MaxDatagram), SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                if (result.ReceivedBytes > 0 && result.RemoteEndPoint is IPEndPoint remote)
                {
                    if (!IsExpectedDirectReply(remote, _directExpectedReplies, DateTimeOffset.UtcNow))
                    {
                        // 未登记的来源一律丢弃：否则任意本机/局域网进程都可伪造应答（如伪造 DNS 结果）。
                        _engine.Log.Debug("UDP", $"丢弃非预期来源的直连 UDP 应答：{remote}。");
                        continue;
                    }

                    await SendToClientAsync(
                        new ProxyTarget(remote.Address.ToString(), remote.Port),
                        buffer.AsMemory(0, result.ReceivedBytes),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<Socks5UdpUpstream?> GetUpstreamAsync(RouteDecision decision, ProxyTarget target, CancellationToken cancellationToken)
    {
        if (decision.PolicyId is null)
        {
            return null;
        }

        var policy = _engine.Routes.GetPolicy(decision.PolicyId);
        if (policy is null)
        {
            return null;
        }

        ProxyEndpoint? hop = null;
        foreach (var chain in _engine.Routes.BuildCandidates(policy, target))
        {
            if (chain.Hops.Count == 1 && chain.Hops[0].Protocol == ProxyProtocol.Socks5)
            {
                hop = chain.Hops[0];
                break;
            }
        }

        if (hop is null)
        {
            if (Interlocked.Exchange(ref _unsupportedLogged, 1) == 0)
            {
                _engine.Log.Warn("UDP", $"策略「{policy.Name}」没有可用于 UDP 的单跳 SOCKS5 服务器，UDP 数据报将被丢弃。");
            }

            return null;
        }

        await _upstreamLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_upstreams.TryGetValue(hop.Id, out var existing))
            {
                if (existing.IsAlive)
                {
                    return existing;
                }

                _upstreams.Remove(hop.Id);
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            var access = _engine.Access;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var upstream = await Socks5UdpUpstream.ConnectAsync(hop, access.ConnectTimeout, access.HandshakeTimeout, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                _upstreams[hop.Id] = upstream;
                _engine.Routes.ReportSuccess(hop.Id, stopwatch.Elapsed.TotalMilliseconds);
                var loopCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, upstream.ClosedToken);
                _background.Add(Task.Run(async () =>
                {
                    try
                    {
                        await UpstreamReceiveLoopAsync(upstream, loopCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        loopCts.Dispose();
                    }
                }));
                return upstream;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _engine.Routes.ReportFailure(hop.Id, ex.Message);
                _engine.Log.Warn("UDP", $"UDP 上游 {hop.Name} 不可用：{ex.Message}");
                return null;
            }
        }
        finally
        {
            _upstreamLock.Release();
        }
    }

    private async Task UpstreamReceiveLoopAsync(Socks5UdpUpstream upstream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxDatagram);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var (source, length) = await upstream.ReceiveAsync(buffer.AsMemory(0, MaxDatagram), cancellationToken).ConfigureAwait(false);
                if (length > 0)
                {
                    await SendToClientAsync(source, buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 通道关闭。
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            _engine.Log.Debug("UDP", $"UDP 上游接收结束：{ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask SendToClientAsync(ProxyTarget source, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var client = _clientEndPoint;
        if (client is null)
        {
            return;
        }

        var header = new byte[Socks5UdpCodec.MaxHeaderLength];
        var headerLength = Socks5UdpCodec.WriteHeader(source, header);
        var total = headerLength + payload.Length;
        if (total > 65507)
        {
            return;
        }

        var datagram = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            header.AsSpan(0, headerLength).CopyTo(datagram);
            payload.Span.CopyTo(datagram.AsSpan(headerLength));
            await _clientUdp.SendToAsync(datagram.AsMemory(0, total), SocketFlags.None, client, cancellationToken).ConfigureAwait(false);
            _entry.AddDownloaded(payload.Length);
            _engine.Stats.AddDownloaded(payload.Length);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // 客户端已离开。
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(datagram);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        foreach (var upstream in _upstreams.Values)
        {
            await upstream.DisposeAsync().ConfigureAwait(false);
        }

        _upstreams.Clear();
        _directExpectedReplies.Clear();
        lock (_directLock)
        {
            foreach (var socket in _directSockets)
            {
                socket.Dispose();
            }

            _directSockets.Clear();
        }

        _clientUdp.Dispose();
        try
        {
            await Task.WhenAll(_background.ToArray()).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // 后台循环允许超时退出。
        }

        _upstreamLock.Dispose();
        _dispatchLimit.Dispose();
        _cts.Dispose();
    }
}
