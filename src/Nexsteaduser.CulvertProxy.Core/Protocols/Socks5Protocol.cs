using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Nexsteaduser.CulvertProxy.Core.Configuration;
using Nexsteaduser.CulvertProxy.Core.Net;

namespace Nexsteaduser.CulvertProxy.Core.Protocols;

/// <summary>SOCKS5 协议常量、地址编解码与客户端握手。</summary>
public static class Socks5Protocol
{
    public const byte Version = 0x05;
    public const byte CmdConnect = 0x01;
    public const byte CmdBind = 0x02;
    public const byte CmdUdpAssociate = 0x03;
    public const byte AuthNone = 0x00;
    public const byte AuthUserPassword = 0x02;
    public const byte AuthNoAcceptable = 0xFF;
    public const byte AddressIpv4 = 0x01;
    public const byte AddressDomain = 0x03;
    public const byte AddressIpv6 = 0x04;

    /// <summary>把应答码转为中文说明。</summary>
    public static string DescribeReply(byte reply) => reply switch
    {
        0x00 => "成功",
        0x01 => "服务器内部故障",
        0x02 => "规则不允许该连接",
        0x03 => "网络不可达",
        0x04 => "主机不可达",
        0x05 => "连接被拒绝",
        0x06 => "TTL 过期",
        0x07 => "不支持的命令",
        0x08 => "不支持的地址类型",
        _ => $"未知应答码 0x{reply:X2}",
    };

    /// <summary>协商认证方式并完成（可选）用户名密码认证。</summary>
    public static async Task NegotiateAsync(
        Stream stream,
        ProxyEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var token = cts.Token;

        var username = endpoint.Username;
        var password = endpoint.PlainPassword;
        var useAuth = !string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password);

        byte[] greeting = useAuth ? [Version, 2, AuthNone, AuthUserPassword] : [Version, 1, AuthNone];
        await stream.WriteAsync(greeting, token).ConfigureAwait(false);

        var methodReply = new byte[2];
        await stream.ReadExactlyAsync(methodReply, token).ConfigureAwait(false);
        if (methodReply[0] != Version)
        {
            throw new ProxyHandshakeException($"上游 SOCKS5 版本异常：0x{methodReply[0]:X2}。");
        }

        if (methodReply[1] == AuthNoAcceptable)
        {
            throw new ProxyHandshakeException("上游不接受任何认证方式。", AuthNoAcceptable);
        }

