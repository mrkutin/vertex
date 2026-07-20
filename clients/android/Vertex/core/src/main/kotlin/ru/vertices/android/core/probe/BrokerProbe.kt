package ru.vertices.android.core.probe

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import ru.vertices.android.core.config.BrokerUrl

/**
 * Reorders broker URLs by ascending TCP-connect RTT before a long-lived
 * MQTT connection is opened, so the autopaho `ServerUrls` list points at
 * the fastest reachable broker first. Failed probes (timeout, refusal,
 * no path) keep their original relative position at the tail — a degraded
 * broker still gets tried as a last-resort fallback rather than silently
 * dropped.
 *
 * Mirror of `BrokerProbe.swift` — semantics MUST stay byte-equivalent
 * with the iOS / macOS shared `VertexCore.BrokerProbe` and the Go
 * reference `pkg/probe.ReorderByRTT`. Probe timing excludes TLS /
 * WebSocket handshake — only the TCP round-trip is measured.
 */
object BrokerProbe {

    /**
     * Per-URL probe outcome paired with original index for stable
     * tie-breaking inside the comparator. `rttMs == null` means the
     * probe failed (timeout / refused / unreachable).
     */
    private data class Probe(val idx: Int, val url: BrokerUrl, val rttMs: Int?)

    /**
     * Reorder broker URLs by ascending TCP-connect RTT. Empty or
     * single-URL input is a no-op (no probes issued). Total wait is
     * bounded by [timeoutMs]; probes run in parallel so the slowest
     * individual probe sets the wall-clock floor.
     */
    suspend fun reorderByRtt(brokers: List<BrokerUrl>, timeoutMs: Long = 1500L): List<BrokerUrl> {
        if (brokers.size <= 1) return brokers
        val probes = runProbes(brokers, timeoutMs)
        return sortByRtt(probes).map { it.url }
    }

    /**
     * Reorder + return the per-host RTT map for logging. When two URLs
     * share a hostname (e.g. `mqtts://host:8883` and `wss://host:443`),
     * the lower RTT wins for that host.
     *
     * Single-broker input still goes through `runProbes` so callers can
     * surface latency even when there is no reordering decision to make
     * — matches Swift `reorderWithRTTs`.
     */
    suspend fun reorderWithRtts(
        brokers: List<BrokerUrl>,
        timeoutMs: Long = 1500L,
    ): Pair<List<BrokerUrl>, Map<String, Int>> {
        if (brokers.isEmpty()) return Pair(emptyList(), emptyMap())
        val probes = runProbes(brokers, timeoutMs)
        val rtts = HashMap<String, Int>(brokers.size)
        for (p in probes) {
            val ms = p.rttMs ?: continue
            // Keep the lower of the two RTTs when the same host appears
            // with multiple schemes (mqtts:8883 + wss:443). On exact tie
            // the first probe to finish wins — coroutine completion
            // order is not deterministic, but this map is diagnostic
            // only (used for log lines) and does NOT affect ordering.
            val existing = rtts[p.url.host]
            if (existing == null || ms < existing) {
                rtts[p.url.host] = ms
            }
        }
        return Pair(sortByRtt(probes).map { it.url }, rtts)
    }

    /**
     * Format the result of [reorderWithRtts] as a single human-readable
     * `host=Xms host=Yms` string for log lines. Failed probes (host
     * absent from `rttMs`) show as `host=fail`.
     */
    fun formatOrder(sorted: List<BrokerUrl>, rttMs: Map<String, Int>): String =
        sorted.joinToString(separator = " ") { url ->
            val ms = rttMs[url.host]
            if (ms != null) "${url.host}=${ms}ms" else "${url.host}=fail"
        }

    // ---- private --------------------------------------------------------

    private suspend fun runProbes(brokers: List<BrokerUrl>, timeoutMs: Long): List<Probe> =
        coroutineScope {
            brokers.mapIndexed { i, b ->
                async(Dispatchers.IO) {
                    Probe(i, b, TcpRtt.measure(b.host, b.port, timeoutMs).getOrNull())
                }
            }.awaitAll()
        }

    /**
     * Sort: successful probes ascending by RTT (with original index as
     * tiebreaker for stability), then failed probes in their original
     * order. Single source of truth — must match Swift `sortByRTT` and
     * Go `sortByRTT` exactly.
     */
    private fun sortByRtt(probes: List<Probe>): List<Probe> =
        probes.sortedWith(Comparator { a, b ->
            val x = a.rttMs
            val y = b.rttMs
            when {
                x != null && y != null -> if (x != y) x.compareTo(y) else a.idx.compareTo(b.idx)
                x != null && y == null -> -1
                x == null && y != null -> 1
                else -> a.idx.compareTo(b.idx)
            }
        })
}
