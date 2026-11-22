using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Nexsteaduser.CulvertProxy.Core.Configuration;
using Nexsteaduser.CulvertProxy.Core.Dns;
using Nexsteaduser.CulvertProxy.Core.Diagnostics;
using Nexsteaduser.CulvertProxy.Core.Net;
using Nexsteaduser.CulvertProxy.Core.Protocols;
using Nexsteaduser.CulvertProxy.Core.Routing;
using Nexsteaduser.CulvertProxy.Core.Server;
using Xunit;

namespace Nexsteaduser.CulvertProxy.Tests;

public sealed class HostPatternTests
{
    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.com", "www.example.com", false)]
    [InlineData("*.example.com", "a.example.com", true)]
    [InlineData("*.example.com", "a.b.example.com", true)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData(".example.com", "example.com", true)]
    [InlineData(".example.com", "api.example.com", true)]
    [InlineData("*google*", "www.google.com", true)]
    [InlineData("api?.example.com", "api1.example.com", true)]
    [InlineData("api?.example.com", "api12.example.com", false)]
    [InlineData("re:^ad[0-9]+\\.example\\.com$", "ad12.example.com", true)]
    [InlineData("*", "anything.at.all", true)]
    public void Patterns_Match_As_Expected(string patternText, string host, bool expected)
    {
        Assert.True(HostPattern.TryCreate(patternText, out var pattern, out var error), error);
        Assert.Equal(expected, pattern!.IsMatch(host));
    }

    [Fact]
    public void Invalid_Regex_Is_Rejected()
    {
        Assert.False(HostPattern.TryCreate("re:(", out _, out var error));
        Assert.NotNull(error);
    }
}

public sealed class CidrTests
{
    [Theory]
    [InlineData("192.168.0.0/16", "192.168.5.5", true)]
    [InlineData("192.168.0.0/16", "192.169.0.1", false)]
    [InlineData("10.0.0.0/8", "10.200.1.1", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("0.0.0.0/0", "223.5.5.5", true)]
    [InlineData("::1/128", "::1", true)]
    [InlineData("::1/128", "::2", false)]
    public void Contains_Works(string cidrText, string addressText, bool expected)
    {
        Assert.True(CidrRange.TryParse(cidrText, out var cidr));
        Assert.Equal(expected, cidr.Contains(IPAddress.Parse(addressText)));
    }

    [Fact]
    public void Ipv4_Mapped_Address_Is_Normalized()
    {
        Assert.True(CidrRange.TryParse("10.0.0.0/8", out var cidr));
        Assert.True(cidr.Contains(IPAddress.Parse("::ffff:10.1.2.3")));
    }

    [Fact]
    public void Invalid_Prefix_Is_Rejected()
    {
        Assert.False(CidrRange.TryParse("10.0.0.0/33", out _));
        Assert.False(CidrRange.TryParse("not-an-address/24", out _));
    }
}

public sealed class PortSetTests
{
    [Fact]
    public void Ranges_Are_Parsed()
    {
        Assert.True(PortSet.TryCreate(["80,443,8000-8002"], out var set, out var error), error);
        Assert.True(set!.Contains(80));
        Assert.True(set.Contains(443));
        Assert.True(set.Contains(8001));
        Assert.False(set.Contains(8003));
    }

