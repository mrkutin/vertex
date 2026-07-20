package ru.vertices.android.core.mqtt

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Round-trip and basic shape tests for the MQTT 5.0 codec. */
class MqttCodecTest {

    @Test fun pingreq_is_two_bytes() {
        val bytes = MqttPacketCodec.encodePingReq()
        assertArrayEquals(byteArrayOf(0xC0.toByte(), 0x00), bytes)
    }

    @Test fun disconnect_is_four_bytes() {
        val bytes = MqttPacketCodec.encodeDisconnect()
        assertArrayEquals(byteArrayOf(0xE0.toByte(), 0x02, 0x00, 0x00), bytes)
    }

    @Test fun connect_round_trip_first_byte_and_remaining_length() {
        val pkt = ConnectPacket(
            clientId = "vtx-test",
            username = "vtx-client-test",
            password = "secret",
            keepAlive = 20,
            cleanStart = true,
            sessionExpiryInterval = 0,
        )
        val out = MqttPacketCodec.encodeConnect(pkt)
        // First byte: 0x10 (CONNECT << 4)
        assertEquals(0x10, out[0].toInt() and 0xFF)
        // Decode remaining length and verify total length adds up.
        val (rl, off) = MqttPacketCodec.readVariableInt(out, 1)
        assertEquals(out.size.toLong(), (off + rl.toInt()).toLong())
    }

    @Test fun publish_qos0_round_trip_via_tryDecode_then_decodePublish() {
        val payload = "hello-vertex".toByteArray()
        val outbound = MqttPacketCodec.encodePublish(
            PublishPacket(topic = "vpn/aws/test/in", payload = payload, retain = false, messageExpirySeconds = 10)
        )
        // First byte: 0x30 (PUBLISH << 4, no retain, QoS 0)
        assertEquals(0x30, outbound[0].toInt() and 0xFF)

        val triple = MqttPacketCodec.tryDecode(outbound)!!
        assertEquals(MqttPacketType.PUBLISH, triple.first)
        assertEquals(outbound.size, triple.third)
        val decoded = MqttPacketCodec.decodePublish(triple.second)
        assertEquals("vpn/aws/test/in", decoded.topic)
        assertArrayEquals(payload, decoded.payload)
    }

    @Test fun tryDecode_returns_null_on_partial_packet() {
        val full = MqttPacketCodec.encodePublish(
            PublishPacket(topic = "t", payload = ByteArray(64) { 0x42 }, retain = false, messageExpirySeconds = null)
        )
        val partial = full.copyOfRange(0, full.size - 4)
        assertNull(MqttPacketCodec.tryDecode(partial))
    }

    @Test fun tryDecode_consumes_only_one_packet_from_concatenated_stream() {
        val a = MqttPacketCodec.encodePublish(PublishPacket("a", "hi".toByteArray(), false, null))
        val b = MqttPacketCodec.encodePublish(PublishPacket("b", "yo".toByteArray(), false, null))
        val both = a + b
        val first = MqttPacketCodec.tryDecode(both)!!
        assertEquals(a.size, first.third)
        // Second decode on the remaining bytes works.
        val second = MqttPacketCodec.tryDecode(both.copyOfRange(first.third, both.size))!!
        assertEquals(b.size, second.third)
    }

    @Test fun connack_decode_success_no_serverKeepAlive() {
        // Hand-crafted CONNACK: type=2 << 4 = 0x20, remaining=3, ackFlags=0, reasonCode=0, propLen=0
        val bytes = byteArrayOf(0x20, 0x03, 0x00, 0x00, 0x00)
        val ack = MqttPacketCodec.decodeConnack(bytes)
        assertTrue(ack.isSuccess)
        assertNull(ack.serverKeepAlive)
        assertEquals(false, ack.sessionPresent)
    }

    @Test fun connack_decode_with_server_keep_alive_property() {
        // ackFlags=0, reason=0, propLen=3, propID=0x13 (server keepalive), value 0x0014 = 20
        val bytes = byteArrayOf(0x20, 0x06, 0x00, 0x00, 0x03, 0x13, 0x00, 0x14)
        val ack = MqttPacketCodec.decodeConnack(bytes)
        assertTrue(ack.isSuccess)
        assertEquals(20, ack.serverKeepAlive)
    }

    @Test fun connack_decode_auth_failure_reason_code() {
        val bytes = byteArrayOf(0x20, 0x03, 0x00, 0x86.toByte(), 0x00)
        val ack = MqttPacketCodec.decodeConnack(bytes)
        assertEquals(0x86, ack.reasonCode)
        assertTrue(!ack.isSuccess)
        assertEquals("Bad username or password", ack.reasonString)
    }
}
