package ru.vertices.android.core.mqtt

import java.io.ByteArrayOutputStream

/**
 * Wire encoder/decoder for the minimal MQTT 5.0 subset Vertex needs:
 * CONNECT, CONNACK, PUBLISH (QoS 0), SUBSCRIBE (QoS 0), SUBACK,
 * PINGREQ, PINGRESP, DISCONNECT.
 *
 * Byte-exact mirror of `clients/shared/VertexCore/Sources/VertexCore/Transport/MQTTPacketCodec.swift`.
 */
internal object MqttPacketCodec {

    // ---- MQTT 5 property IDs ----
    private const val PROP_SESSION_EXPIRY_INTERVAL: Byte = 0x11
    private const val PROP_SERVER_KEEP_ALIVE: Byte       = 0x13
    private const val PROP_MESSAGE_EXPIRY_INTERVAL: Byte = 0x02

    // ---------------- Encode ----------------

    fun encodeConnect(p: ConnectPacket): ByteArray {
        val vh = ByteArrayOutputStream()
        appendUtf8String("MQTT", vh)
        vh.write(5) // protocol version

        var flags = 0
        if (p.cleanStart) flags = flags or 0x02
        if (p.username != null) flags = flags or 0x80
        if (p.password != null) flags = flags or 0x40
        vh.write(flags)
        appendUInt16(p.keepAlive, vh)

        // Properties
        val props = ByteArrayOutputStream()
        p.sessionExpiryInterval?.let {
            props.write(PROP_SESSION_EXPIRY_INTERVAL.toInt())
            appendUInt32(it, props)
        }
        appendVariableInt(props.size().toLong(), vh)
        vh.write(props.toByteArray())

        // Payload
        val payload = ByteArrayOutputStream()
        appendUtf8String(p.clientId, payload)
        p.username?.let { appendUtf8String(it, payload) }
        p.password?.let { appendUtf8String(it, payload) }

        return buildPacket(MqttPacketType.CONNECT, 0, vh.toByteArray(), payload.toByteArray())
    }

    fun encodePublish(p: PublishPacket): ByteArray {
        val vh = ByteArrayOutputStream()
        appendUtf8String(p.topic, vh)
        // No packet ID for QoS 0

        val props = ByteArrayOutputStream()
        p.messageExpirySeconds?.let {
            props.write(PROP_MESSAGE_EXPIRY_INTERVAL.toInt())
            appendUInt32(it.toLong(), props)
        }
        appendVariableInt(props.size().toLong(), vh)
        vh.write(props.toByteArray())

        val flags = if (p.retain) 0x01 else 0x00
        return buildPacket(MqttPacketType.PUBLISH, flags, vh.toByteArray(), p.payload)
    }

    fun encodeSubscribe(p: SubscribePacket): ByteArray {
        val vh = ByteArrayOutputStream()
        appendUInt16(p.packetId, vh)
        vh.write(0) // empty properties

        val payload = ByteArrayOutputStream()
        for (topic in p.topics) {
            appendUtf8String(topic, payload)
            payload.write(0x00) // QoS 0, no options
        }
        return buildPacket(MqttPacketType.SUBSCRIBE, 0x02, vh.toByteArray(), payload.toByteArray())
    }

    fun encodePingReq(): ByteArray = byteArrayOf(0xC0.toByte(), 0x00)

    fun encodeDisconnect(): ByteArray = byteArrayOf(0xE0.toByte(), 0x02, 0x00, 0x00)

    // ---------------- Decode ----------------

    /**
     * Try to decode one full packet from the head of [buffer]. Returns
     * `(type, packetData, bytesConsumed)` or null if more bytes are needed.
     */
    fun tryDecode(buffer: ByteArray): Triple<MqttPacketType, ByteArray, Int>? {
        if (buffer.size < 2) return null

        val firstByte = buffer[0].toInt() and 0xFF
        val typeRaw = firstByte ushr 4

        // Decode remaining length (variable-length integer)
        var multiplier = 1
        var remaining = 0
        var offset = 1
        while (offset < buffer.size) {
            val b = buffer[offset].toInt() and 0xFF
            remaining += (b and 0x7F) * multiplier
            offset++
            if (b and 0x80 == 0) break
            multiplier *= 128
            if (multiplier > 128 * 128 * 128) return null
            if (offset == 5 && (b and 0x80) != 0) return null
        }
        if (offset == buffer.size && (buffer[offset - 1].toInt() and 0x80) != 0) {
            // last byte still had continuation bit and we're out of buffer
            return null
        }

        val total = offset + remaining
        if (buffer.size < total) return null

        val pktType = MqttPacketType.fromRaw(typeRaw) ?: return null
        val pktData = buffer.copyOfRange(0, total)
        return Triple(pktType, pktData, total)
    }

    fun decodeConnack(data: ByteArray): ConnackPacket {
        val (_, body) = parseFixedHeader(data)
        var off = body
        if (data.size < off + 2) throw MqttCodecError.Incomplete

        val ackFlags = data[off].toInt() and 0xFF
        val sessionPresent = (ackFlags and 0x01) != 0
        off++
        val reasonCode = data[off].toInt() and 0xFF
        off++

        // Properties
        var serverKeepAlive: Int? = null
        if (off < data.size) {
            val (propLen, propOff) = readVariableInt(data, off)
            off = propOff
            val end = off + propLen.toInt()
            while (off < end && off < data.size) {
                val propId = data[off]
                off++
                when (propId) {
                    PROP_SERVER_KEEP_ALIVE -> {
                        if (off + 2 > data.size) break
                        serverKeepAlive = readUInt16(data, off)
                        off += 2
                    }
                    else -> off = skipProperty(propId, data, off)
                }
            }
        }
        return ConnackPacket(sessionPresent, reasonCode, serverKeepAlive)
    }