    [Fact]
    public void Out_Of_Range_Is_Rejected()
    {
        Assert.False(PortSet.TryCreate(["70000"], out _, out _));
        Assert.False(PortSet.TryCreate(["9000-8000"], out _, out _));
    }
}

public sealed class DnsWireTests
{
    [Fact]
    public void Query_Roundtrip_And_Answer_Parse()
    {
        var query = DnsWire.BuildQuery("example.com", DnsWire.TypeA, 0x1234);
        Assert.True(DnsWire.TryParseQuery(query, out var id, out var host, out var qtype, out var questionEnd));
        Assert.Equal(0x1234, id);
        Assert.Equal("example.com", host);
        Assert.Equal(DnsWire.TypeA, qtype);
        Assert.Equal(query.Length, questionEnd);

        var ipv6 = IPAddress.Parse("2001:db8::1").GetAddressBytes();
        var response = new List<byte>();
        WriteU16(response, 0x1234);
        WriteU16(response, 0x8180);
        WriteU16(response, 1);
        WriteU16(response, 2);
        WriteU16(response, 0);
        WriteU16(response, 0);
        response.AddRange(query[12..]);
        response.AddRange([0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x01, 0x2C, 0x00, 0x04, 1, 2, 3, 4]);
        response.AddRange([0xC0, 0x0C, 0x00, 0x1C, 0x00, 0x01, 0x00, 0x00, 0x02, 0x58, 0x00, 0x10]);
        response.AddRange(ipv6);

        var parsed = DnsWire.ParseAnswer(response.ToArray());
        Assert.True(parsed.IsSuccess);
        Assert.Equal(2, parsed.Addresses.Count);
        Assert.Contains(IPAddress.Parse("1.2.3.4"), parsed.Addresses);
        Assert.Contains(IPAddress.Parse("2001:db8::1"), parsed.Addresses);
        Assert.Equal(300, parsed.TtlSeconds);
    }

    [Fact]
    public void Build_Response_Filters_Family_And_Copies_Question()
    {
        var query = DnsWire.BuildQuery("test.local", DnsWire.TypeA, 7);
        Assert.True(DnsWire.TryParseQuery(query, out _, out _, out _, out var questionEnd));
        var response = DnsWire.BuildResponse(
            query,
            questionEnd,
            [IPAddress.Parse("10.1.2.3"), IPAddress.Parse("2001:db8::5")],
            120,
            DnsWire.TypeA);
        var parsed = DnsWire.ParseAnswer(response);
        Assert.True(parsed.IsSuccess);
        Assert.Single(parsed.Addresses);
        Assert.Equal(IPAddress.Parse("10.1.2.3"), parsed.Addresses[0]);
        Assert.Equal(120, parsed.TtlSeconds);
    }

    [Fact]
    public void Build_Response_Supports_Aaaa_Addresses()
    {
        var query = DnsWire.BuildQuery("example.com", DnsWire.TypeAaaa, 0x4321);
        Assert.True(DnsWire.TryParseQuery(query, out _, out _, out _, out var questionEnd));
        // 混合传入 A 与 AAAA 地址，qtype 为 AAAA 时只应保留 AAAA 地址。
        var response = DnsWire.BuildResponse(
            query,
            questionEnd,
            [IPAddress.Parse("1.2.3.4"), IPAddress.Parse("2001:db8::1"), IPAddress.Parse("2001:db8::2")],
            60,
            DnsWire.TypeAaaa);
        var parsed = DnsWire.ParseAnswer(response);
        Assert.True(parsed.IsSuccess);
        Assert.Equal(2, parsed.Addresses.Count);
        Assert.Contains(IPAddress.Parse("2001:db8::1"), parsed.Addresses);
        Assert.Contains(IPAddress.Parse("2001:db8::2"), parsed.Addresses);
        Assert.Equal(60, parsed.TtlSeconds);
    }

    [Fact]
    public void Nxdomain_Is_Reported()
    {
        var query = DnsWire.BuildQuery("missing.test", DnsWire.TypeA, 9);
        Assert.True(DnsWire.TryParseQuery(query, out _, out _, out _, out var questionEnd));
        var response = DnsWire.BuildResponse(query, questionEnd, [], 0, DnsWire.TypeA, 3);
        var parsed = DnsWire.ParseAnswer(response);
        Assert.False(parsed.IsSuccess);
        Assert.Equal(3, parsed.RCode);
    }

