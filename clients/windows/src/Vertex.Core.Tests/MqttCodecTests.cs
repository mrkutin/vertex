using FluentAssertions;
using Vertex.Core.Transport;
using Xunit;

namespace Vertex.Core.Tests;

/// <summary>
/// Round-trip and shape tests for the MQTT 5.0 codec. Mirrors the Swift
/// <c>MQTTCodecTests</c> and Kotlin <c>MqttCodecTest</c> fixtures so the
/// four implementations stay byte-for-byte compatible on the wire.
/// </summary>
public class MqttCodecTests
{
    [Fact]
    public void PingReq_IsTwoBytes()
    {
        MqttPacketCodec.EncodePingReq().Should().Equal(new byte[] { 0xC0, 0x00 });
    }

    [Fact]
    public void Disconnect_IsFourBytes()
    {
        MqttPacketCodec.EncodeDisconnect().Should().Equal(new byte[] { 0xE0, 0x02, 0x00, 0x00 });
    }

    [Fact]
    public void Connect_RoundTrip_FirstByteAndRemainingLengthAddUp()
    {
        var pkt = new ConnectPacket(
            ClientId: "vtx-test",
            Username: "vtx-client-test",
            Password: "secret",
            KeepAlive: 20,
            CleanStart: true,
            SessionExpiryInterval: 0);

        var bytes = MqttPacketCodec.EncodeConnect(pkt);

        // First byte: 0x10 (CONNECT << 4).
        bytes[0].Should().Be(0x10);

        // Remaining length + header bytes equal to total packet length.
        var (rl, off) = MqttPacketCodec.ReadVariableInt(bytes, 1);
        ((int)rl + off).Should().Be(bytes.Length);
    }

    [Fact]
    public void Publish_QoS0_RoundTrip_ViaTryDecodeThenDecodePublish()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("hello-vertex");
        var outbound = MqttPacketCodec.EncodePublish(new PublishPacket(
            Topic: "vpn/aws/test/in",
            Payload: payload,
            Retain: false,
            MessageExpirySeconds: 10));

        // First byte: 0x30 (PUBLISH << 4, no retain, QoS 0).
        outbound[0].Should().Be(0x30);

        var triple = MqttPacketCodec.TryDecode(outbound);
        triple.Should().NotBeNull();
        triple!.Value.Type.Should().Be(MqttPacketType.Publish);
        triple.Value.Consumed.Should().Be(outbound.Length);

