using FluentAssertions;
using Vertex.Core.Discovery;
using Vertex.Core.Protocol;
using Vertex.Core.Transport;
using Xunit;

namespace Vertex.Service.Tests;

/// <summary>
/// Anchors the Phase 2.5 fix for auto-exit selection: BestExit must be
/// scored against the live broker host (so heartbeat broker-RTT maps
/// resolve), not against the literal string "auto" (which fell through
/// to DefaultRttMs and reduced the choice to load-only).
///
/// The test exercises the discovery + scoring path directly — JoinHandshake
/// itself depends on IMqttTransport semantics that are out of scope here,
/// but the BestExit-with-broker-host call is the only thing that changed
/// in Phase 2.5, so we anchor that.
/// </summary>
public class JoinHandshakeAutoExitTests
{
    [Fact]
    public void BestExit_PassedActualBrokerHost_PicksLowerRttExit()
    {
        var tracker = new DiscoveryTracker();

        // Two exits, identical load. Aws is fastest from yc broker; sto
        // is fastest from sber broker. Score ought to flip with broker host.
        tracker.Handle(new DiscoveryHeartbeat(
            Id: "aws", Country: "ca", Clients: 0, MaxClients: 100,
            BrokerRttMs: new Dictionary<string, int>
            {
                ["mqtt-yc.vertices.ru"]   = 30,
                ["mqtt-sber.vertices.ru"] = 200,
            },
            Uptime: 0, Ts: null, DhPubkey: "X"));
        tracker.Handle(new DiscoveryHeartbeat(
            Id: "sto", Country: "se", Clients: 0, MaxClients: 100,
            BrokerRttMs: new Dictionary<string, int>
            {
                ["mqtt-yc.vertices.ru"]   = 200,
                ["mqtt-sber.vertices.ru"] = 30,
            },
            Uptime: 0, Ts: null, DhPubkey: "X"));

        tracker.BestExit("mqtt-yc.vertices.ru").Should().Be("aws");
        tracker.BestExit("mqtt-sber.vertices.ru").Should().Be("sto");
    }

    [Fact]
    public void BestExit_PassedLiteralAuto_FallsBackToLoadOnly()
    {
        // Regression guard: with selected="auto" and brokerHost="auto"
        // (the bug), neither exit's BrokerRttMs has an entry for "auto";
        // both fall back to DefaultRttMs. Same RTT + same load + same
        // capacity would tie — the loop returns whichever happens to be
        // first by Dictionary iteration order, which is brittle. The fix
        // is to pass the live broker host instead.
        var tracker = new DiscoveryTracker();

        tracker.Handle(new DiscoveryHeartbeat(
            Id: "aws", Country: "ca", Clients: 50, MaxClients: 100,
            BrokerRttMs: new Dictionary<string, int>
            {
                ["mqtt-yc.vertices.ru"]   = 30,
            },
            Uptime: 0, Ts: null, DhPubkey: "X"));
        tracker.Handle(new DiscoveryHeartbeat(
            Id: "sto", Country: "se", Clients: 0, MaxClients: 100,
            BrokerRttMs: new Dictionary<string, int>
            {
                ["mqtt-yc.vertices.ru"]   = 200,
            },
            Uptime: 0, Ts: null, DhPubkey: "X"));

        // With a real broker host, RTT 30 wins despite higher load
        // (30 * (1 + 0.5*2) = 60 vs 200 * (1 + 0*2) = 200).
        tracker.BestExit("mqtt-yc.vertices.ru").Should().Be("aws");

        // With literal "auto", both fall back to DefaultRttMs=100 →
        // score driven only by load → "sto" (0 clients) wins. Different
        // exit picked from same heartbeat data — that's the bug in
        // shape: depends on a flag the user never picked.
        tracker.BestExit("auto").Should().Be("sto");
    }
}
