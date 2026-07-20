using FluentAssertions;
using Vertex.Core.Discovery;
using Vertex.Core.Protocol;
using Xunit;

namespace Vertex.Core.Tests;

public class DiscoveryTrackerTests
{
    private static readonly DateTime FixedNow = new(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);

    private static DiscoveryHeartbeat Hb(
        string id, int rttMs = 50, int clients = 0, int max = 0,
        Dictionary<string, int>? rttMap = null,
        string? country = null, string? dh = null)
    {
        rttMap ??= new Dictionary<string, int> { ["yc.example"] = rttMs };
        return new DiscoveryHeartbeat(id, country, clients, max, rttMap, Uptime: 100, Ts: 0, dh);
    }

    [Fact]
    public void BestExit_PicksLowestScore_RttDominatesAtZeroLoad()
    {
        var t = new DiscoveryTracker(staleAge: TimeSpan.FromHours(1), clock: () => FixedNow);
        t.Handle(Hb("aws", rttMs: 80,  clients: 0, max: 50), FixedNow);
        t.Handle(Hb("sto", rttMs: 30,  clients: 0, max: 50), FixedNow);
        t.Handle(Hb("rvk", rttMs: 200, clients: 0, max: 50), FixedNow);

        t.BestExit("yc.example").Should().Be("sto");
    }

    [Fact]
    public void BestExit_LoadFactorPenalisesBusyExits()
    {
        var t = new DiscoveryTracker(loadFactor: 2.0, staleAge: TimeSpan.FromHours(1), clock: () => FixedNow);
        // sto: rtt=30, 0/50 → score = 30 * (1 + 0)         = 30
        // aws: rtt=20, 45/50 → score = 20 * (1 + 0.9 * 2)  = 56
        t.Handle(Hb("sto", rttMs: 30, clients: 0,  max: 50), FixedNow);
        t.Handle(Hb("aws", rttMs: 20, clients: 45, max: 50), FixedNow);

        t.BestExit("yc.example").Should().Be("sto");
    }

    [Fact]
    public void BestExit_FullExitsExcluded()
    {
        var t = new DiscoveryTracker(clock: () => FixedNow);
        t.Handle(Hb("aws", rttMs: 10, clients: 50, max: 50), FixedNow); // full
        t.Handle(Hb("sto", rttMs: 80, clients: 0,  max: 50), FixedNow);

        t.BestExit("yc.example").Should().Be("sto");
    }

    [Fact]
    public void BestExit_MissingRtt_FallsBackTo100ms()
    {
        var t = new DiscoveryTracker(clock: () => FixedNow);
        // No "yc.example" entry — fallback default RTT=100ms.
        t.Handle(Hb("aws", rttMap: new() { ["sber.example"] = 30 }), FixedNow);
        t.Handle(Hb("sto", rttMap: new() { ["yc.example"]   = 200 }), FixedNow);

        // aws has fallback 100, sto has actual 200 → aws wins despite missing measurement.
        t.BestExit("yc.example").Should().Be("aws");
    }

    [Fact]
    public void BestExit_StripsPortFromBrokerHostWhenLookingUpRtt()
    {
        var t = new DiscoveryTracker(clock: () => FixedNow);
        t.Handle(Hb("aws", rttMap: new() { ["yc.example"] = 30 }), FixedNow);

        // Heartbeat keys by bare host; we query with "host:port".
        t.BestExit("yc.example:8883").Should().Be("aws");
    }

    [Fact]
    public void StaleHeartbeats_AreExcluded()
    {
        var clockNow = FixedNow;
        var t = new DiscoveryTracker(staleAge: TimeSpan.FromSeconds(90), clock: () => clockNow);

        t.Handle(Hb("aws", rttMs: 30), FixedNow.AddMinutes(-2));   // stale (120s old)
        t.Handle(Hb("sto", rttMs: 80), FixedNow);                  // fresh

        t.BestExit("yc.example").Should().Be("sto");
        t.IsAvailable("aws").Should().BeFalse();
        t.IsAvailable("sto").Should().BeTrue();
    }

