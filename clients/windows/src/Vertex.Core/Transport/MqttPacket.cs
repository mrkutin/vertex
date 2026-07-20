namespace Vertex.Core.Transport;

/// <summary>MQTT 5.0 control packet types (4-bit, upper nibble of first byte).</summary>
public enum MqttPacketType : byte
{
    Connect    = 1,
    Connack    = 2,
    Publish    = 3,
    Subscribe  = 8,
    Suback     = 9,
    Pingreq    = 12,
    Pingresp   = 13,
    Disconnect = 14,
}

/// <summary>MQTT 5.0 CONNECT packet parameters (the minimal VPN subset).</summary>
public sealed record ConnectPacket(
    string  ClientId,
    string? Username,
    string? Password,
    ushort  KeepAlive,
    bool    CleanStart,
    uint?   SessionExpiryInterval);

/// <summary>MQTT 5.0 CONNACK packet (decoded).</summary>
public sealed record ConnackPacket(
    bool    SessionPresent,
    byte    ReasonCode,
    ushort? ServerKeepAlive)
{
    public bool IsSuccess => ReasonCode == 0;

    public string ReasonString => ReasonCode switch
    {
        0    => "Success",
        0x80 => "Unspecified error",
        0x81 => "Malformed packet",
        0x82 => "Protocol error",
        0x83 => "Implementation specific error",
        0x84 => "Unsupported protocol version",
        0x85 => "Client ID not valid",
        0x86 => "Bad username or password",
        0x87 => "Not authorized",
        0x88 => "Server unavailable",
        0x89 => "Server busy",
        0x8A => "Banned",
        0x8C => "Bad authentication method",
        0x90 => "Topic name invalid",
        0x95 => "Packet too large",
        0x97 => "Quota exceeded",
        0x99 => "Payload format invalid",
        0x9A => "Retain not supported",
        0x9B => "QoS not supported",
        0x9C => "Use another server",
        0x9D => "Server moved",
        0x9F => "Connection rate exceeded",
        _    => $"Unknown ({ReasonCode})",
    };
}

/// <summary>MQTT 5.0 PUBLISH packet parameters (QoS 0 only).</summary>
public sealed record PublishPacket(
    string Topic,
    byte[] Payload,
    bool   Retain,
    uint?  MessageExpirySeconds);

/// <summary>Decoded incoming PUBLISH packet.</summary>
public sealed record PublishReceived(string Topic, byte[] Payload);

/// <summary>MQTT 5.0 SUBSCRIBE packet (QoS 0 only).</summary>
public sealed record SubscribePacket(ushort PacketId, IReadOnlyList<string> Topics);

/// <summary>Decoded SUBACK.</summary>
public sealed record SubackPacket(ushort PacketId, IReadOnlyList<byte> ReasonCodes)
{
    /// <summary>True if every topic filter was granted at least QoS 0.</summary>
    public bool AllSuccess
    {
        get
        {
            foreach (var c in ReasonCodes) if (c > 2) return false;
            return true;
        }
    }
}

/// <summary>MQTT 5.0 property identifiers used by Vertex.</summary>
internal static class MqttPropertyId
{
    public const byte SessionExpiryInterval = 0x11; // 17
    public const byte ServerKeepAlive       = 0x13; // 19
    public const byte MessageExpiryInterval = 0x02; // 2
}

public sealed class MqttCodecException : Exception
{
    public MqttCodecException(string message) : base(message) { }
    public MqttCodecException(string message, Exception inner) : base(message, inner) { }
}
