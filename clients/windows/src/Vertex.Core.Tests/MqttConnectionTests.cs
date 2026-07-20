using System.Threading.Channels;
using FluentAssertions;
using Vertex.Core.Config;
using Vertex.Core.Transport;
using Xunit;

namespace Vertex.Core.Tests;

/// <summary>
/// In-memory broker harness. Pumps bytes between a fake <see cref="IMqttSocket"/>
/// (consumed by <see cref="MqttConnection"/>) and a "broker side" pair of
/// channels that the tests can read from / write into. Lets us exercise the
/// CONNECT → CONNACK → PUBLISH / SUBSCRIBE / DISCONNECT lifecycle without
/// any network I/O.
/// </summary>
internal sealed class FakeMqttSocket : IMqttSocket
{
    public BrokerUrl Broker { get; }

    public Channel<byte[]> ClientWrites { get; } = Channel.CreateUnbounded<byte[]>();
    public Channel<byte[]> ClientReads  { get; } = Channel.CreateUnbounded<byte[]>();

    /// <summary>If non-null, the next <see cref="SendAsync"/> throws this and clears the field.</summary>
    public Exception? FailNextSendWith { get; set; }

    private byte[] _carry = Array.Empty<byte>();

    public FakeMqttSocket(BrokerUrl broker) => Broker = broker;

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        if (FailNextSendWith is { } ex)
        {
            FailNextSendWith = null;
            throw ex;
        }
        ClientWrites.Writer.TryWrite(packet.ToArray());
        return Task.CompletedTask;
    }

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_carry.Length == 0)
        {
            byte[] next;
            try
            {
                next = await ClientReads.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException) { return 0; }

            _carry = next;
        }

        int n = Math.Min(buffer.Length, _carry.Length);
        _carry.AsMemory(0, n).CopyTo(buffer);
        _carry = _carry.AsSpan(n).ToArray();
        return n;
    }

    public Task CloseAsync()
    {
        ClientWrites.Writer.TryComplete();
        ClientReads.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class MqttConnectionTests
{
    private static readonly BrokerUrl AnyBroker = BrokerUrl.Parse("mqtts://test-broker:8883");

    [Fact]
    public async Task ConnectAsync_SendsConnectPacketAndReportsConnectedAfterConnack()
    {
        await using var fake = new FakeMqttSocket(AnyBroker);
        var conn = new MqttConnection(fake, "vtx-test", "user", "pass", keepAliveSeconds: 20);

        var connectedSignal = new TaskCompletionSource();
        conn.OnEvent = ev =>
        {
            if (ev is MqttConnectionEvent.Connected) connectedSignal.TrySetResult();
        };

        await conn.ConnectAsync();

        // Drain client side until we see the CONNECT packet.
        var bytes = await fake.ClientWrites.Reader.ReadAsync();
        bytes[0].Should().Be(0x10); // CONNECT type byte

        // Inject a CONNACK success reply.
        await fake.ClientReads.Writer.WriteAsync(new byte[] { 0x20, 0x03, 0x00, 0x00, 0x00 });

        await connectedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        conn.IsConnected.Should().BeTrue();

        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Connack_AuthFailure_EmitsDisconnectedWithConnackReason()
    {
        await using var fake = new FakeMqttSocket(AnyBroker);
        var conn = new MqttConnection(fake, "vtx-test", "user", "wrong");

        var doneSignal = new TaskCompletionSource<MqttConnectionEvent.Disconnected>();
        conn.OnEvent = ev =>
        {
            if (ev is MqttConnectionEvent.Disconnected d) doneSignal.TrySetResult(d);
        };

        await conn.ConnectAsync();
        // Drop the CONNECT packet from the client side, inject a CONNACK 0x86.
        _ = await fake.ClientWrites.Reader.ReadAsync();
        await fake.ClientReads.Writer.WriteAsync(new byte[] { 0x20, 0x03, 0x00, 0x86, 0x00 });

        var disconnect = await doneSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        disconnect.ConnackReason.Should().Be((byte)0x86);
        disconnect.LinkDead.Should().BeFalse();
        disconnect.Cause.Should().BeOfType<MqttCodecException>();
    }

    [Fact]
    public async Task Publish_AfterConnack_FramesPublishOnTheWire()
    {
        await using var fake = new FakeMqttSocket(AnyBroker);
        var conn = new MqttConnection(fake, "vtx-test", "user", "pass");

        var connected = new TaskCompletionSource();
        conn.OnEvent = ev =>
        {
            if (ev is MqttConnectionEvent.Connected) connected.TrySetResult();
        };

        await conn.ConnectAsync();
        _ = await fake.ClientWrites.Reader.ReadAsync(); // CONNECT
        await fake.ClientReads.Writer.WriteAsync(new byte[] { 0x20, 0x03, 0x00, 0x00, 0x00 });
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await conn.PublishAsync("vpn/aws/test/out", System.Text.Encoding.UTF8.GetBytes("ping"));

        var pubBytes = await fake.ClientWrites.Reader.ReadAsync();
        pubBytes[0].Should().Be(0x30); // PUBLISH

        var (type, packet, _) = MqttPacketCodec.TryDecode(pubBytes)!.Value;
        type.Should().Be(MqttPacketType.Publish);
        var pub = MqttPacketCodec.DecodePublish(packet);
        pub.Topic.Should().Be("vpn/aws/test/out");
        System.Text.Encoding.UTF8.GetString(pub.Payload).Should().Be("ping");

        await conn.DisposeAsync();
    }

    [Fact]
    public async Task IncomingPublish_InvokesOnPublishCallback()
    {
        await using var fake = new FakeMqttSocket(AnyBroker);
        var conn = new MqttConnection(fake, "vtx-test", "user", "pass");

        var connected = new TaskCompletionSource();
        var pub = new TaskCompletionSource<(string topic, byte[] payload)>();

        conn.OnEvent = ev => { if (ev is MqttConnectionEvent.Connected) connected.TrySetResult(); };
        conn.OnPublish = (t, p) => pub.TrySetResult((t, p));

        await conn.ConnectAsync();
        _ = await fake.ClientWrites.Reader.ReadAsync();
        await fake.ClientReads.Writer.WriteAsync(new byte[] { 0x20, 0x03, 0x00, 0x00, 0x00 });
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Inject a PUBLISH from the broker side.
        var inbound = MqttPacketCodec.EncodePublish(new PublishPacket(
            "vpn/aws/test/in",
            System.Text.Encoding.UTF8.GetBytes("hello"),
            Retain: false,
            MessageExpirySeconds: null));
        await fake.ClientReads.Writer.WriteAsync(inbound);

        var got = await pub.Task.WaitAsync(TimeSpan.FromSeconds(2));
        got.topic.Should().Be("vpn/aws/test/in");
        System.Text.Encoding.UTF8.GetString(got.payload).Should().Be("hello");

        await conn.DisposeAsync();
    }

    [Fact]
    public async Task PublishSendError_DeclaresLinkDead()
    {
        await using var fake = new FakeMqttSocket(AnyBroker);
        var conn = new MqttConnection(fake, "vtx-test", "user", "pass");

        var connected = new TaskCompletionSource();
        var done = new TaskCompletionSource<MqttConnectionEvent.Disconnected>();
        conn.OnEvent = ev =>
        {
            if (ev is MqttConnectionEvent.Connected) connected.TrySetResult();
            if (ev is MqttConnectionEvent.Disconnected d) done.TrySetResult(d);
        };

        await conn.ConnectAsync();
        _ = await fake.ClientWrites.Reader.ReadAsync();
        await fake.ClientReads.Writer.WriteAsync(new byte[] { 0x20, 0x03, 0x00, 0x00, 0x00 });
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Inject a send-side failure on the next publish.
        var thrown = new IOException("simulated socket failure");
        fake.FailNextSendWith = thrown;
        await conn.PublishAsync("vpn/aws/test/out", new byte[] { 1, 2, 3 });

        var disc = await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        disc.LinkDead.Should().BeTrue();
        disc.Cause.Should().BeSameAs(thrown);
    }

    [Fact]
    public async Task IncomingMalformedConnack_TearsDownWithCodecException()
    {
        await using var fake = new FakeMqttSocket(AnyBroker);
        var conn = new MqttConnection(fake, "vtx-test", "user", "pass");

        var done = new TaskCompletionSource<MqttConnectionEvent.Disconnected>();
        conn.OnEvent = ev =>
        {
            if (ev is MqttConnectionEvent.Disconnected d) done.TrySetResult(d);
        };

        await conn.ConnectAsync();
        _ = await fake.ClientWrites.Reader.ReadAsync();

        // CONNACK truncated to one body byte: TryDecode accepts the
        // packet (remaining-length=1 is well-formed); DecodeConnack throws
        // MqttCodecException("CONNACK truncated") because the variable
        // header needs at least 2 bytes (ack-flags + reason-code).
        var badConnack = new byte[]
        {
            0x20, 0x01,             // CONNACK, remaining=1
            0x00,                    // only ack-flags — reason-code missing
        };
        await fake.ClientReads.Writer.WriteAsync(badConnack);

        var disc = await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        disc.LinkDead.Should().BeFalse();
        disc.Cause.Should().BeOfType<MqttCodecException>();
    }

    [Fact]
    public async Task SubscribePacketIds_StartAtOne_AndWrapAroundFromMaxUshortToOne()
    {
        await using var fake = new FakeMqttSocket(AnyBroker);
        var conn = new MqttConnection(fake, "vtx-test", "user", "pass");

        var connected = new TaskCompletionSource();
        conn.OnEvent = ev => { if (ev is MqttConnectionEvent.Connected) connected.TrySetResult(); };

        await conn.ConnectAsync();
        _ = await fake.ClientWrites.Reader.ReadAsync();
        await fake.ClientReads.Writer.WriteAsync(new byte[] { 0x20, 0x03, 0x00, 0x00, 0x00 });
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // First three SUBSCRIBEs must carry packetIds 1, 2, 3 (parity with Swift/Kotlin).
        for (ushort expected = 1; expected <= 3; expected++)
        {
            await conn.SubscribeAsync(new[] { $"vpn/+/c/in" });
            var bytes = await fake.ClientWrites.Reader.ReadAsync();
            var (type, packet, _) = MqttPacketCodec.TryDecode(bytes)!.Value;
            type.Should().Be(MqttPacketType.Subscribe);
            // SUBACK's packet ID lives at offset 2 (skip type+remainingLength → 2 bytes UInt16 BE).
            // For our SUBSCRIBE encode, the variable header is packetId at the same offset.
            int afterFixedHeader = 1 + (bytes[1] < 128 ? 1 : 2); // remaining-length is single byte for these small packets
            ushort packetId = (ushort)((bytes[afterFixedHeader] << 8) | bytes[afterFixedHeader + 1]);
            packetId.Should().Be(expected);
        }

        await conn.DisposeAsync();
    }

    [Fact]
    public async Task BrokerSideDisconnect_PropagatesAsCleanDisconnected()
    {
        await using var fake = new FakeMqttSocket(AnyBroker);
        var conn = new MqttConnection(fake, "vtx-test", "user", "pass");

        var done = new TaskCompletionSource<MqttConnectionEvent.Disconnected>();
        conn.OnEvent = ev =>
        {
            if (ev is MqttConnectionEvent.Disconnected d) done.TrySetResult(d);
        };

        await conn.ConnectAsync();
        _ = await fake.ClientWrites.Reader.ReadAsync();
        await fake.ClientReads.Writer.WriteAsync(new byte[] { 0x20, 0x03, 0x00, 0x00, 0x00 });

        // Wait until the connection settled before injecting DISCONNECT.
        for (int i = 0; i < 50 && !conn.IsConnected; i++) await Task.Delay(20);
        conn.IsConnected.Should().BeTrue();

        // Broker sends DISCONNECT (type=0xE0, remaining=2, reason=0, propLen=0).
        await fake.ClientReads.Writer.WriteAsync(new byte[] { 0xE0, 0x02, 0x00, 0x00 });

        var disc = await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        disc.LinkDead.Should().BeFalse();
        disc.ConnackReason.Should().BeNull();
        disc.Cause.Should().BeNull();
    }
}
