using System.Buffers.Binary;
using System.Text;

namespace Vertex.Core.Transport;

/// <summary>
/// MQTT 5.0 binary packet encoder/decoder for the minimal VPN subset.
/// Supported: CONNECT, CONNACK, PUBLISH (QoS 0), SUBSCRIBE (QoS 0),
/// SUBACK, PINGREQ, PINGRESP, DISCONNECT.
///
/// Wire format is byte-for-byte identical to Swift <c>MQTTPacketCodec</c>
/// (<c>clients/shared/VertexCore</c>) and Kotlin <c>MqttPacketCodec</c>
/// (<c>clients/android/.../core/mqtt</c>) — keep in sync if any of those
/// changes.
/// </summary>
public static class MqttPacketCodec
{
    // ---------- Encode ----------

    /// <summary>Encode a CONNECT packet (MQTT 5.0).</summary>
    public static byte[] EncodeConnect(ConnectPacket pkt)
    {
        var vh = new MemoryStream();

        // Protocol Name "MQTT", version 5.
        AppendUtf8String(vh, "MQTT");
        vh.WriteByte(5);

        // Connect flags.
        byte flags = 0;
        if (pkt.CleanStart)        flags |= 0x02;
        if (pkt.Username != null)  flags |= 0x80;
        if (pkt.Password != null)  flags |= 0x40;
        vh.WriteByte(flags);

        // Keep Alive (UInt16 BE).
        AppendUInt16(vh, pkt.KeepAlive);

        // Properties.
        var props = new MemoryStream();
        if (pkt.SessionExpiryInterval is uint sei)
        {
            props.WriteByte(MqttPropertyId.SessionExpiryInterval);
            AppendUInt32(props, sei);
        }
        AppendVariableInt(vh, (uint)props.Length);
        props.Position = 0;
        props.CopyTo(vh);

        // Payload.
        var payload = new MemoryStream();
        AppendUtf8String(payload, pkt.ClientId);
        if (pkt.Username is { } u) AppendUtf8String(payload, u);
        if (pkt.Password is { } p) AppendUtf8String(payload, p);

        return BuildPacket(MqttPacketType.Connect, flags: 0, vh.ToArray(), payload.ToArray());
    }

    /// <summary>Encode a PUBLISH packet (QoS 0, no packet ID).</summary>
    public static byte[] EncodePublish(PublishPacket pkt)
    {
        var vh = new MemoryStream();
        AppendUtf8String(vh, pkt.Topic);

        var props = new MemoryStream();
        if (pkt.MessageExpirySeconds is uint expiry)
        {
            props.WriteByte(MqttPropertyId.MessageExpiryInterval);
            AppendUInt32(props, expiry);
        }
        AppendVariableInt(vh, (uint)props.Length);
        props.Position = 0;
        props.CopyTo(vh);

        byte flags = pkt.Retain ? (byte)0x01 : (byte)0x00;
        return BuildPacket(MqttPacketType.Publish, flags, vh.ToArray(), pkt.Payload);
    }

    /// <summary>Encode a SUBSCRIBE packet (QoS 0). MQTT 5.0 mandates fixed-header flags=0x02 here.</summary>
    public static byte[] EncodeSubscribe(SubscribePacket pkt)
    {
        // MQTT 5.0 §3.8.3 — at least one Topic Filter is required; a
        // payload-less SUBSCRIBE is a protocol error and the broker will
        // disconnect us. Catch the misuse here rather than over the wire.
        if (pkt.Topics.Count == 0)
        {
            throw new ArgumentException("SUBSCRIBE requires at least one topic filter.", nameof(pkt));
        }

        var vh = new MemoryStream();
        AppendUInt16(vh, pkt.PacketId);
        vh.WriteByte(0x00); // properties length = 0

        var payload = new MemoryStream();
        foreach (var topic in pkt.Topics)
        {
            AppendUtf8String(payload, topic);
            payload.WriteByte(0x00); // QoS 0, no options
        }

        return BuildPacket(MqttPacketType.Subscribe, flags: 0x02, vh.ToArray(), payload.ToArray());
    }

    /// <summary>PINGREQ — 2 bytes total: <c>0xC0 0x00</c>.</summary>
    public static byte[] EncodePingReq() => new byte[] { 0xC0, 0x00 };

    /// <summary>DISCONNECT (reason=normal): <c>0xE0 0x02 0x00 0x00</c>.</summary>
    public static byte[] EncodeDisconnect() => new byte[] { 0xE0, 0x02, 0x00, 0x00 };

    // ---------- Decode ----------