        var decoded = MqttPacketCodec.DecodePublish(triple.Value.Packet);
        decoded.Topic.Should().Be("vpn/aws/test/in");
        decoded.Payload.Should().Equal(payload);
    }

    [Fact]
    public void TryDecode_ReturnsNull_OnPartialPacket()
    {
        var full = MqttPacketCodec.EncodePublish(new PublishPacket(
            Topic: "t",
            Payload: Enumerable.Repeat((byte)0x42, 64).ToArray(),
            Retain: false,
            MessageExpirySeconds: null));

        var partial = full[..^4];
        MqttPacketCodec.TryDecode(partial).Should().BeNull();
    }

    [Fact]
    public void TryDecode_ConsumesOnlyOnePacket_FromConcatenatedStream()
    {
        var a = MqttPacketCodec.EncodePublish(new PublishPacket("a", System.Text.Encoding.UTF8.GetBytes("hi"), false, null));
        var b = MqttPacketCodec.EncodePublish(new PublishPacket("b", System.Text.Encoding.UTF8.GetBytes("yo"), false, null));
        var both = a.Concat(b).ToArray();

        var first = MqttPacketCodec.TryDecode(both);
        first.Should().NotBeNull();
        first!.Value.Consumed.Should().Be(a.Length);

        var second = MqttPacketCodec.TryDecode(both.AsSpan(first.Value.Consumed));
        second.Should().NotBeNull();
        second!.Value.Consumed.Should().Be(b.Length);
    }

    [Fact]
    public void Connack_Decode_Success_NoServerKeepAlive()
    {
        // type=2 << 4 = 0x20, remaining=3, ackFlags=0, reasonCode=0, propLen=0.
        var bytes = new byte[] { 0x20, 0x03, 0x00, 0x00, 0x00 };

        var ack = MqttPacketCodec.DecodeConnack(bytes);

        ack.IsSuccess.Should().BeTrue();
        ack.ServerKeepAlive.Should().BeNull();
        ack.SessionPresent.Should().BeFalse();
    }

    [Fact]
    public void Connack_Decode_WithServerKeepAliveProperty()
    {
        // ackFlags=0, reason=0, propLen=3, propID=0x13 (server keepalive), value 0x0014 = 20.
        var bytes = new byte[] { 0x20, 0x06, 0x00, 0x00, 0x03, 0x13, 0x00, 0x14 };

        var ack = MqttPacketCodec.DecodeConnack(bytes);

        ack.IsSuccess.Should().BeTrue();
        ack.ServerKeepAlive.Should().Be((ushort)20);
    }

    [Fact]
    public void Connack_Decode_AuthFailureReasonCode()
    {
        var bytes = new byte[] { 0x20, 0x03, 0x00, 0x86, 0x00 };

        var ack = MqttPacketCodec.DecodeConnack(bytes);

        ack.ReasonCode.Should().Be((byte)0x86);
        ack.IsSuccess.Should().BeFalse();
        ack.ReasonString.Should().Be("Bad username or password");
    }

    [Fact]
    public void Subscribe_HasFlagBit2_PerMqtt5Spec()
    {
        var bytes = MqttPacketCodec.EncodeSubscribe(new SubscribePacket(
            PacketId: 1,
            Topics: new[] { "vpn/+/iphone/in", "vpn/+/iphone/control" }));

        // First byte: 0x82 (SUBSCRIBE << 4 | flags=0x02).
        bytes[0].Should().Be(0x82);
    }

    [Fact]
    public void Suback_Decode_ReadsReasonCodes()
    {
        // SUBACK header (0x90), remaining=4, packetId=0x0001, propLen=0, reasonCode=0
        var bytes = new byte[] { 0x90, 0x04, 0x00, 0x01, 0x00, 0x00 };

        var ack = MqttPacketCodec.DecodeSuback(bytes);

        ack.PacketId.Should().Be((ushort)1);
        ack.ReasonCodes.Should().Equal(new byte[] { 0x00 });
        ack.AllSuccess.Should().BeTrue();
    }

    [Fact]
    public void EncodeSubscribe_EmptyTopicList_Throws()
    {
        Action act = () => MqttPacketCodec.EncodeSubscribe(new SubscribePacket(1, Array.Empty<string>()));
        act.Should().Throw<ArgumentException>().WithMessage("*at least one topic filter*");
    }

    [Fact]
    public void DecodePublish_PropLenOvershoot_Throws()
    {
        // PUBLISH (0x30), remaining=5, topic length=1, topic='a', propLen=2 (claims 2 props bytes)
        // — but only 0 bytes remain after propLen byte, so this overshoots.
        var bytes = new byte[]
        {
            0x30, 0x05,             // fixed header (type=publish, remaining=5)
            0x00, 0x01, (byte)'a',   // topic ("a")
            0x02,                    // propLen=2 — overshoots, no bytes follow
        };

        Action act = () => MqttPacketCodec.DecodePublish(bytes);
        act.Should().Throw<MqttCodecException>().WithMessage("*PUBLISH properties length 2 overshoots*");
    }

    [Fact]
    public void DecodeSuback_PropLenOvershoot_Throws()
    {
        // SUBACK (0x90), remaining=3, packetId=0x0001, propLen=4 — claims 4 prop bytes but none follow.
        var bytes = new byte[] { 0x90, 0x03, 0x00, 0x01, 0x04 };

        Action act = () => MqttPacketCodec.DecodeSuback(bytes);
        act.Should().Throw<MqttCodecException>().WithMessage("*SUBACK properties length 4 overshoots*");
    }
}