    private static void WriteU16(List<byte> buffer, int value)
    {
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }
}

public sealed class FakeIpPoolTests
{
    [Fact]
    public void Allocation_Is_Stable_And_Reverse_Mappable()
    {
        var pool = new FakeIpPool("198.18.0.0/15");
        var first = pool.GetOrAdd("example.com");
        var again = pool.GetOrAdd("example.com");
        var other = pool.GetOrAdd("example.org");
        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.True(pool.TryGetHost(first, out var host));
        Assert.Equal("example.com", host);
        Assert.StartsWith("198.1", first.ToString());
        pool.Clear();
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Slash30_Allocates_Both_Addresses()
    {
        var pool = new FakeIpPool("198.18.0.0/30");
        var first = pool.GetOrAdd("a.example.com");
        var second = pool.GetOrAdd("b.example.com");
        Assert.Equal(IPAddress.Parse("198.18.0.1"), first);
        Assert.Equal(IPAddress.Parse("198.18.0.2"), second);
        Assert.True(pool.TryGetHost(first, out var firstHost));
        Assert.Equal("a.example.com", firstHost);
        Assert.True(pool.TryGetHost(second, out var secondHost));
        Assert.Equal("b.example.com", secondHost);
    }

    [Fact]
    public void Exhaustion_Evicts_Only_The_Oldest_Entry()
    {
        var pool = new FakeIpPool("198.18.0.0/30");
        var first = pool.GetOrAdd("a.example.com");
        var second = pool.GetOrAdd("b.example.com");
        var third = pool.GetOrAdd("c.example.com");
        Assert.Equal(2, pool.Count);
        // 只淘汰最旧的 a.example.com，复用它的地址；b 的映射必须保留。
        // 注意 first 这个地址已被 c 复用，反查应返回 c，而不是 a。
        Assert.Equal(first, third);
        Assert.True(pool.TryGetHost(first, out var reusedHost));
        Assert.Equal("c.example.com", reusedHost);
        Assert.True(pool.TryGetHost(second, out var secondHost));
        Assert.Equal("b.example.com", secondHost);
        // a 已被淘汰，再次分配会淘汰当时最旧的 b 并复用它的地址。
        var again = pool.GetOrAdd("a.example.com");
        Assert.Equal(second, again);
        Assert.True(pool.TryGetHost(again, out var againHost));
        Assert.Equal("a.example.com", againHost);
    }

    [Fact]
    public void Hook_Reserved_Range_Is_Rejected()
    {
        Assert.Throws<FormatException>(() => new FakeIpPool("198.19.0.0/16"));
    }
}

public sealed class Socks5UdpCodecTests
{
    [Theory]
    [InlineData(254)]
    [InlineData(255)]
    public void WriteHeader_Supports_Long_Domain_Names(int hostLength)
    {
        var host = new string('a', hostLength - 4) + ".com";
        Assert.Equal(hostLength, host.Length);

        var header = new byte[Socks5UdpCodec.MaxHeaderLength];
        var length = Socks5UdpCodec.WriteHeader(new ProxyTarget(host, 53), header);
        Assert.Equal(3 + 4 + hostLength, length);
        Assert.True(Socks5UdpCodec.TryParseHeader(header.AsSpan(0, length), out var parsed, out var payloadOffset));
        Assert.Equal(host, parsed.Host);
        Assert.Equal(53, parsed.Port);
        Assert.Equal(length, payloadOffset);
    }

    [Fact]
    public void MaxHeaderLength_Fits_Longest_Domain_Header()
    {
        Assert.Equal(262, Socks5UdpCodec.MaxHeaderLength);
    }
}

public sealed class RuleEngineTests
{
    [Fact]
    public async Task Process_Host_Port_Rules_And_Default()
    {
        await using var log = new LogHub(writeToDisk: false);
        var config = new AppConfig();
        config.Rules.Add(new RoutingRule
        {
            Name = "chrome-example",
            Action = RuleAction.Proxy,
            Processes = ["chrome.exe"],
            Hosts = ["*.example.com"],
            Ports = ["443"],
        });
        config.Rules.Add(new RoutingRule
        {
            Name = "block-ads",
            Action = RuleAction.Block,
            Hosts = ["ads.*"],
        });

        var engine = new RuleEngine(log);
        engine.Update(config);

        var hit = engine.Resolve(new RouteRequest(
            "api.example.com",
            443,
            RuleTransport.Tcp,
            "chrome.exe",
            @"C:\Program Files\Google\Chrome\chrome.exe"));
        Assert.Equal(RuleAction.Proxy, hit.Action);
        Assert.Equal("chrome-example", hit.RuleName);
        Assert.False(hit.IsDefault);

        var wrongPort = engine.Resolve(new RouteRequest("api.example.com", 80, RuleTransport.Tcp, "chrome.exe"));
        Assert.True(wrongPort.IsDefault);
        Assert.Equal(RuleAction.Direct, wrongPort.Action);

        var wrongProcess = engine.Resolve(new RouteRequest("api.example.com", 443, RuleTransport.Tcp, "firefox.exe"));
        Assert.True(wrongProcess.IsDefault);

        var blocked = engine.Resolve(new RouteRequest("ads.example.net", 443, RuleTransport.Tcp, "chrome.exe"));
        Assert.Equal(RuleAction.Block, blocked.Action);
    }