    /// <summary>
    /// Try to decode one complete packet from the buffer. Returns
    /// <c>(type, packetSlice, bytesConsumed)</c> or <c>null</c> if the
    /// buffer is incomplete.
    /// </summary>
    public static (MqttPacketType Type, byte[] Packet, int Consumed)? TryDecode(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 2) return null;

        byte firstByte = buffer[0];
        byte typeRaw = (byte)(firstByte >> 4);

        // Variable-length remaining length.
        uint multiplier = 1;
        uint remainingLength = 0;
        int offset = 1;

        while (offset < buffer.Length)
        {
            byte b = buffer[offset];
            remainingLength += (uint)(b & 0x7F) * multiplier;
            offset++;
            if ((b & 0x80) == 0) break;
            multiplier *= 128;
            if (multiplier > 128 * 128 * 128) return null; // max 4 bytes
            if (offset >= buffer.Length) return null;       // need more bytes
        }

        // The loop guarantees `offset >= 2` here (we only enter once `buffer.Length >= 2`,
        // and the loop body always increments offset before either breaking or returning).
        int headerSize = offset;
        int totalSize = headerSize + (int)remainingLength;
        if (buffer.Length < totalSize) return null;

        if (!Enum.IsDefined(typeof(MqttPacketType), typeRaw)) return null;

