using System.Text.Json;
using FluentAssertions;
using Vertex.Core.Protocol;
using Xunit;

namespace Vertex.Core.Tests;

/// <summary>
/// Pin the JSON wire-format of every Protocol type. Field-name drift away
/// from the Swift / Kotlin / Go implementations would silently break the
/// production exit handshake — Docker-only tests do NOT catch this, hence
/// the explicit regression coverage. Reference fixtures in this file are
/// hand-authored by reading the Swift source under
/// <c>clients/shared/VertexCore/Sources/VertexCore/Protocol/</c>.
/// </summary>
public class ProtocolWireFormatTests
{
    /// <summary>
    /// Intentionally minimal serializer options: each Protocol record now
    /// pins <c>JsonIgnore(WhenWritingNull)</c> per-property, so we don't
    /// rely on caller-supplied options to avoid emitting <c>"id":null</c>
    /// on the wire (the case Go / Swift / Kotlin treat with omitempty).
    /// </summary>
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    [Fact]
    public void Topics_Builders_MatchGoFormat()
    {
        Topics.Upload("aws", "iphone").Should().Be("vpn/aws/iphone/out");
        Topics.Download("aws", "iphone").Should().Be("vpn/aws/iphone/in");
        Topics.DownloadAny("iphone").Should().Be("vpn/+/iphone/in");
        Topics.Join("aws").Should().Be("vpn/aws/control/join");
        Topics.Control("aws", "iphone").Should().Be("vpn/aws/iphone/control");
        Topics.ControlAny("iphone").Should().Be("vpn/+/iphone/control");
        Topics.Discovery("aws").Should().Be("discovery/exits/aws");
        Topics.DiscoveryAll.Should().Be("discovery/exits/+");
    }

    [Fact]
    public void JoinMessage_FullPayload_UsesSnakeCaseIdSig()
    {
        var msg = new JoinMessage(
            Name: "iphone",
            Dh: "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
            Id: "ZGVhZGJlZWY=",
            IdSig: "Y2FmZWJhYmU=");

        var json = JsonSerializer.Serialize(msg, Json);

        json.Should().Be(
            """
            {"name":"iphone","dh":"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=","id":"ZGVhZGJlZWY=","id_sig":"Y2FmZWJhYmU="}
            """);

        var back = JsonSerializer.Deserialize<JoinMessage>(json, Json);
        back.Should().Be(msg);
    }

    [Fact]
    public void JoinMessage_LegacyClientWithoutIdentity_OmitsIdAndIdSig()
    {
        var msg = new JoinMessage(Name: "legacy", Dh: "ZmFrZWRoa2V5");

        var json = JsonSerializer.Serialize(msg, Json);

        json.Should().Be("""{"name":"legacy","dh":"ZmFrZWRoa2V5"}""");
    }

    [Fact]
    public void AssignMessage_RoundTrip_MatchesGoShape()
    {
        var msg = new AssignMessage(
            Ip: "10.9.0.5",
            Mask: "255.255.255.0",
            Gw: "10.9.0.1",
            Dh: "ZXhpdGRoa2V5");

        var json = JsonSerializer.Serialize(msg, Json);

        json.Should().Be(
            """{"ip":"10.9.0.5","mask":"255.255.255.0","gw":"10.9.0.1","dh":"ZXhpdGRoa2V5"}""");

        var back = JsonSerializer.Deserialize<AssignMessage>(json, Json);
        back.Should().Be(msg);
    }

    [Fact]
    public void DiscoveryHeartbeat_FullPayload_KeepsSnakeCaseFields()
    {
        var msg = new DiscoveryHeartbeat(
            Id: "aws",
            Country: "CA",
            Clients: 3,
            MaxClients: 50,
            BrokerRttMs: new Dictionary<string, int>
            {
                ["mqtt-yc.vertices.ru"] = 42,
                ["mqtt-sber.vertices.ru"] = 78,
            },
            Uptime: 3600,
            Ts: 1700000000,
            DhPubkey: "ZXhpdHB1YmtleQ==");

        var json = JsonSerializer.Serialize(msg, Json);

        json.Should().Contain("\"max_clients\":50");
        json.Should().Contain("\"broker_rtt_ms\":{");
        json.Should().Contain("\"mqtt-yc.vertices.ru\":42");
        json.Should().Contain("\"dh_pubkey\":\"ZXhpdHB1YmtleQ==\"");

        var back = JsonSerializer.Deserialize<DiscoveryHeartbeat>(json, Json);
        back!.Id.Should().Be("aws");
        back.MaxClients.Should().Be(50);
        back.BrokerRttMs.Should().NotBeNull();
        back.BrokerRttMs!["mqtt-yc.vertices.ru"].Should().Be(42);
    }

    [Fact]
    public void DiscoveryHeartbeat_PartialPayload_OmitsNullsOnTheWire()
    {
        var msg = new DiscoveryHeartbeat(
            Id: "sto",
            Country: null, Clients: null, MaxClients: null,
            BrokerRttMs: null, Uptime: null, Ts: null, DhPubkey: null);

        var json = JsonSerializer.Serialize(msg, Json);

        json.Should().Be("""{"id":"sto"}""");
    }
}
