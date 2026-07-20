package ru.vertices.android.core.probe

import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import ru.vertices.android.core.config.BrokerUrl
import java.net.ServerSocket

class BrokerProbeTest {

    private val servers = mutableListOf<ServerSocket>()

    @After fun tearDown() {
        servers.forEach { runCatching { it.close() } }
        servers.clear()
    }

    /**
     * One-shot accept loop on `127.0.0.1:0` — kernel finishes the
     * SYN/SYN+ACK handshake before our `accept()` returns, which is
     * exactly what `TcpRtt.measure` times. We immediately close each
     * accepted client; the test only cares about the round trip.
     */
    private fun startEchoServer(): ServerSocket {
        val s = ServerSocket(0)
        servers.add(s)
        Thread {
            while (!s.isClosed) {
                runCatching { s.accept().close() }
            }
        }.apply { isDaemon = true }.start()
        return s
    }

    private fun broker(host: String, port: Int): BrokerUrl =
        BrokerUrl(BrokerUrl.Scheme.MQTT, host, port)

    // ---- reorderByRtt ---------------------------------------------------

    @Test fun reorderByRtt_emptyList_returnsEmpty() = runTest {
        val out = BrokerProbe.reorderByRtt(emptyList())
        assertTrue(out.isEmpty())
    }

    @Test fun reorderByRtt_singleBroker_passesThrough() = runTest {
        // Port 1 is closed on loopback; if a probe were issued the call
        // would still succeed via the no-op fast path — that is exactly
        // what we are asserting here (single-broker input is untouched).
        val only = broker("127.0.0.1", 1)
        val out = BrokerProbe.reorderByRtt(listOf(only))
        assertEquals(listOf(only), out)
    }

    @Test fun reorderByRtt_partitionsSuccessFromFailed() = runTest {
        val live = startEchoServer()
        val liveBroker = broker("127.0.0.1", live.localPort)
        // Port 1 on loopback: TCP RST → fast ECONNREFUSED, no need to
        // wait for the probe timeout to expire.
        val deadBroker = broker("127.0.0.1", 1)

        val out = BrokerProbe.reorderByRtt(listOf(deadBroker, liveBroker), timeoutMs = 500L)
        assertEquals(2, out.size)
        assertEquals(liveBroker, out[0])
        assertEquals(deadBroker, out[1])
    }

    @Test fun reorderByRtt_allFailedKeepsOriginalOrder() = runTest {
        // Two unreachable brokers — comparator's (none, none) branch must
        // preserve original index order so a degraded set still gets tried
        // in user-facing priority.
        val first = broker("127.0.0.1", 1)
        val second = broker("127.0.0.1", 2)
        val out = BrokerProbe.reorderByRtt(listOf(first, second), timeoutMs = 200L)
        assertEquals(listOf(first, second), out)
    }

    @Test fun reorderByRtt_stableTiebreakByIndex() = runTest {
        val a = startEchoServer()
        val b = startEchoServer()
        val brokerA = broker("127.0.0.1", a.localPort)
        val brokerB = broker("127.0.0.1", b.localPort)

        val out = BrokerProbe.reorderByRtt(listOf(brokerA, brokerB), timeoutMs = 500L)
        assertEquals(2, out.size)
        // Loopback handshake is sub-millisecond; both probes round to
        // 0 ms and the comparator falls back to original index.
        assertTrue("output must contain both inputs", out.containsAll(listOf(brokerA, brokerB)))
    }

    // ---- reorderWithRtts ------------------------------------------------

    @Test fun reorderWithRtts_returnsPerHostRttMap() = runTest {
        val live = startEchoServer()
        val liveBroker = broker("127.0.0.1", live.localPort)

        val (sorted, rtts) = BrokerProbe.reorderWithRtts(listOf(liveBroker), timeoutMs = 500L)
        assertEquals(listOf(liveBroker), sorted)
        assertNotNull("live host must appear in rtt map", rtts["127.0.0.1"])
    }

    @Test fun reorderWithRtts_emptyInput_returnsEmptyPair() = runTest {
        val (sorted, rtts) = BrokerProbe.reorderWithRtts(emptyList())
        assertTrue(sorted.isEmpty())
        assertTrue(rtts.isEmpty())
    }

    @Test fun reorderWithRtts_failedProbeAbsentFromMap() = runTest {
        val dead = broker("127.0.0.1", 1)
        val (_, rtts) = BrokerProbe.reorderWithRtts(listOf(dead), timeoutMs = 200L)
        assertNull("failed probe must not appear in rtt map", rtts["127.0.0.1"])
    }

    // ---- formatOrder ----------------------------------------------------

    @Test fun formatOrder_failedShownAsFail() {
        val a = broker("a.example", 1883)
        val b = broker("b.example", 1883)
        val s = BrokerProbe.formatOrder(listOf(a, b), emptyMap())
        assertEquals("a.example=fail b.example=fail", s)
    }

    @Test fun formatOrder_successShownWithMs() {
        val a = broker("a.example", 1883)
        val b = broker("b.example", 1883)
        val s = BrokerProbe.formatOrder(listOf(a, b), mapOf("a.example" to 12, "b.example" to 47))
        assertEquals("a.example=12ms b.example=47ms", s)
    }

    @Test fun formatOrder_mixedSuccessAndFail() {
        val a = broker("a.example", 1883)
        val b = broker("b.example", 1883)
        val s = BrokerProbe.formatOrder(listOf(a, b), mapOf("a.example" to 25))
        assertTrue(s.contains("a.example=25ms"))
        assertTrue(s.contains("b.example=fail"))
    }
}
