using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Vertex.Core.Config;
using Xunit;

namespace Vertex.Core.Tests;

public class BrokerUrlTests
{
    [Theory]
    [InlineData("mqtt://localhost",                   "mqtt",  "localhost",            1883, "/")]
    [InlineData("mqtts://mqtt-yc.vertices.ru:8883",   "mqtts", "mqtt-yc.vertices.ru",  8883, "/")]
    [InlineData("mqtts://mqtt-yc.vertices.ru",        "mqtts", "mqtt-yc.vertices.ru",  8883, "/")]
    [InlineData("ws://broker.example:9001/mqtt",      "ws",    "broker.example",       9001, "/mqtt")]
    [InlineData("wss://mqtt-yc.vertices.ru/mqtt",     "wss",   "mqtt-yc.vertices.ru",   443, "/mqtt")]
    [InlineData("wss://mqtt-yc.vertices.ru:443",      "wss",   "mqtt-yc.vertices.ru",   443, "/")]
    public void Parse_StandardUrls_PicksDefaultPortsAndPath(string url, string scheme, string host, int port, string path)
    {
        var parsed = BrokerUrl.Parse(url);
        parsed.Scheme.Should().Be(scheme);
        parsed.Host.Should().Be(host);
        parsed.Port.Should().Be(port);
        parsed.Path.Should().Be(path);
    }

    [Theory]
    [InlineData("mqtts://[::1]:8883",     "::1",      8883)]
    [InlineData("mqtts://[2001:db8::1]",  "2001:db8::1", 8883)]
    public void Parse_IPv6Bracketed_HandlesPortAndDefault(string url, string host, int port)
    {
        var parsed = BrokerUrl.Parse(url);
        parsed.Host.Should().Be(host);
        parsed.Port.Should().Be(port);
    }

    [Fact]
    public void TlsAndWsFlags_MatchScheme()
    {
        BrokerUrl.Parse("mqtt://h").Should().Match<BrokerUrl>(b => !b.IsTls && !b.IsWebSocket);
        BrokerUrl.Parse("mqtts://h").Should().Match<BrokerUrl>(b => b.IsTls && !b.IsWebSocket);
        BrokerUrl.Parse("ws://h").Should().Match<BrokerUrl>(b => !b.IsTls && b.IsWebSocket);
        BrokerUrl.Parse("wss://h").Should().Match<BrokerUrl>(b => b.IsTls && b.IsWebSocket);
    }

    [Theory]
    [InlineData("",                             typeof(ArgumentException))]
    [InlineData("notaurl",                      typeof(FormatException))]
    [InlineData("ftp://h",                      typeof(FormatException))]
    [InlineData("mqtts://",                     typeof(FormatException))]
    [InlineData("mqtts://h:99999",              typeof(FormatException))]
    [InlineData("mqtts://[::1",                 typeof(FormatException))]
    public void Parse_InvalidInput_Throws(string url, Type expected)
    {
        Action act = () => BrokerUrl.Parse(url);
        act.Should().Throw<Exception>().Which.GetType().Should().Be(expected);
    }

    [Fact]
    public async Task ResolveIpsAsync_LiteralIPv4_ReturnsAsIs()
    {
        var ips = await BrokerUrl.Parse("mqtts://93.77.179.242:8883").ResolveIpsAsync();
        ips.Should().ContainSingle();
        ips[0].Should().Be(IPAddress.Parse("93.77.179.242"));
    }

    [Fact]
    public async Task ResolveIpsAsync_LiteralIPv6_DropsForBypass()
    {
        // Phase 2.5 broker bypass installs /32 IPv4 routes only — IPv6
        // bypass would need a /128 mirror, not implemented yet. Returning
        // empty here is the safe behavior (caller logs + skips).
        var ips = await BrokerUrl.Parse("mqtts://[2001:db8::1]:8883").ResolveIpsAsync();
        ips.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveIpsAsync_NonExistentHost_ReturnsEmpty()
    {
        // The .invalid TLD is reserved by RFC 6761 for this exact
        // purpose — no recursive resolver should ever return an answer.
        var ips = await BrokerUrl.Parse("mqtts://nope.invalid:8883").ResolveIpsAsync();
        ips.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveIpsAsync_HonorsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await BrokerUrl.Parse("mqtts://example.com:8883")
            .ResolveIpsAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ResolveIpsAsync_PerCallTimeout_FallsThroughEmpty()
    {
        // Per-call deadline (default 2s, override here to 50ms) caps the
        // synchronous getaddrinfo that Dns.GetHostAddressesAsync wraps —
        // a misconfigured resolver / captive portal can't stall Connect.
        // The .invalid TLD never resolves on a normal CI machine; we just
        // need *some* guarantee the timeout fires before the OS resolver
        // gives up on its own (which can take 5–30s on Windows).
        var ips = await BrokerUrl.Parse("mqtts://nope.invalid:8883")
            .ResolveIpsAsync(timeout: TimeSpan.FromMilliseconds(50));
        ips.Should().BeEmpty();
    }
}
