package ru.vertices.android.core.protocol

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Pin the JSON wire-format. These tests fail loudly if anyone renames a field
 * without coordinating with Go and Swift implementations — wire-format drift
 * makes the tunnel silently wrong.
 */
class WireFormatTest {

    @Test fun joinMessage_serializes_with_id_sig_snake_case() {
        val msg = JoinMessage(
            name = "android-test",
            dh = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            id = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBE=",
            idSig = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCQ=",
        )
        val json = WireJson.encodeToString(JoinMessage.serializer(), msg)
        // Snake-case wire fields must appear verbatim.
        assertEquals(
            """{"name":"android-test","dh":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=","id":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBE=","id_sig":"CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCQ="}""",
            json,
        )
    }

    @Test fun joinMessage_omits_null_id_and_idSig() {
        val msg = JoinMessage(name = "test", dh = "DH", id = null, idSig = null)
        val json = WireJson.encodeToString(JoinMessage.serializer(), msg)
        assertEquals("""{"name":"test","dh":"DH"}""", json)
    }

    @Test fun assignMessage_decodes_with_optional_fields() {
        val payload = """{"ip":"10.9.0.5","mask":"255.255.255.0","gw":"10.9.0.1","dh":"DH"}"""
        val a = WireJson.decodeFromString(AssignMessage.serializer(), payload)
        assertEquals("10.9.0.5", a.ip)
        assertEquals("255.255.255.0", a.mask)
        assertEquals("10.9.0.1", a.gw)
        assertEquals("DH", a.dh)
    }

    @Test fun assignMessage_decodes_without_mask_or_dh() {
        val payload = """{"ip":"10.9.0.5","gw":"10.9.0.1"}"""
        val a = WireJson.decodeFromString(AssignMessage.serializer(), payload)
        assertEquals("10.9.0.5", a.ip)
        assertEquals("10.9.0.1", a.gw)
        assertNull(a.mask)
        assertNull(a.dh)
    }

    @Test fun discoveryHeartbeat_uses_snake_case_fields() {
        val payload = """
            {"id":"aws","country":"CA","clients":3,"max_clients":50,
             "broker_rtt_ms":{"mqtt-yc.vertices.ru":42,"mqtt-sber.vertices.ru":99},
             "uptime":86400,"ts":1714400000,"dh_pubkey":"DEAD"}
        """.trimIndent()
        val h = WireJson.decodeFromString(DiscoveryHeartbeat.serializer(), payload)
        assertEquals("aws", h.id)
        assertEquals("CA", h.country)
        assertEquals(50, h.maxClients)
        assertEquals(42, h.brokerRttMs?.get("mqtt-yc.vertices.ru"))
        assertEquals(86400L, h.uptime)
        assertEquals(1714400000L, h.ts)
        assertEquals("DEAD", h.dhPubkey)
    }

    @Test fun discoveryHeartbeat_tolerates_unknown_future_fields() {
        val payload = """{"id":"aws","capacity_pct":42,"version":"v3","extra":{"nested":true}}"""
        val h = WireJson.decodeFromString(DiscoveryHeartbeat.serializer(), payload)
        assertEquals("aws", h.id)
    }
}