        if (methodReply[1] == AuthUserPassword)
        {
            await AuthenticateAsync(stream, username ?? string.Empty, password ?? string.Empty, token).ConfigureAwait(false);
        }
        else if (methodReply[1] != AuthNone)
        {
            throw new ProxyHandshakeException($"上游要求不支持的认证方式：0x{methodReply[1]:X2}。", methodReply[1]);
        }
    }

    /// <summary>对上游执行 SOCKS5 握手与 CONNECT。</summary>
    public static async Task ConnectAsync(
        Stream stream,
        ProxyEndpoint endpoint,
        ProxyTarget target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await NegotiateAsync(stream, endpoint, timeout, cancellationToken).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var token = cts.Token;

        var addressBytes = new byte[1 + 1 + 255 + 2];
        var addressLength = EncodeAddressAndPort(target, addressBytes);
        var request = new byte[3 + addressLength];
        request[0] = Version;
        request[1] = CmdConnect;
        request[2] = 0x00;
        addressBytes.AsSpan(0, addressLength).CopyTo(request.AsSpan(3));
        await stream.WriteAsync(request, token).ConfigureAwait(false);

        var head = new byte[4];
        await stream.ReadExactlyAsync(head, token).ConfigureAwait(false);
        if (head[0] != Version)
        {
            throw new ProxyHandshakeException($"SOCKS5 应答版本异常：0x{head[0]:X2}。");
        }

        if (head[1] != 0)
        {
            await TryDrainAddressAsync(stream, head[3], token).ConfigureAwait(false);
            throw new ProxyHandshakeException($"上游拒绝连接：{DescribeReply(head[1])}。", head[1]);
        }

        await TryDrainAddressAsync(stream, head[3], token).ConfigureAwait(false);
    }

    /// <summary>RFC1929 用户名密码认证。</summary>
    private static async Task AuthenticateAsync(Stream stream, string username, string password, CancellationToken token)
    {
        var userBytes = Encoding.UTF8.GetBytes(username);
        var passBytes = Encoding.UTF8.GetBytes(password);
        if (userBytes.Length > 255 || passBytes.Length > 255)
        {
            throw new ProxyHandshakeException("用户名或密码超过 255 字节。");
        }

        var request = new byte[3 + userBytes.Length + passBytes.Length];
        request[0] = 0x01;
        request[1] = (byte)userBytes.Length;
        userBytes.CopyTo(request, 2);
        request[2 + userBytes.Length] = (byte)passBytes.Length;
        passBytes.CopyTo(request, 3 + userBytes.Length);
        await stream.WriteAsync(request, token).ConfigureAwait(false);

        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply, token).ConfigureAwait(false);
        if (reply[1] != 0)
        {
            throw new ProxyHandshakeException("上游用户名或密码错误。", 0x02);
        }
    }

    /// <summary>把目标编码为 SOCKS5 地址字段（含端口），返回写入长度。</summary>
    public static int EncodeAddressAndPort(ProxyTarget target, Span<byte> destination)
    {
        if (IPAddress.TryParse(target.Host, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                destination[0] = AddressIpv4;
                if (!ip.TryWriteBytes(destination[1..5], out _))
                {
                    throw new ProxyHandshakeException("IPv4 地址编码失败。");
                }

                WritePort(destination, 5, target.Port);
                return 7;
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                destination[0] = AddressIpv6;
                if (!ip.TryWriteBytes(destination[1..17], out _))
                {
                    throw new ProxyHandshakeException("IPv6 地址编码失败。");
                }

                WritePort(destination, 17, target.Port);
                return 19;
            }
        }

        var ascii = ToAsciiHost(target.Host);
        if (!IsValidDomainHost(ascii))
        {
            throw new ProxyHandshakeException($"目标域名非法：{target.Host}。");
        }

        var hostBytes = Encoding.ASCII.GetBytes(ascii);
        if (hostBytes.Length is 0 or > 255)
        {
            throw new ProxyHandshakeException($"目标域名长度非法：{target.Host}。");
        }

        destination[0] = AddressDomain;
        destination[1] = (byte)hostBytes.Length;
        hostBytes.CopyTo(destination[2..]);
        WritePort(destination, 2 + hostBytes.Length, target.Port);
        return 4 + hostBytes.Length;
    }

    /// <summary>读取地址字段（4 字节头已读后），返回便于日志的字符串。</summary>
    public static async ValueTask<string> ReadAddressAsync(Stream stream, byte addressType, CancellationToken cancellationToken)
    {
        var endpoint = await ReadEndpointAsync(stream, addressType, cancellationToken).ConfigureAwait(false);
        return endpoint.ToString();
    }

    /// <summary>读取地址字段并返回结构化目标。</summary>
    public static async ValueTask<ProxyTarget> ReadEndpointAsync(Stream stream, byte addressType, CancellationToken cancellationToken)
    {
        switch (addressType)
        {
            case AddressIpv4:
            {
                var buffer = new byte[6];
                await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
                return new ProxyTarget(new IPAddress(buffer.AsSpan(0, 4)).ToString(), ReadPort(buffer, 4));
            }
            case AddressIpv6:
            {
                var buffer = new byte[18];
                await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
                return new ProxyTarget(new IPAddress(buffer.AsSpan(0, 16)).ToString(), ReadPort(buffer, 16));
            }
            case AddressDomain:
            {
                var lengthBuffer = new byte[1];
                await stream.ReadExactlyAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
                var domain = new byte[lengthBuffer[0] + 2];
                await stream.ReadExactlyAsync(domain, cancellationToken).ConfigureAwait(false);
                var host = Encoding.ASCII.GetString(domain, 0, lengthBuffer[0]);
                if (!IsValidDomainHost(host))
                {
                    // 未校验的域名会原样拼进上游 HTTP CONNECT 请求，\r\n 可注入任意请求头。
                    throw new ProxyHandshakeException("SOCKS5 域名含非法字符，已拒绝。");
                }

                return new ProxyTarget(host, ReadPort(domain, lengthBuffer[0]));
            }
            default:
                throw new ProxyHandshakeException($"未知地址类型：0x{addressType:X2}。");
        }
    }

    private static async ValueTask TryDrainAddressAsync(Stream stream, byte addressType, CancellationToken token)
    {
        try
        {
            _ = await ReadAddressAsync(stream, addressType, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            // 失败应答后部分服务器会直接断开，忽略排空错误。
        }
    }

    private static void WritePort(Span<byte> destination, int offset, int port)
    {
        destination[offset] = (byte)(port >> 8);
        destination[offset + 1] = (byte)port;
    }

    private static int ReadPort(ReadOnlySpan<byte> source, int offset) => (source[offset] << 8) | source[offset + 1];

    /// <summary>域名是否只含 DNS 安全字符（字母、数字、连字符、点），且标签非空、首尾不是点或连字符。</summary>
    /// <remarks>用于堵住 CRLF/控制字符注入：SOCKS5 域名是长度前缀的二进制字段，\r、\n、空格等会原样通过，
    /// 若拼进上游 HTTP CONNECT 请求即可注入任意请求头。IP 字面量不走本校验。
    /// 总长度上限是 SOCKS5 线路格式的 255（一个长度字节），不是 DNS 的 253。
    /// 注意：不做每标签 ≤63 的硬性校验——现有测试 Socks5UdpCodecTests.WriteHeader_Supports_Long_Domain_Names
    /// 要求 254/255 字符的长域名能编码通过，其构造方式是 250/251 字符的单一标签加 .com；
    /// 加标签 63 上限会让这两个测试继续失败。不合 DNS 标签规范的域名由后续 DNS 解析拒绝。</remarks>
    public static bool IsValidDomainHost(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 255)
        {
            return false;
        }

        if (host[0] is '.' or '-' || host[^1] is '.' or '-')
        {
            return false;
        }

        foreach (var c in host)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '.')
            {
                continue;
            }

            return false;
        }

        // 拒绝空标签（如连续两个点）。
        return !host.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>域名转 ASCII（punycode），失败时原样返回。</summary>
    public static string ToAsciiHost(string host)
    {
        if (host.All(char.IsAscii))
        {
            return host;
        }

        try
        {
            return new IdnMapping().GetAscii(host);
        }
        catch
        {
            return host;
        }
    }
}
