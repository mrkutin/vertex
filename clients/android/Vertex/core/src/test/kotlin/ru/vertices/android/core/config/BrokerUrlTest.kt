package ru.vertices.android.core.config

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class BrokerUrlTest {

    @Test fun parses_mqtts_with_explicit_port() {
        val u = BrokerUrl.parse("mqtts://mqtt-yc.vertices.ru:8883")!!
        assertEquals(BrokerUrl.Scheme.MQTTS, u.scheme)
        assertEquals("mqtt-yc.vertices.ru", u.host)
        assertEquals(8883, u.port)
        assertTrue(u.isTls)
        assertTrue(!u.isWebSocket)
    }

    @Test fun parses_wss_with_default_port_443() {
        val u = BrokerUrl.parse("wss://mqtt.example.com")!!
        assertEquals(BrokerUrl.Scheme.WSS, u.scheme)
        assertEquals(443, u.port)
        assertTrue(u.isTls && u.isWebSocket)
    }

    @Test fun parses_plain_mqtt_default_port_1883() {
        val u = BrokerUrl.parse("mqtt://localhost")!!
        assertEquals(BrokerUrl.Scheme.MQTT, u.scheme)
        assertEquals(1883, u.port)
        assertTrue(!u.isTls)
    }

    @Test fun parses_ws_default_port_80() {
        val u = BrokerUrl.parse("ws://localhost")!!
        assertEquals(80, u.port)
    }

    @Test fun urlString_round_trip() {
        val original = "mqtts://mqtt-sber.vertices.ru:8883"
        val u = BrokerUrl.parse(original)!!
        assertEquals(original, u.urlString)
    }

    @Test fun rejects_unknown_scheme() {
        assertNull(BrokerUrl.parse("nats://broker:1234"))
        assertNull(BrokerUrl.parse("https://broker:443"))
    }

    @Test fun rejects_missing_host() {
        assertNull(BrokerUrl.parse("mqtts://:8883"))
    }

    @Test fun rejects_invalid_port_zero() {
        assertNull(BrokerUrl.parse("mqtts://h:0"))
    }

    @Test fun rejects_invalid_port_too_large() {
        assertNull(BrokerUrl.parse("mqtts://h:70000"))
    }
}
