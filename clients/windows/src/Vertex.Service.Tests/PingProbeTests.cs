using FluentAssertions;
using Vertex.Service.Net;
using Xunit;

namespace Vertex.Service.Tests;

/// <summary>
/// Anchors the sticky semantics of <see cref="PingProbe"/> as documented
/// on <c>ConnectionStatus.PingMs</c>: a transient probe failure MUST keep
/// the last successful value; only an explicit <c>Reset()</c> clears it.
/// The TunnelEngine wires <c>Reset()</c> to the Disconnected transition
/// (Phase 2.4) — that integration is not exercised here, only the
/// observable behavior of the probe itself.
/// </summary>
public class PingProbeTests
{
    [Fact]
    public async Task ProbeOnce_Success_UpdatesCurrentAndFires()
    {
        var script = new ProbeScript(42);
        int? notified = -1;
        var sut = new PingProbe(probe: script.Run);
        sut.PingMsChanged += v => notified = v;

        await sut.ProbeOnceAsync(default);

        sut.Current.Should().Be(42);
        notified.Should().Be(42);
    }

    [Fact]
    public async Task ProbeOnce_TransientFailure_KeepsLastValue()
    {
        var script = new ProbeScript(42, null, null);
        int eventCount = 0;
        int? lastNotified = -1;
        var sut = new PingProbe(probe: script.Run);
        sut.PingMsChanged += v => { eventCount++; lastNotified = v; };

        await sut.ProbeOnceAsync(default);  // 42
        await sut.ProbeOnceAsync(default);  // null (timeout) → keep
        await sut.ProbeOnceAsync(default);  // null (timeout) → keep

        sut.Current.Should().Be(42, "transient failures must not clear the sticky value");
        eventCount.Should().Be(1, "PingMsChanged should fire only on actual transitions");
        lastNotified.Should().Be(42);
    }

    [Fact]
    public async Task ProbeOnce_SameValueTwice_DoesNotFireDuplicate()
    {
        var script = new ProbeScript(42, 42, 42);
        int eventCount = 0;
        var sut = new PingProbe(probe: script.Run);
        sut.PingMsChanged += _ => eventCount++;

        await sut.ProbeOnceAsync(default);
        await sut.ProbeOnceAsync(default);
        await sut.ProbeOnceAsync(default);

        sut.Current.Should().Be(42);
        eventCount.Should().Be(1, "debounce: identical successful readings collapse into one event");
    }

    [Fact]
    public async Task ProbeOnce_ValueChanges_FiresEachTransition()
    {
        var script = new ProbeScript(42, 50, 50, 30);
        var transitions = new List<int?>();
        var sut = new PingProbe(probe: script.Run);
        sut.PingMsChanged += transitions.Add;

        await sut.ProbeOnceAsync(default);  // 42 → fire
        await sut.ProbeOnceAsync(default);  // 50 → fire
        await sut.ProbeOnceAsync(default);  // 50 → no fire
        await sut.ProbeOnceAsync(default);  // 30 → fire

        transitions.Should().Equal(42, 50, 30);
    }

    [Fact]
    public async Task FullStickyLifecycle_ConnectProbeFailDisconnect()
    {
        // The exact lifecycle from the Phase 2.1 reviewer note:
        //   state=Connected → probe success=42 → status.PingMs=42
        //   probe timeout → status.PingMs STILL 42 (sticky)
        //   state=Disconnected → status.PingMs=null
        var script = new ProbeScript(42, null);
        var transitions = new List<int?>();
        var sut = new PingProbe(probe: script.Run);
        sut.PingMsChanged += transitions.Add;

        // Imitate state=Connected → run two probes (1 success, 1 transient timeout)
        await sut.ProbeOnceAsync(default);
        await sut.ProbeOnceAsync(default);
        sut.Current.Should().Be(42, "sticky preserves successful reading through timeout");

        // Imitate state=Disconnected
        sut.Reset();

        sut.Current.Should().BeNull("Disconnected clears sticky");
        transitions.Should().Equal(42, null);
    }

    [Fact]
    public void Reset_OnFreshProbe_NoEvent()
    {
        // Reset on a probe that was never successful must NOT fire null →
        // null. Otherwise the producer would push two identical
        // ConnectionStatus frames in sequence on every Disconnected
        // transition.
        int eventCount = 0;
        var sut = new PingProbe(probe: ProbeScript.NeverCalled);
        sut.PingMsChanged += _ => eventCount++;

        sut.Reset();

        eventCount.Should().Be(0);
        sut.Current.Should().BeNull();
    }

    [Fact]
    public async Task ProbeOnce_Throws_TreatedAsTransientFailure()
    {
        // A probe delegate that throws (e.g. unexpected DNS exception
        // bubbling out of TcpRtt) is treated identically to a null return:
        // log and keep the last value.
        var sut = new PingProbe(probe: (_, _, _, _) => throw new InvalidOperationException("simulated"));

        await sut.Invoking(s => s.ProbeOnceAsync(default)).Should().NotThrowAsync();
        sut.Current.Should().BeNull();
    }

    [Fact]
    public async Task ProbeOnce_CancellationPropagates()
    {
        // Genuine cancellation of the outer scope must NOT be caught.
        // Producers rely on cancellation to break out of RunAsync.
        var sut = new PingProbe(probe: async (_, _, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        await sut.Invoking(s => s.ProbeOnceAsync(cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_PreCancelledToken_ReturnsImmediatelyWithoutProbing()
    {
        // TunnelEngine cancels the loop's CTS on Disconnected. If a fresh
        // RunAsync is later restarted with a cancelled token (e.g., a
        // race during reconfigure), it must NOT issue a probe — that
        // would leak a measurement into a state where the tunnel is
        // already torn down.
        var script = new ProbeScript(42);
        var sut = new PingProbe(probe: script.Run);

        await sut.RunAsync(new CancellationToken(canceled: true));

        script.CallCount.Should().Be(0);
        sut.Current.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_LoopsUntilCancelled()
    {
        var script = new ProbeScript(42, 50, 60);
        var sut = new PingProbe(
            interval: TimeSpan.FromMilliseconds(20),
            probe: script.Run);

        using var cts = new CancellationTokenSource();
        var loop = sut.RunAsync(cts.Token);

        // Wait long enough for at least 3 probes, then cancel.
        await Task.Delay(150);
        cts.Cancel();
        await loop;

        script.CallCount.Should().BeGreaterOrEqualTo(3);
        sut.Current.Should().BeOneOf(42, 50, 60);
    }

    // -------- helpers --------

    /// <summary>
    /// Replays a fixed sequence of return values across successive
    /// <c>ProbeOnceAsync</c> calls. Returns the last scripted value once
    /// the script is exhausted (so a long-running RunAsync test doesn't
    /// crash on the next tick).
    /// </summary>
    private sealed class ProbeScript
    {
        private readonly int?[] _values;
        private int _idx;

        public int CallCount => _idx;

        public ProbeScript(params int?[] values) => _values = values;

        public Task<int?> Run(string host, int port, TimeSpan timeout, CancellationToken ct)
        {
            var i = Interlocked.Increment(ref _idx) - 1;
            var v = i < _values.Length ? _values[i] : _values[^1];
            return Task.FromResult(v);
        }

        public static Task<int?> NeverCalled(string host, int port, TimeSpan timeout, CancellationToken ct)
            => throw new Xunit.Sdk.XunitException("probe should not have been invoked");
    }
}
