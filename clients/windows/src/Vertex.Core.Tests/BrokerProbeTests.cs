using FluentAssertions;
using System.Net;
using System.Net.Sockets;
using Vertex.Core.Config;
using Vertex.Core.Discovery;
using Vertex.Core.Util;
using Xunit;

namespace Vertex.Core.Tests;

public class BrokerProbeTests
{
    [Fact]
    public async Task ReorderByRtt_EmptyInput_NoOps()
    {
        var got = await BrokerProbe.ReorderByRttAsync(Array.Empty<BrokerUrl>());
        got.Should().BeEmpty();
    }

    [Fact]
    public async Task ReorderByRtt_SingleInput_NoOps()
    {
        var url = BrokerUrl.Parse("mqtts://yc.example:8883");
        var got = await BrokerProbe.ReorderByRttAsync(new[] { url });
        got.Should().Equal(url);
    }

    [Fact]
    public async Task ReorderByRtt_AllProbesFail_PreservesOriginalOrder()
    {
        // Both URLs point at unreachable addresses (RFC 5737 TEST-NET).
        // Use 500ms timeout — at 150ms ARP timeout on a busy ARM64 VM
        // can flake the test; the outer wall-clock cap is well-bounded.
        var u1 = BrokerUrl.Parse("mqtts://192.0.2.1:8883");
        var u2 = BrokerUrl.Parse("mqtts://192.0.2.2:8883");
        var got = await BrokerProbe.ReorderByRttAsync(new[] { u1, u2 }, TimeSpan.FromMilliseconds(500));
        got.Should().Equal(u1, u2); // both failed, original order preserved
    }

    [Fact]
    public async Task ReorderByRtt_OneSuccessOneFail_SuccessFirst()
    {
        // Spin up a listener to give us a real TCP-connectable target.
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // Drain new connections so they don't accumulate.
        _ = Task.Run(async () =>
        {
            try { while (true) (await listener.AcceptTcpClientAsync()).Dispose(); }
            catch { /* swallow on stop */ }
        });

        try
        {
            var dead    = BrokerUrl.Parse("mqtts://192.0.2.1:8883");
            var alive   = BrokerUrl.Parse($"mqtts://127.0.0.1:{port}");

            var got = await BrokerProbe.ReorderByRttAsync(
                new[] { dead, alive },
                TimeSpan.FromMilliseconds(500));

            got.Should().Equal(alive, dead);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ReorderWithRtts_BuildsHostMapForAliveProbes()
    {
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try { while (true) (await listener.AcceptTcpClientAsync()).Dispose(); }
            catch { /* swallow */ }
        });

        try
        {
            var alive = BrokerUrl.Parse($"mqtts://127.0.0.1:{port}");
            var dead  = BrokerUrl.Parse("mqtts://192.0.2.1:8883");

            var (sorted, rtts) = await BrokerProbe.ReorderWithRttsAsync(
                new[] { dead, alive },
                TimeSpan.FromMilliseconds(500));

            sorted.First().Should().Be(alive);
            rtts.Should().ContainKey("127.0.0.1");
            rtts.Should().NotContainKey("192.0.2.1"); // failed probes excluded from rtt map
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void FormatOrder_RendersAliveAsMs_FailedAsFail()
    {
        var u1 = BrokerUrl.Parse("mqtts://yc.example:8883");
        var u2 = BrokerUrl.Parse("mqtts://sber.example:8883");

        var rtts = new Dictionary<string, int> { ["yc.example"] = 42 };
        var line = BrokerProbe.FormatOrder(new[] { u1, u2 }, rtts);

        line.Should().Be("yc.example=42ms sber.example=fail");
    }
}

public class TcpRttTests
{
    [Fact]
    public async Task MeasureAsync_LiveListener_ReturnsNonNegativeMs()
    {
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try { while (true) (await listener.AcceptTcpClientAsync()).Dispose(); }
            catch { /* swallow */ }
        });

        try
        {
            var ms = await TcpRtt.MeasureAsync("127.0.0.1", port, TimeSpan.FromSeconds(1));
            ms.Should().NotBeNull();
            ms!.Value.Should().BeGreaterThanOrEqualTo(0);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task MeasureAsync_Unreachable_ReturnsNullWithinTimeout()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ms = await TcpRtt.MeasureAsync("192.0.2.1", 8883, TimeSpan.FromMilliseconds(500));
        sw.Stop();
        ms.Should().BeNull();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }
}