    [Fact]
    public async Task Cidr_And_Transport_Filters_Work()
    {
        await using var log = new LogHub(writeToDisk: false);
        var config = new AppConfig();
        config.Rules.Add(new RoutingRule
        {
            Name = "udp-private",
            Action = RuleAction.Block,
            IpRanges = ["10.0.0.0/8"],
            Transports = [RuleTransport.Udp],
        });

        var engine = new RuleEngine(log);
        engine.Update(config);

        Assert.Equal(RuleAction.Block, engine.Resolve(new RouteRequest("10.1.2.3", 53, RuleTransport.Udp)).Action);
        Assert.True(engine.Resolve(new RouteRequest("10.1.2.3", 53, RuleTransport.Tcp)).IsDefault);
        Assert.True(engine.Resolve(new RouteRequest("11.1.2.3", 53, RuleTransport.Udp)).IsDefault);
    }
}

public sealed class ConfigTests
{
    [Fact]
    public void Normalize_Creates_Policy_And_Clamps_Ports()
    {
        var config = new AppConfig();
        config.Listener.Port = 70000;
        config.Endpoints.Add(new ProxyEndpoint { Name = "up", Host = "1.2.3.4", Port = 1080 });
        ConfigStore.Normalize(config);
        Assert.Equal(65535, config.Listener.Port);
        Assert.Single(config.Policies);
        Assert.Equal(config.Policies[0].Id, config.Engine.DefaultPolicyId);
        Assert.Single(config.Policies[0].Chains);
    }

    [Fact]
    public void Normalize_Does_Not_Keep_Disabled_Default_Policy()
    {
        var config = new AppConfig();
        var policy = new RoutePolicy
        {
            Name = "disabled",
            Enabled = false,
            Chains = [new ProxyChainSpec { EndpointIds = ["missing"] }],
        };
        config.Policies.Add(policy);
        config.Engine.DefaultPolicyId = policy.Id;
        ConfigStore.Normalize(config);

        Assert.Null(config.Engine.DefaultPolicyId);
        Assert.False(policy.Enabled);
    }

    [Fact]
    public void Normalize_Treats_Zero_Ports_As_Default()
    {
        var config = new AppConfig();
        config.Listener.Port = 0;
        config.Dns.Port = 0;
        ConfigStore.Normalize(config);
        Assert.Equal(1080, config.Listener.Port);
        Assert.Equal(5353, config.Dns.Port);
    }

    [Theory]
    [InlineData("198.19.0.0/16")]
    [InlineData("198.18.0.0/31")]
    [InlineData("not-a-cidr")]
    [InlineData("fd00::/8")]
    public void Normalize_Repairs_Unusable_FakeIp_Range(string range)
    {
        var config = new AppConfig();
        config.Dns.FakeIpRange = range;
        ConfigStore.Normalize(config);
        Assert.Equal("198.18.0.0/16", config.Dns.FakeIpRange);
    }

