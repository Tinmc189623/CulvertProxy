using System.Buffers.Binary;
using System.Net;
using Nexsteaduser.CulvertProxy.Core.Net;

namespace Nexsteaduser.CulvertProxy.Core.Protocols;

/// <summary>SOCKS5 UDP 封装头（RSV/FRAG + 地址 + 端口）的编解码。</summary>
public static class Socks5UdpCodec
{
    /// <summary>UDP 头最大长度：RSV/FRAG 3 字节 + 域名地址字段最大 259 字节（类型 1 + 长度 1 + 域名 255 + 端口 2）。</summary>
    public const int MaxHeaderLength = 3 + 4 + 255;

    /// <summary>写 UDP 头，返回头长度。</summary>
    public static int WriteHeader(ProxyTarget target, Span<byte> destination)
    {
        destination[0] = 0;
        destination[1] = 0;
        destination[2] = 0;
        return 3 + Socks5Protocol.EncodeAddressAndPort(target, destination[3..]);
    }

    /// <summary>解析 UDP 头，返回负载偏移；分片包返回 false。</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> packet, out ProxyTarget source, out int payloadOffset)
    {
        source = default;
        payloadOffset = 0;
        if (packet.Length < 4 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0)
        {
            return false;
        }

        var atyp = packet[3];
        var offset = 4;
        string host;
        switch (atyp)
        {
            case Socks5Protocol.AddressIpv4:
                if (packet.Length < offset + 6)
                {
                    return false;
                }

                host = new IPAddress(packet.Slice(offset, 4)).ToString();
                offset += 4;
                break;
            case Socks5Protocol.AddressIpv6:
                if (packet.Length < offset + 18)
                {
                    return false;
                }

                host = new IPAddress(packet.Slice(offset, 16)).ToString();
                offset += 16;
                break;
            case Socks5Protocol.AddressDomain:
                if (packet.Length < offset + 1)
                {
                    return false;
                }

                var length = packet[offset];
                offset++;
                if (packet.Length < offset + length + 2)
                {
                    return false;
                }

                host = System.Text.Encoding.ASCII.GetString(packet.Slice(offset, length));
                offset += length;
                if (!Socks5Protocol.IsValidDomainHost(host))
                {
                    // 与 TCP 入口一致：拒绝含控制字符/空格等的域名，避免其进入路由与日志。
                    return false;
                }

                break;
            default:
                return false;
        }

        var port = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
        offset += 2;
        if (port == 0)
        {
            return false;
        }

        source = new ProxyTarget(host, port);
        payloadOffset = offset;
        return true;
    }
}