    [Fact]
    public void ShouldSwitch_RecommendsTargetOnly_WhenAlternativeIs1Point5xBetter()
    {
        var t = new DiscoveryTracker(clock: () => FixedNow);
        // aws current: rtt=100 → score 100
        // sto: rtt=70 → score 70 — only 1.43× better → should NOT switch.
        t.Handle(Hb("aws", rttMs: 100), FixedNow);
        t.Handle(Hb("sto", rttMs: 70), FixedNow);
        t.ShouldSwitch("aws", "yc.example").Should().BeNull();

        // Make sto significantly better: rtt=40 → score 40, ratio 100/40 = 2.5 > 1.5.
        t.Handle(Hb("sto", rttMs: 40), FixedNow);
        t.ShouldSwitch("aws", "yc.example").Should().Be("sto");
    }

    [Fact]
    public void ShouldSwitch_StaleCurrentFallsThroughToBestExit()
    {
        var clockNow = FixedNow;
        var t = new DiscoveryTracker(staleAge: TimeSpan.FromSeconds(90), clock: () => clockNow);

        t.Handle(Hb("aws", rttMs: 100), FixedNow.AddMinutes(-2)); // stale
        t.Handle(Hb("sto", rttMs: 80),  FixedNow);                // fresh

        // Since current (aws) is stale, return best alternative regardless of margin.
        t.ShouldSwitch("aws", "yc.example").Should().Be("sto");
    }

    [Fact]
    public void Snapshot_ReturnsFreshOnly_OrAllWhenIncludeStaleTrue()
    {
        var t = new DiscoveryTracker(staleAge: TimeSpan.FromSeconds(90), clock: () => FixedNow);
        t.Handle(Hb("aws"), FixedNow.AddMinutes(-2));   // stale
        t.Handle(Hb("sto"), FixedNow);                  // fresh

        t.Snapshot().Select(i => i.Id).Should().BeEquivalentTo(new[] { "sto" });
        t.Snapshot(includeStale: true).Select(i => i.Id).Should().BeEquivalentTo(new[] { "aws", "sto" });
    }

    [Fact]
    public void Info_ReturnsFreshHeartbeat_WithDhPubkey()
    {
        var t = new DiscoveryTracker(clock: () => FixedNow);
        t.Handle(Hb("aws", dh: "Y2FmZWJhYmU="), FixedNow);

        t.Info("aws").Should().NotBeNull()
            .And.BeOfType<ExitInfo>()
            .Which.DhPubkey.Should().Be("Y2FmZWJhYmU=");
    }

    [Fact]
    public void Remove_DropsExitFromTrackerImmediately()
    {
        var t = new DiscoveryTracker(clock: () => FixedNow);
        t.Handle(Hb("aws"), FixedNow);
        t.IsAvailable("aws").Should().BeTrue();

        t.Remove("aws");

        t.IsAvailable("aws").Should().BeFalse();
        t.Info("aws").Should().BeNull();
        t.BestExit("yc.example").Should().BeNull();
    }

    [Fact]
    public void ShouldSwitch_OnlyFreshExitIsCurrent_ReturnsNull()
    {
        var clockNow = FixedNow;
        var t = new DiscoveryTracker(staleAge: TimeSpan.FromSeconds(90), clock: () => clockNow);
        t.Handle(Hb("aws", rttMs: 50), FixedNow);
        t.Handle(Hb("sto", rttMs: 30), FixedNow.AddMinutes(-2));   // stale

        // aws is the only fresh exit AND it's our current — there's no
        // alternative to switch to (sto is stale, and even if it weren't
        // we wouldn't switch to ourselves).
        t.ShouldSwitch("aws", "yc.example").Should().BeNull();
    }

    [Fact]
    public void StripPort_GuardAgainstColonAtPositionZero()
    {
        // Sanity: ":8883" must NOT collapse to empty string. Mirrors the
        // Go pkg/discovery.stripPort `i > 0` guard — see Phase 1.6 review
        // major #1.
        DiscoveryTracker.StripPort(":8883").Should().Be(":8883");
        DiscoveryTracker.StripPort("h:8883").Should().Be("h");
        DiscoveryTracker.StripPort("h").Should().Be("h");
    }
}