    [Fact]
    public void Password_Is_Encrypted_At_Rest_And_Roundtrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "culvert-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var log = new LogHub(writeToDisk: false);
            var store = new ConfigStore(log, Path.Combine(directory, "config.json"));
            var config = new AppConfig();
            var endpoint = new ProxyEndpoint { Name = "up", Host = "127.0.0.1", Port = 1080, Username = "user" };
            endpoint.SetPassword("p@ssw0rd");
            config.Endpoints.Add(endpoint);
            store.Save(config);

            var json = File.ReadAllText(store.FilePath);
            Assert.DoesNotContain("p@ssw0rd", json);
            var loaded = store.Load();
            Assert.Equal("p@ssw0rd", loaded.Endpoints[0].PlainPassword);
            Assert.Equal("user", loaded.Endpoints[0].Username);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_Config_Falls_Back_And_Is_Preserved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "culvert-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{ this is not json");
            var log = new LogHub(writeToDisk: false);
            var store = new ConfigStore(log, path);
            var config = store.Load();
            Assert.NotNull(store.LastLoadError);
            Assert.NotEmpty(config.Rules);
            Assert.True(Directory.GetFiles(directory, "config.corrupt-*.json").Length == 1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class PacTests
{
    [Fact]
    public void Pac_Contains_Local_Proxy_And_Direct_Bypasses()
    {
        var config = new AppConfig();
        config.Listener.BindAddress = "127.0.0.1";
        config.Listener.Port = 1080;
        var pac = PacGenerator.Generate(config);
        Assert.Contains("FindProxyForURL", pac);
        Assert.Contains("SOCKS5 127.0.0.1:1080", pac);
        Assert.Contains("DIRECT", pac);
    }
}

/// <summary>AUDIT2 M1：SOCKS5 域名校验，堵住 CRLF 注入上游 HTTP CONNECT 请求。</summary>
public sealed class Socks5DomainValidationTests
{
    [Theory]
    [InlineData("example.com")]
    [InlineData("www.example.com")]
    [InlineData("localhost")]
    [InlineData("a-b.example.com")]
    [InlineData("xn--nxasmq6b.example")]
    [InlineData("a1.example2.com")]
    public void Valid_Domains_Are_Accepted(string host)
    {
        Assert.True(Socks5Protocol.IsValidDomainHost(host));
    }

    [Theory]
    [InlineData("example.com\r\nX-Injected: 1")]
    [InlineData("exa\nmple.com")]
    [InlineData("exa\rmple.com")]
    [InlineData("example .com")]
    [InlineData("exam ple.com")]
    [InlineData("")]
    [InlineData("exa\x01mple.com")]
    [InlineData("exa\x7fmple.com")]
    [InlineData("example.com:80")]
    [InlineData("example.com/path")]
    [InlineData("exam?ple.com")]
    [InlineData("exam_ple.com")]
    [InlineData("-example.com")]
    [InlineData("example.com-")]
    [InlineData(".example.com")]
    [InlineData("example.com.")]
    [InlineData("exa..mple.com")]
    public void Invalid_Domains_Are_Rejected(string host)
    {
        Assert.False(Socks5Protocol.IsValidDomainHost(host));
    }

    [Fact]
    public async Task ReadEndpointAsync_Rejects_Crlf_Domain()
    {
        var malicious = Encoding.ASCII.GetBytes("example.com\r\nX-Injected: 1");
        using var stream = new MemoryStream();
        stream.WriteByte((byte)malicious.Length);
        stream.Write(malicious);
        stream.WriteByte(1);
        stream.WriteByte(187); // 端口 443
        stream.Position = 0;

        await Assert.ThrowsAsync<ProxyHandshakeException>(() =>
            Socks5Protocol.ReadEndpointAsync(stream, Socks5Protocol.AddressDomain, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ReadEndpointAsync_Accepts_Valid_Domain()
    {
        var host = Encoding.ASCII.GetBytes("example.com");
        using var stream = new MemoryStream();
        stream.WriteByte((byte)host.Length);
        stream.Write(host);
        stream.WriteByte(0);
        stream.WriteByte(80);
        stream.Position = 0;

        var target = await Socks5Protocol.ReadEndpointAsync(stream, Socks5Protocol.AddressDomain, CancellationToken.None);
        Assert.Equal("example.com", target.Host);
        Assert.Equal(80, target.Port);
    }

    [Fact]
    public void EncodeAddressAndPort_Rejects_Crlf_Domain()
    {
        var buffer = new byte[300];
        Assert.Throws<ProxyHandshakeException>(() =>
            Socks5Protocol.EncodeAddressAndPort(new ProxyTarget("example.com\r\nX-Injected: 1", 80), buffer));
    }

    [Fact]
    public void TryParseHeader_Rejects_Crlf_Domain()
    {
        var host = Encoding.ASCII.GetBytes("example.com\r\nX-Injected: 1");
        var packet = new byte[4 + 1 + host.Length + 2];
        packet[3] = Socks5Protocol.AddressDomain;
        packet[4] = (byte)host.Length;
        host.CopyTo(packet, 5);
        packet[5 + host.Length] = 0;
        packet[6 + host.Length] = 53;
        Assert.False(Socks5UdpCodec.TryParseHeader(packet, out _, out _));
    }

    [Theory]
    [InlineData("example.com\r\nX-Injected: 1", 80)]
    [InlineData("exa\nmple.com", 80)]
    [InlineData("example.com", 0)]
    [InlineData("example.com", 70000)]
    public void ValidateConnectTarget_Rejects_Invalid_Targets(string host, int port)
    {
        Assert.Throws<ProxyHandshakeException>(() =>
            HttpConnectClient.ValidateConnectTarget(new ProxyTarget(host, port)));
    }

    [Theory]
    [InlineData("example.com", 443)]
    [InlineData("127.0.0.1", 8080)]
    [InlineData("::1", 8080)]
    public void ValidateConnectTarget_Accepts_Valid_Targets(string host, int port)
    {
        HttpConnectClient.ValidateConnectTarget(new ProxyTarget(host, port));
    }
}

/// <summary>AUDIT2 M2：直连 UDP 只接受已登记的期望来源回包。</summary>
public sealed class DirectUdpReplyValidationTests
{
    [Fact]
    public void IsExpectedDirectReply_Accepts_Registered_And_Rejects_Unknown()
    {
        var expected = new ConcurrentDictionary<IPEndPoint, DateTimeOffset>();
        var now = DateTimeOffset.UtcNow;
        var target = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53);
        expected[target] = now + Socks5UdpRelay.DirectReplyWindow;

        Assert.True(Socks5UdpRelay.IsExpectedDirectReply(target, expected, now));
        Assert.False(Socks5UdpRelay.IsExpectedDirectReply(new IPEndPoint(IPAddress.Parse("8.8.4.4"), 53), expected, now));
        Assert.False(Socks5UdpRelay.IsExpectedDirectReply(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 54), expected, now));
    }

    [Fact]
    public void IsExpectedDirectReply_Rejects_Expired_And_Prunes_It()
    {
        var expected = new ConcurrentDictionary<IPEndPoint, DateTimeOffset>();
        var now = DateTimeOffset.UtcNow;
        var target = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53);
        expected[target] = now - TimeSpan.FromSeconds(1);

        Assert.False(Socks5UdpRelay.IsExpectedDirectReply(target, expected, now));
        Assert.Empty(expected);
    }
}

/// <summary>AUDIT2 M3：本地入口首字节超时与并发连接上限。</summary>
public sealed class LocalProxyServerLimitsTests
{
    [Fact]
    public void Limits_Are_Configured()
    {
        Assert.Equal(512, LocalProxyServer.MaxConcurrentConnections);
        Assert.Equal(TimeSpan.FromSeconds(10), LocalProxyServer.FirstByteTimeout);
    }

    [Fact]
    public async Task Idle_Connection_Is_Closed_After_FirstByteTimeout()
    {
        await using var host = await EngineHost.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, host.Port);
        var stream = client.GetStream();
        var buffer = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // 一个字节都不发：服务端应在首字节超时（10 秒）后关闭连接，读到 0。
        var read = await stream.ReadAsync(buffer, cts.Token);
        Assert.Equal(0, read);
    }
}