        return ((MqttPacketType)typeRaw, buffer[..totalSize].ToArray(), totalSize);
    }

    /// <summary>Decode a CONNACK packet.</summary>
    public static ConnackPacket DecodeConnack(ReadOnlySpan<byte> data)
    {
        int offset = ParseFixedHeader(data);

        if (data.Length < offset + 2) throw new MqttCodecException("CONNACK truncated.");

        bool sessionPresent = (data[offset] & 0x01) != 0;
        offset++;

        byte reasonCode = data[offset];
        offset++;

        ushort? serverKeepAlive = null;
        if (offset < data.Length)
        {
            (uint propLen, int propOffset) = ReadVariableInt(data, offset);
            offset = propOffset;
            // Clamp propEnd against data.Length so a malformed huge propLen
            // can't poison the loop bound (ReadVariableInt already caps the
            // value at ≤268M which fits in int, but clamp for clarity).
            int propEnd = (int)Math.Min((long)offset + propLen, data.Length);

            while (offset < propEnd && offset < data.Length)
            {
                byte propId = data[offset];
                offset++;
                switch (propId)
                {
                    case MqttPropertyId.ServerKeepAlive:
                        if (offset + 2 > data.Length) break;
                        serverKeepAlive = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
                        offset += 2;
                        break;
                    default:
                        offset = SkipProperty(propId, data, offset);
                        break;
                }
            }
        }

        return new ConnackPacket(sessionPresent, reasonCode, serverKeepAlive);
    }

    /// <summary>Decode a PUBLISH packet (incoming, QoS 0).</summary>
    public static PublishReceived DecodePublish(ReadOnlySpan<byte> data)
    {
        int offset = ParseFixedHeader(data);

        (string topic, int afterTopic) = ReadUtf8String(data, offset);
        offset = afterTopic;

        if (offset < data.Length)
        {
            (uint propLen, int afterPropLen) = ReadVariableInt(data, offset);
            offset = AdvancePastProperties(afterPropLen, propLen, data.Length, "PUBLISH");
        }

        byte[] payload = data[offset..].ToArray();
        return new PublishReceived(topic, payload);
    }

    /// <summary>Decode a SUBACK packet.</summary>
    public static SubackPacket DecodeSuback(ReadOnlySpan<byte> data)
    {
        int offset = ParseFixedHeader(data);

        if (data.Length < offset + 2) throw new MqttCodecException("SUBACK truncated.");

        ushort packetId = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;

        if (offset < data.Length)
        {
            (uint propLen, int afterPropLen) = ReadVariableInt(data, offset);
            offset = AdvancePastProperties(afterPropLen, propLen, data.Length, "SUBACK");
        }

        var reasonCodes = data[offset..].ToArray();
        return new SubackPacket(packetId, reasonCodes);
    }

    // ---------- Helpers ----------

    private static byte[] BuildPacket(MqttPacketType type, byte flags, byte[] vh, byte[] payload)
    {
        int remaining = vh.Length + payload.Length;
        var ms = new MemoryStream(2 + remaining);
        ms.WriteByte((byte)(((byte)type << 4) | (flags & 0x0F)));
        AppendVariableInt(ms, (uint)remaining);
        ms.Write(vh, 0, vh.Length);
        ms.Write(payload, 0, payload.Length);
        return ms.ToArray();
    }

    private static int ParseFixedHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) throw new MqttCodecException("Packet too short.");
        (uint _, int bodyOffset) = ReadVariableInt(data, 1);
        return bodyOffset;
    }

    /// <summary>
    /// Skip a packet's variable-header property block, validating that the
    /// declared <paramref name="propLen"/> fits inside the remaining buffer.
    /// Returns the offset of the first byte after the properties.
    /// </summary>
    private static int AdvancePastProperties(int afterPropLen, uint propLen, int bufferLength, string packetName)
    {
        // ReadVariableInt caps the value at ≤ 0x0FFFFFFF (≤ 268M), so the
        // (long) widening below is just defence-in-depth against future codec
        // changes.
        long end = (long)afterPropLen + propLen;
        if (end > bufferLength)
        {
            throw new MqttCodecException(
                $"{packetName} properties length {propLen} overshoots remaining buffer ({bufferLength - afterPropLen} bytes).");
        }
        return (int)end;
    }

    /// <summary>MQTT variable-length integer encode (1-4 bytes).</summary>
    public static void AppendVariableInt(Stream sink, uint value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value > 0) b |= 0x80;
            sink.WriteByte(b);
        }
        while (value > 0);
    }

    /// <summary>MQTT variable-length integer decode. Returns (value, offsetAfterField).</summary>
    public static (uint Value, int NextOffset) ReadVariableInt(ReadOnlySpan<byte> data, int start)
    {
        uint multiplier = 1;
        uint value = 0;
        int offset = start;

        while (offset < data.Length)
        {
            byte b = data[offset];
            value += (uint)(b & 0x7F) * multiplier;
            offset++;
            if ((b & 0x80) == 0) return (value, offset);
            multiplier *= 128;
            if (multiplier > 128 * 128 * 128)
            {
                throw new MqttCodecException("Malformed remaining length.");
            }
        }
        throw new MqttCodecException("Truncated variable-length integer.");
    }

    private static void AppendUtf8String(Stream sink, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        AppendUInt16(sink, (ushort)bytes.Length);
        sink.Write(bytes, 0, bytes.Length);
    }

    private static (string Value, int NextOffset) ReadUtf8String(ReadOnlySpan<byte> data, int start)
    {
        if (start + 2 > data.Length) throw new MqttCodecException("UTF-8 string length missing.");
        int length = BinaryPrimitives.ReadUInt16BigEndian(data[start..]);
        int strStart = start + 2;
        if (strStart + length > data.Length) throw new MqttCodecException("UTF-8 string truncated.");
        string s = Encoding.UTF8.GetString(data.Slice(strStart, length));
        return (s, strStart + length);
    }

    private static void AppendUInt16(Stream sink, ushort value)
    {
        sink.WriteByte((byte)(value >> 8));
        sink.WriteByte((byte)(value & 0xFF));
    }

    private static void AppendUInt32(Stream sink, uint value)
    {
        sink.WriteByte((byte)((value >> 24) & 0xFF));
        sink.WriteByte((byte)((value >> 16) & 0xFF));
        sink.WriteByte((byte)((value >> 8)  & 0xFF));
        sink.WriteByte((byte)( value        & 0xFF));
    }

    /// <summary>Skip an unknown MQTT 5.0 property based on its identifier byte's known wire size.</summary>
    private static int SkipProperty(byte propId, ReadOnlySpan<byte> data, int offset)
    {
        int o = offset;
        switch (propId)
        {
            // 1-byte
            case 0x01: case 0x17: case 0x19: case 0x24: case 0x25:
            case 0x28: case 0x29: case 0x2A:
                o += 1; break;
            // 2-byte
            case 0x13: case 0x21: case 0x22: case 0x23:
                o += 2; break;
            // 4-byte
            case 0x02: case 0x11: case 0x18: case 0x27:
                o += 4; break;
            // UTF-8 string
            case 0x03: case 0x08: case 0x12: case 0x15: case 0x1A: case 0x1C: case 0x1F:
                if (o + 2 <= data.Length)
                {
                    int len = BinaryPrimitives.ReadUInt16BigEndian(data[o..]);
                    o += 2 + len;
                }
                break;
            // Binary data
            case 0x09: case 0x16:
                if (o + 2 <= data.Length)
                {
                    int len = BinaryPrimitives.ReadUInt16BigEndian(data[o..]);
                    o += 2 + len;
                }
                break;
            // Variable byte int
            case 0x0B:
                while (o < data.Length)
                {
                    byte b = data[o];
                    o++;
                    if ((b & 0x80) == 0) break;
                }
                break;
            // User property (string pair)
            case 0x26:
                for (int i = 0; i < 2; i++)
                {
                    if (o + 2 <= data.Length)
                    {
                        int len = BinaryPrimitives.ReadUInt16BigEndian(data[o..]);
                        o += 2 + len;
                    }
                }
                break;
            default:
                // Unknown property — can't determine size, bail.
                o = data.Length;
                break;
        }
        return o;
    }
}
