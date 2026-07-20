using FluentAssertions;
using Vertex.Core.Config;
using Vertex.Core.Transport;
using Xunit;

namespace Vertex.Core.Tests;

public class MqttTransportTests
{
    private static readonly BrokerUrl Yc   = BrokerUrl.Parse("mqtts://yc.example:8883");
    private static readonly BrokerUrl Sber = BrokerUrl.Parse("mqtts://sber.example:8883");

    /// <summary>Helper: simulate a broker accepting CONNECT and replying CONNACK success.</summary>
    private static async Task ReplyConnackSuccessAsync(FakeMqttSocket socket, CancellationToken ct = default)
    {
        // Wait until the transport sent CONNECT before replying.
        _ = await socket.ClientWrites.Reader.ReadAsync(ct).ConfigureAwait(false);
        await socket.ClientReads.Writer.WriteAsync(new byte[] { 0x20, 0x03, 0x00, 0x00, 0x00 }, ct).ConfigureAwait(false);
    }

    private sealed class CapturingSocketFactory
    {
        public readonly List<FakeMqttSocket> Created = new();

        public IMqttSocket Create(BrokerUrl b)
        {
            var s = new FakeMqttSocket(b);
            lock (Created) Created.Add(s);
            return s;
        }

        public FakeMqttSocket Wait(int index, TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                lock (Created)
                {
                    if (Created.Count > index) return Created[index];
                }
                Thread.Sleep(20);
            }
            throw new TimeoutException($"Socket {index} not created within {timeout}.");
        }
    }

    [Fact]
    public async Task Start_FirstBrokerSucceeds_ReachesConnected()
    {
        var factory = new CapturingSocketFactory();
        await using var tx = new MqttTransport(
            new[] { Yc, Sber }, "user", "pass", "vtx-test",
            socketFactory: factory.Create);

        var startTask = tx.StartAsync();
        var sock = factory.Wait(0, TimeSpan.FromSeconds(2));
        await ReplyConnackSuccessAsync(sock);

        await startTask.WaitAsync(TimeSpan.FromSeconds(2));
        tx.IsReady.Should().BeTrue();
        tx.CurrentBroker.Should().Be("yc.example");

        await tx.StopAsync();
    }

    [Fact]
    public async Task Start_AuthFailure_ShortCircuitsRetryAndInvokesCallback()
    {
        byte? captured = null;
        string? captureStr = null;
        var factory = new CapturingSocketFactory();

        await using var tx = new MqttTransport(
            new[] { Yc, Sber }, "user", "wrong", "vtx-test",
            socketFactory: factory.Create,
            onAuthFailure: (rc, s) => { captured = rc; captureStr = s; });

        var startTask = tx.StartAsync();
        var sock = factory.Wait(0, TimeSpan.FromSeconds(2));

        // Drop client's CONNECT; reply CONNACK with reason 0x86 ("Bad username/password").
        _ = await sock.ClientWrites.Reader.ReadAsync();
        await sock.ClientReads.Writer.WriteAsync(new byte[] { 0x20, 0x03, 0x00, 0x86, 0x00 });

        // StartAsync should reject with TransportException.
        await Assert.ThrowsAsync<TransportException>(() => startTask.WaitAsync(TimeSpan.FromSeconds(2)));

        // Callback fired with the right reason.
        for (int i = 0; i < 50 && captured is null; i++) await Task.Delay(20);
        captured.Should().Be((byte)0x86);
        captureStr.Should().Be("Bad username or password");

        // No retry: factory was only invoked once.
        await Task.Delay(200);
        lock (factory.Created) factory.Created.Count.Should().Be(1);
    }

    [Fact]
    public async Task Start_FirstBrokerFailsTransport_FailsOverToSecondAndStickyMovesItToIndex0()
    {
        var factory = new CapturingSocketFactory();

        await using var tx = new MqttTransport(
            new[] { Yc, Sber }, "user", "pass", "vtx-test",
            socketFactory: factory.Create);

        var startTask = tx.StartAsync();

        // First socket: simulate transport-level failure by closing reads
        // immediately (ReceiveAsync returns 0 → MqttConnection emits clean
        // Disconnected with cause=null, linkDead=false → Transport schedules
        // reconnect on next broker).
        var first = factory.Wait(0, TimeSpan.FromSeconds(2));
        _ = await first.ClientWrites.Reader.ReadAsync(); // drain CONNECT
        first.ClientReads.Writer.TryComplete();

        // Second socket: accept CONNECT and reply CONNACK success.
        var second = factory.Wait(1, TimeSpan.FromSeconds(5));
        await ReplyConnackSuccessAsync(second);

        await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Sticky reconnect: the winning broker (sber.example) is now at index 0.
        tx.CurrentBroker.Should().Be("sber.example");

        await tx.StopAsync();
    }

    [Fact]
    public async Task Subscribe_BeforeStart_AppliesAfterConnack()
    {
        var factory = new CapturingSocketFactory();
        await using var tx = new MqttTransport(
            new[] { Yc }, "user", "pass", "vtx-test",
            socketFactory: factory.Create);

        var received = new List<(string topic, byte[] payload)>();
        tx.Subscribe("vpn/+/iphone/in", (t, p) => { lock (received) received.Add((t, p)); });

        var startTask = tx.StartAsync();
        var sock = factory.Wait(0, TimeSpan.FromSeconds(2));
        await ReplyConnackSuccessAsync(sock);
        await startTask.WaitAsync(TimeSpan.FromSeconds(2));

        // We should see a SUBSCRIBE for the registered pattern (after CONNECT).
        // Drain any pending writes; assert we eventually saw a SUBSCRIBE.
        bool sawSubscribe = false;
        for (int i = 0; i < 50 && !sawSubscribe; i++)
        {
            if (sock.ClientWrites.Reader.TryRead(out var bytes) && bytes[0] == 0x82)
            {
                sawSubscribe = true;
                break;
            }
            await Task.Delay(20);
        }
        sawSubscribe.Should().BeTrue();

        // Inject a matching PUBLISH from the broker side.
        var inbound = MqttPacketCodec.EncodePublish(new PublishPacket(
            "vpn/aws/iphone/in",
            System.Text.Encoding.UTF8.GetBytes("hello"),
            Retain: false,
            MessageExpirySeconds: null));
        await sock.ClientReads.Writer.WriteAsync(inbound);

        for (int i = 0; i < 50 && received.Count == 0; i++) await Task.Delay(20);
        received.Should().HaveCount(1);
        received[0].topic.Should().Be("vpn/aws/iphone/in");

        // Inject a non-matching PUBLISH; should NOT increment the list.
        var nonMatching = MqttPacketCodec.EncodePublish(new PublishPacket(
            "vpn/aws/macbook/in",
            System.Text.Encoding.UTF8.GetBytes("ignored"),
            Retain: false,
            MessageExpirySeconds: null));
        await sock.ClientReads.Writer.WriteAsync(nonMatching);
        await Task.Delay(100);
        received.Should().HaveCount(1);

        await tx.StopAsync();
    }

    [Fact]
    public async Task MultipleStateUpdatesSubscribers_AllReceiveTransitions()
    {
        var factory = new CapturingSocketFactory();
        await using var tx = new MqttTransport(
            new[] { Yc }, "user", "pass", "vtx-test",
            socketFactory: factory.Create);

        // Two independent subscribers attached BEFORE start; both must see
        // Disconnected (replayed) and the subsequent Connecting / Connected
        // events — paritет с Kotlin StateFlow replay=1 + broadcast.
        var seenA = new List<string>();
        var seenB = new List<string>();

        async Task Drain(List<string> seen, CancellationToken ct)
        {
            await foreach (var s in tx.StateUpdates.WithCancellation(ct).ConfigureAwait(false))
            {
                lock (seen) seen.Add(s.GetType().Name);
                if (s is TransportState.Connected) return;
            }
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var taskA = Task.Run(() => Drain(seenA, cts.Token));
        var taskB = Task.Run(() => Drain(seenB, cts.Token));

        // Wait until both drain tasks have observed the replayed
        // Disconnected state — that's our signal they're fully
        // subscribed. A blind Task.Delay flakes on ARM64 testhost
        // under load.
        for (int i = 0; i < 100; i++)
        {
            int aCount, bCount;
            lock (seenA) aCount = seenA.Count;
            lock (seenB) bCount = seenB.Count;
            if (aCount >= 1 && bCount >= 1) break;
            await Task.Delay(20);
        }

        var startTask = tx.StartAsync();
        var sock = factory.Wait(0, TimeSpan.FromSeconds(2));
        await ReplyConnackSuccessAsync(sock);
        await startTask.WaitAsync(TimeSpan.FromSeconds(2));

        // Wait for BOTH drain tasks to terminate (each returns once it
        // sees Connected). Earlier version raced via a shared TCS
        // that fired on the first drain, letting the assertion run
        // before the second subscriber received its event.
        await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(3));
        lock (seenA) seenA.Should().Contain("Connected");
        lock (seenB) seenB.Should().Contain("Connected");

        await tx.StopAsync();
    }

    [Fact]
    public async Task Unsubscribe_StopsLocalDispatchEvenIfBrokerKeepsDelivering()
    {
        var factory = new CapturingSocketFactory();
        await using var tx = new MqttTransport(
            new[] { Yc }, "user", "pass", "vtx-test",
            socketFactory: factory.Create);

        var received = new List<string>();
        tx.Subscribe("vpn/+/iphone/in", (t, _) => { lock (received) received.Add(t); });

        var startTask = tx.StartAsync();
        var sock = factory.Wait(0, TimeSpan.FromSeconds(2));
        await ReplyConnackSuccessAsync(sock);
        await startTask.WaitAsync(TimeSpan.FromSeconds(2));

        // Drain the SUBSCRIBE that went out automatically.
        for (int i = 0; i < 50; i++)
        {
            if (sock.ClientWrites.Reader.TryRead(out var bytes) && bytes[0] == 0x82) break;
            await Task.Delay(10);
        }

        // First publish — handler fires.
        await sock.ClientReads.Writer.WriteAsync(MqttPacketCodec.EncodePublish(
            new PublishPacket("vpn/aws/iphone/in", new byte[] { 1 }, false, null)));
        for (int i = 0; i < 50 && received.Count == 0; i++) await Task.Delay(10);
        received.Should().HaveCount(1);

        tx.Unsubscribe("vpn/+/iphone/in");
        // Wait for Unsubscribe to settle on the work queue.
        await Task.Delay(50);

        // Second publish from the broker side — handler should NOT fire
        // even though we never sent UNSUBSCRIBE on the wire (broker keeps
        // delivering, but local dispatch is gated by the map).
        await sock.ClientReads.Writer.WriteAsync(MqttPacketCodec.EncodePublish(
            new PublishPacket("vpn/aws/iphone/in", new byte[] { 2 }, false, null)));
        await Task.Delay(150);
        received.Should().HaveCount(1);

        await tx.StopAsync();
    }

    [Fact]
    public async Task AuthFailure_OnStickyBroker_DemotesItSoNextStartTriesPrimary()
    {
        var factory = new CapturingSocketFactory();

        // Two-broker setup: YC primary, Sber backup. Auth-fail on
        // index 0 (YC) should demote it so the broker order becomes
        // [Sber, YC].
        await using var tx = new MqttTransport(
            new[] { Yc, Sber }, "user", "pass", "vtx-test",
            socketFactory: factory.Create,
            onAuthFailure: (_, _) => { /* swallow */ });

        var startTask = tx.StartAsync();
        var sock = factory.Wait(0, TimeSpan.FromSeconds(2));
        _ = await sock.ClientWrites.Reader.ReadAsync();
        await sock.ClientReads.Writer.WriteAsync(new byte[] { 0x20, 0x03, 0x00, 0x86, 0x00 });

        await Assert.ThrowsAsync<TransportException>(() => startTask.WaitAsync(TimeSpan.FromSeconds(2)));

        // Re-StartAsync — first socket created in this round must point
        // at Sber (the un-stickied YC was demoted to the tail).
        var startTask2 = tx.StartAsync();
        var second = factory.Wait(1, TimeSpan.FromSeconds(2));
        second.Broker.Host.Should().Be("sber.example");

        await tx.StopAsync();
        try { await startTask2; } catch { /* swallow — we never replied CONNACK */ }
    }

    [Fact]
    public async Task WaitReadyAsync_TimesOutWhenBrokerNeverAcks()
    {
        var factory = new CapturingSocketFactory();
        await using var tx = new MqttTransport(
            new[] { Yc }, "user", "pass", "vtx-test",
            socketFactory: factory.Create);

        // Fire-and-forget StartAsync — never fulfilled because we never
        // reply CONNACK on the fake socket.
        _ = tx.StartAsync();
        _ = factory.Wait(0, TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<TransportException>(
            () => tx.WaitReadyAsync(TimeSpan.FromMilliseconds(200)));

        await tx.StopAsync();
    }
}