    fun decodePublish(data: ByteArray): PublishReceived {
        val (_, body) = parseFixedHeader(data)
        var off = body

        val (topic, topicEnd) = readUtf8String(data, off)
        off = topicEnd

        // Skip properties (we don't currently consume any inbound props).
        if (off < data.size) {
            val (propLen, propOff) = readVariableInt(data, off)
            off = propOff + propLen.toInt()
        }
        if (off > data.size) throw MqttCodecError.Incomplete

        val payload = data.copyOfRange(off, data.size)
        return PublishReceived(topic, payload)
    }

    fun decodeSuback(data: ByteArray): SubackPacket {
        val (_, body) = parseFixedHeader(data)
        var off = body
        if (data.size < off + 2) throw MqttCodecError.Incomplete
        val packetId = readUInt16(data, off)
        off += 2

        if (off < data.size) {
            val (propLen, propOff) = readVariableInt(data, off)
            off = propOff + propLen.toInt()
        }
        if (off > data.size) throw MqttCodecError.Incomplete

        val codes = ArrayList<Int>(data.size - off)
        for (i in off until data.size) {
            codes.add(data[i].toInt() and 0xFF)
        }
        return SubackPacket(packetId, codes)
    }

    // ---------------- Internals ----------------

    private fun buildPacket(
        type: MqttPacketType,
        flags: Int,
        variableHeader: ByteArray,
        payload: ByteArray,
    ): ByteArray {
        val remaining = variableHeader.size + payload.size
        val out = ByteArrayOutputStream(2 + remaining)
        out.write(((type.raw shl 4) or (flags and 0x0F)) and 0xFF)
        appendVariableInt(remaining.toLong(), out)
        out.write(variableHeader)
        out.write(payload)
        return out.toByteArray()
    }

    private fun parseFixedHeader(data: ByteArray): Pair<Long, Int> {
        if (data.size < 2) throw MqttCodecError.Incomplete
        return readVariableInt(data, 1)
    }

    /** Variable-length integer per MQTT 5.0 §1.5.5. Returns (value, nextOffset). */
    fun readVariableInt(data: ByteArray, start: Int): Pair<Long, Int> {
        var multiplier = 1L
        var value = 0L
        var off = start
        while (off < data.size) {
            val b = data[off].toInt() and 0xFF
            value += (b and 0x7F) * multiplier
            off++
            if ((b and 0x80) == 0) return value to off
            multiplier *= 128
            if (multiplier > 128L * 128 * 128) throw MqttCodecError.MalformedRemainingLength
        }
        throw MqttCodecError.Incomplete
    }

    fun appendVariableInt(value: Long, out: ByteArrayOutputStream) {
        var v = value
        do {
            var byte = (v and 0x7F).toInt()
            v = v ushr 7
            if (v > 0) byte = byte or 0x80
            out.write(byte)
        } while (v > 0)
    }

    private fun appendUtf8String(s: String, out: ByteArrayOutputStream) {
        val bytes = s.toByteArray(Charsets.UTF_8)
        appendUInt16(bytes.size, out)
        out.write(bytes)
    }

    private fun readUtf8String(data: ByteArray, start: Int): Pair<String, Int> {
        if (start + 2 > data.size) throw MqttCodecError.Incomplete
        val len = readUInt16(data, start)
        val s = start + 2
        if (s + len > data.size) throw MqttCodecError.Incomplete
        val str = try {
            String(data, s, len, Charsets.UTF_8)
        } catch (_: Throwable) {
            throw MqttCodecError.MalformedUtf8String
        }
        return str to (s + len)
    }

    private fun appendUInt16(v: Int, out: ByteArrayOutputStream) {
        out.write((v ushr 8) and 0xFF)
        out.write(v and 0xFF)
    }

    private fun appendUInt32(v: Long, out: ByteArrayOutputStream) {
        out.write(((v ushr 24) and 0xFF).toInt())
        out.write(((v ushr 16) and 0xFF).toInt())
        out.write(((v ushr 8) and 0xFF).toInt())
        out.write((v and 0xFF).toInt())
    }

    private fun readUInt16(data: ByteArray, off: Int): Int =
        ((data[off].toInt() and 0xFF) shl 8) or (data[off + 1].toInt() and 0xFF)

    /** Skip an unknown MQTT 5.0 property value based on its property ID type. */
    private fun skipProperty(propId: Byte, data: ByteArray, offset: Int): Int {
        val id = propId.toInt() and 0xFF
        var off = offset
        when (id) {
            // 1-byte properties
            0x01, 0x17, 0x19, 0x24, 0x25, 0x28, 0x29, 0x2A -> off += 1
            // 2-byte properties
            0x13, 0x21, 0x22, 0x23 -> off += 2
            // 4-byte properties
            0x02, 0x11, 0x18, 0x27 -> off += 4
            // UTF-8 string + binary data (uint16 length + bytes)
            0x03, 0x08, 0x12, 0x15, 0x1A, 0x1C, 0x1F,
            0x09, 0x16 -> {
                if (off + 2 <= data.size) {
                    val len = readUInt16(data, off)
                    off += 2 + len
                }
            }
            // Variable byte integer
            0x0B -> {
                while (off < data.size) {
                    val b = data[off].toInt() and 0xFF
                    off++
                    if ((b and 0x80) == 0) break
                }
            }
            // User Property — string pair
            0x26 -> {
                repeat(2) {
                    if (off + 2 <= data.size) {
                        val len = readUInt16(data, off)
                        off += 2 + len
                    }
                }
            }
            else -> off = data.size // unknown — bail
        }
        return off
    }
}
