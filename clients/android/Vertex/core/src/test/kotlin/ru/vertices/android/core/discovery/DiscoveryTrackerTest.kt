package ru.vertices.android.core.discovery

import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.Dispatchers
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import ru.vertices.android.core.protocol.DiscoveryHeartbeat

class DiscoveryTrackerTest {

    private fun heartbeat(
        id: String,
        country: String? = null,
        clients: Int = 0,
        maxClients: Int = 50,
        rtt: Map<String, Int> = emptyMap(),
        dhPubkey: String? = null,
    ): DiscoveryHeartbeat = DiscoveryHeartbeat(
        id = id,
        country = country,
        clients = clients,
        maxClients = maxClients,
        brokerRttMs = rtt,
        uptime = 100L,
        ts = 0L,
        dhPubkey = dhPubkey,
    )

    // MARK: - Ingest

    @Test fun handleStoresExit() {
        val t = DiscoveryTracker()
        t.handle(heartbeat(id = "aws", country = "CA", clients = 5, maxClients = 50,
                           rtt = mapOf("broker-ru" to 70)))
        val info = t.info("aws")
        assertNotNull(info)
        assertEquals("CA", info!!.country)
        assertEquals(5, info.clients)
        assertEquals(50, info.maxClients)
        assertEquals(70, info.brokerRttMs["broker-ru"])
    }

    @Test fun handleReplacesExit() {
        val t = DiscoveryTracker()
        t.handle(heartbeat(id = "aws", clients = 5))
        t.handle(heartbeat(id = "aws", clients = 12))
        assertEquals(12, t.info("aws")!!.clients)
    }

    @Test fun removeDropsExit() {
        val t = DiscoveryTracker()
        t.handle(heartbeat(id = "aws"))
        assertTrue(t.isAvailable("aws"))
        t.remove("aws")
        assertFalse(t.isAvailable("aws"))
    }

    // MARK: - bestExit

    @Test fun bestExitPicksLowerRtt() {
        val t = DiscoveryTracker()
        t.handle(heartbeat(id = "aws", clients = 2, rtt = mapOf("broker-ru" to 70, "broker-eu" to 80)))
        t.handle(heartbeat(id = "ams", clients = 2, rtt = mapOf("broker-ru" to 50, "broker-eu" to 5)))

        assertEquals("ams", t.bestExit("broker-ru"))
        assertEquals("ams", t.bestExit("broker-eu"))
    }

    @Test fun bestExitFavorsLessLoaded() {
        // Same-ish RTT, different load. eu2 score ≈ 6*(1+5/50*2)=7.2
        // beats eu1 ≈ 5*(1+40/50*2)=13.
        val t = DiscoveryTracker()
        t.handle(heartbeat(id = "eu1", clients = 40, maxClients = 50, rtt = mapOf("broker" to 5)))
        t.handle(heartbeat(id = "eu2", clients = 5, maxClients = 50, rtt = mapOf("broker" to 6)))
        assertEquals("eu2", t.bestExit("broker"))
    }

    @Test fun bestExitSkipsFull() {
        val t = DiscoveryTracker()
        t.handle(heartbeat(id = "aws", clients = 50, maxClients = 50, rtt = mapOf("broker" to 10)))
        t.handle(heartbeat(id = "ams", clients = 5, maxClients = 50, rtt = mapOf("broker" to 100)))
        assertEquals("ams", t.bestExit("broker"))
    }

    @Test fun bestExitSkipsStale() {
        var now = 1_000_000L
        val t = DiscoveryTracker(staleAgeMillis = 90_000L) { now }
        t.handle(heartbeat(id = "aws", rtt = mapOf("broker" to 10)), receivedAtMillis = now)
        now += 120_000L
        assertNull(t.bestExit("broker"))
        assertNull(t.info("aws"))
        assertFalse(t.isAvailable("aws"))
    }

    @Test fun bestExitEmptyTracker() {
        val t = DiscoveryTracker()
        assertNull(t.bestExit("broker"))
    }

    @Test fun bestExitMissingRttUsesDefault() {
        // Both exits lack RTT for the queried broker. Default RTT = 100,
        // so the less-loaded one wins purely on load factor.
        val t = DiscoveryTracker()
        t.handle(heartbeat(id = "a", clients = 30, maxClients = 50))
        t.handle(heartbeat(id = "b", clients = 1, maxClients = 50))
        assertEquals("b", t.bestExit("broker"))
    }

    // MARK: - shouldSwitch

