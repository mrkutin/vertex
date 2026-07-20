package ru.vertices.android.core.mqtt

/**
 * MQTT 5.0 control packet types (4-bit, upper nibble of the first byte).
 * Subset used by Vertex VPN — no QoS 1/2, no will, no auth packets.
 */
enum class MqttPacketType(val raw: Int) {
    CONNECT(1),
    CONNACK(2),
    PUBLISH(3),
    SUBSCRIBE(8),
    SUBACK(9),
    PINGREQ(12),
    PINGRESP(13),
    DISCONNECT(14);

    companion object {
        fun fromRaw(raw: Int): MqttPacketType? = entries.firstOrNull { it.raw == raw }
    }
}

// ---- Outbound ----

internal data class ConnectPacket(
    val clientId: String,
    val username: String?,
    val password: String?,
    val keepAlive: Int = 20,
    val cleanStart: Boolean = true,
    val sessionExpiryInterval: Long? = 0,
)

internal data class PublishPacket(
    val topic: String,
    val payload: ByteArray,
    val retain: Boolean = false,
    val messageExpirySeconds: Int? = 10,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PublishPacket) return false
        return topic == other.topic &&
            payload.contentEquals(other.payload) &&
            retain == other.retain &&
            messageExpirySeconds == other.messageExpirySeconds
    }
    override fun hashCode(): Int {
        var r = topic.hashCode()
        r = 31 * r + payload.contentHashCode()
        r = 31 * r + retain.hashCode()
        r = 31 * r + (messageExpirySeconds ?: 0)
        return r
    }
}

internal data class SubscribePacket(
    val packetId: Int,
    val topics: List<String>,
)

// ---- Inbound (decoded) ----

internal data class ConnackPacket(
    val sessionPresent: Boolean,
    val reasonCode: Int,
    val serverKeepAlive: Int? = null,
) {
    val isSuccess: Boolean get() = reasonCode == 0
    val reasonString: String get() = ReasonStrings.connack(reasonCode)
}

internal data class PublishReceived(
    val topic: String,
    val payload: ByteArray,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PublishReceived) return false
        return topic == other.topic && payload.contentEquals(other.payload)
    }
    override fun hashCode(): Int = 31 * topic.hashCode() + payload.contentHashCode()
}

internal data class SubackPacket(
    val packetId: Int,
    val reasonCodes: List<Int>,
) {
    val allSuccess: Boolean get() = reasonCodes.all { it <= 2 }
}

internal object ReasonStrings {
    fun connack(code: Int): String = when (code) {
        0     -> "Success"
        0x80  -> "Unspecified error"
        0x81  -> "Malformed packet"
        0x82  -> "Protocol error"
        0x83  -> "Implementation specific error"
        0x84  -> "Unsupported protocol version"
        0x85  -> "Client ID not valid"
        0x86  -> "Bad username or password"
        0x87  -> "Not authorized"
        0x88  -> "Server unavailable"
        0x89  -> "Server busy"
        0x8A  -> "Banned"
        0x8C  -> "Bad authentication method"
        0x90  -> "Topic name invalid"
        0x95  -> "Packet too large"
        0x97  -> "Quota exceeded"
        0x99  -> "Payload format invalid"
        0x9A  -> "Retain not supported"
        0x9B  -> "QoS not supported"
        0x9C  -> "Use another server"
        0x9D  -> "Server moved"
        0x9F  -> "Connection rate exceeded"
        else  -> "Unknown ($code)"
    }
}

// ---- Errors ----

sealed class MqttCodecError(msg: String) : Exception(msg) {
    object Incomplete : MqttCodecError("incomplete packet")
    class InvalidPacketType(val raw: Int) : MqttCodecError("invalid packet type 0x${raw.toString(16)}")
    object MalformedRemainingLength : MqttCodecError("malformed remaining length (variable int >4 bytes)")
    object MalformedUtf8String : MqttCodecError("malformed UTF-8 string")
    class ConnackFailed(val reason: String) : MqttCodecError("CONNACK rejected: $reason")
    class UnexpectedPacket(expected: MqttPacketType, got: MqttPacketType) :
        MqttCodecError("expected $expected, got $got")
}