    @Test fun shouldSwitchStaleCurrentReturnsAlternative() {
        var now = 1_000_000L
        val t = DiscoveryTracker(staleAgeMillis = 90_000L) { now }
        t.handle(heartbeat(id = "aws", rtt = mapOf("broker" to 10)), receivedAtMillis = now)
        // ams must be FRESH at the time of the shouldSwitch call below.
        now += 120_000L
        t.handle(heartbeat(id = "ams", rtt = mapOf("broker" to 5)), receivedAtMillis = now)
        assertEquals("ams", t.shouldSwitch("aws", "broker"))
    }

    @Test fun shouldSwitchMissingCurrentReturnsAlternative() {
        val t = DiscoveryTracker()
        t.handle(heartbeat(id = "ams", rtt = mapOf("broker" to 5)))
        assertEquals("ams", t.shouldSwitch("aws", "broker"))
    }

    @Test fun shouldSwitchToleranceBlocksSmallImprovement() {
        // 40ms vs 50ms ≈ 1.25x — under 1.5x threshold.
        val t = DiscoveryTracker()
        t.handle(heartbeat(id = "aws", rtt = mapOf("broker" to 50)))
        t.handle(heartbeat(id = "ams", rtt = mapOf("broker" to 40)))
        assertNull(t.shouldSwitch("aws", "broker"))
    }

    @Test fun shouldSwitchAcceptsLargeImprovement() {
        // 10ms vs 50ms = 5x — well above 1.5x threshold.
        val t = DiscoveryTracker()
        t.handle(heartbeat(id = "aws", rtt = mapOf("broker" to 50)))
        t.handle(heartbeat(id = "ams", rtt = mapOf("broker" to 10)))
        assertEquals("ams", t.shouldSwitch("aws", "broker"))
    }

    @Test fun shouldSwitchNoAlternative() {
        val t = DiscoveryTracker()
        t.handle(heartbeat(id = "aws", rtt = mapOf("broker" to 50)))
        assertNull(t.shouldSwitch("aws", "broker"))
    }

    // MARK: - Broker host normalization

    @Test fun brokerHostStripsPort() {
        val t = DiscoveryTracker()
        t.handle(heartbeat(id = "aws", rtt = mapOf("mqtt.example.com" to 50)))
        assertEquals("aws", t.bestExit("mqtt.example.com:8883"))
    }

    @Test fun brokerHostExactMatchTakesPrecedence() {
        val t = DiscoveryTracker()
        t.handle(heartbeat(id = "aws", rtt = mapOf(
            "mqtt.example.com" to 50,
            "mqtt.example.com:8883" to 25,
        )))
        // Bare lookup → 50; this is a direct hit on the bare key, no port-strip needed.
        assertEquals(50, t.info("aws")!!.brokerRttMs["mqtt.example.com"])
    }

    // MARK: - dhPubkey passthrough

    @Test fun infoExposesDhPubkey() {
        val t = DiscoveryTracker()
        t.handle(heartbeat(id = "aws", dhPubkey = "BASE64KEY=="))
        assertEquals("BASE64KEY==", t.info("aws")!!.dhPubkey)
    }

    // MARK: - snapshot

    @Test fun snapshotSkipsStaleByDefault() {
        var now = 1_000_000L
        val t = DiscoveryTracker(staleAgeMillis = 90_000L) { now }
        t.handle(heartbeat(id = "fresh"), receivedAtMillis = now)
        t.handle(heartbeat(id = "stale"), receivedAtMillis = now - 120_000L)

        val ids = t.snapshot().map { it.id }.toSet()
        assertEquals(setOf("fresh"), ids)
        val allIds = t.snapshot(includeStale = true).map { it.id }.toSet()
        assertEquals(setOf("fresh", "stale"), allIds)
    }

    // MARK: - Concurrency smoke

    @Test fun concurrentHandleAndReadDoesNotCrash() {
        runBlocking {
            val t = DiscoveryTracker()
            val writers = (0..7).map { w ->
                async(Dispatchers.Default) {
                    repeat(200) { i ->
                        t.handle(heartbeat(id = "exit$w", clients = i, rtt = mapOf("broker" to 10)))
                    }
                }
            }
            val readers = (0..3).map {
                async(Dispatchers.Default) {
                    repeat(200) {
                        t.bestExit("broker")
                        t.shouldSwitch("exit0", "broker")
                        t.snapshot()
                    }
                }
            }
            (writers + readers).awaitAll()
        }
    }
}
