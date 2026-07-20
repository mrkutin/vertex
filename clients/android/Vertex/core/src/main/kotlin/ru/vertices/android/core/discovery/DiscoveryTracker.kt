package ru.vertices.android.core.discovery

import ru.vertices.android.core.protocol.DiscoveryHeartbeat

/**
 * Snapshot of one exit's most recent discovery heartbeat plus the wall-clock
 * time the tracker observed it. Mirrors `pkg/discovery.ExitInfo` (Go) and
 * `VertexCore.ExitInfo` (Swift).
 */
data class ExitInfo(
    val id: String,
    val country: String?,
    val clients: Int,
    val maxClients: Int,
    val brokerRttMs: Map<String, Int>,
    val dhPubkey: String?,
    val receivedAtMillis: Long,
)

/**
 * Accumulates exit-node heartbeats and runs the same scoring/selection logic
 * as `pkg/discovery` (Go) and `VertexCore.DiscoveryTracker` (Swift). Lower
 * score = better.
 *
 * Formula: `score = brokerRttMs * (1 + clients / capacity * loadFactor)`,
 * where capacity falls back to 253 (a /24 IP pool minus reserved hosts) and
 * loadFactor = 2.0 by default. RTT defaults to 100 ms when missing so an
 * exit without a measurement is still pickable but loses to any exit with a
 * real number.
 *
 * `shouldSwitch` carries a 1.5x flap-guard: only recommend switching when
 * `bestScore * 1.5 < currentScore`.
 *
 * Thread-safe via `synchronized(lock)`. The subscribe-handler writes from
 * the MQTT receive loop, the connect-flow reads once before publishing the
 * join — contention is minimal, no need for ConcurrentHashMap or coroutines.
 */
class DiscoveryTracker(
    private val loadFactor: Double = 2.0,
    private val staleAgeMillis: Long = 90_000L,
    private val clock: () -> Long = System::currentTimeMillis,
) {
    private val lock = Any()
    private val exits: MutableMap<String, ExitInfo> = mutableMapOf()

    /** Ingest one decoded heartbeat. Latest heartbeat for a given exit ID
     * replaces the previous one. */
    fun handle(hb: DiscoveryHeartbeat, receivedAtMillis: Long = clock()) {
        val info = ExitInfo(
            id = hb.id,
            country = hb.country,
            clients = hb.clients ?: 0,
            maxClients = hb.maxClients ?: 0,
            brokerRttMs = hb.brokerRttMs ?: emptyMap(),
            dhPubkey = hb.dhPubkey,
            receivedAtMillis = receivedAtMillis,
        )
        synchronized(lock) {
            exits[info.id] = info
        }
    }

    /** Drop the entry for `exitId`. Reserved for the day we wire LWT-style
     * removal events; auto-resolve relies on staleAge today. */
    fun remove(exitId: String) {
        synchronized(lock) { exits.remove(exitId) }
    }

    /** Best non-stale, non-full exit for the given broker host. Null when
     * the tracker hasn't seen a usable heartbeat yet. */
    fun bestExit(brokerHost: String): String? = synchronized(lock) {
        bestExitLocked(brokerHost, excluding = null)
    }

    /** 1.5x-tolerance switch decision. Returns the target exit when an
     * alternative is significantly better than `currentExit`. When
     * `currentExit` is missing or stale, returns the best alternative
     * regardless of margin. */
    fun shouldSwitch(currentExit: String, brokerHost: String): String? = synchronized(lock) {
        val current = exits[currentExit]
        if (current == null || isStaleLocked(current)) {
            return@synchronized bestExitLocked(brokerHost, excluding = currentExit)
        }
        val currentScore = scoreLocked(current, brokerHost)

        var bestId: String? = null
        var bestScore = Double.MAX_VALUE
        for (info in exits.values) {
            if (info.id == currentExit || isStaleLocked(info)) continue
            if (info.maxClients > 0 && info.clients >= info.maxClients) continue
            val s = scoreLocked(info, brokerHost)
            if (s < bestScore) {
                bestScore = s
                bestId = info.id
            }
        }
        if (bestId != null && bestScore * 1.5 < currentScore) bestId else null
    }

    /** Most recent non-stale heartbeat for the given exit ID, or null. Used
     * by the join handshake to pull `dhPubkey` for the identity proof
     * without re-waiting on the discovery stream. */
    fun info(exitId: String): ExitInfo? = synchronized(lock) {
        val info = exits[exitId] ?: return@synchronized null
        if (isStaleLocked(info)) null else info
    }

    /** True if the exit has a recent (non-stale) heartbeat. */
    fun isAvailable(exitId: String): Boolean = synchronized(lock) {
        val info = exits[exitId] ?: return@synchronized false
        !isStaleLocked(info)
    }

    /** Snapshot of all known exits. `includeStale=true` returns even
     * expired entries (used by the auto-resolve fallback chain when no
     * fresh heartbeat is in yet). */
    fun snapshot(includeStale: Boolean = false): List<ExitInfo> = synchronized(lock) {
        exits.values.filter { includeStale || !isStaleLocked(it) }
    }

    // MARK: - Private helpers (must run under the lock)

    private fun bestExitLocked(brokerHost: String, excluding: String?): String? {
        var bestId: String? = null
        var bestScore = Double.MAX_VALUE
        for (info in exits.values) {
            if (info.id == excluding || isStaleLocked(info)) continue
            if (info.maxClients > 0 && info.clients >= info.maxClients) continue
            val s = scoreLocked(info, brokerHost)
            if (s < bestScore) {
                bestScore = s
                bestId = info.id
            }
        }
        return bestId
    }

    private fun scoreLocked(info: ExitInfo, brokerHost: String): Double {
        val rtt = brokerRttLocked(info, brokerHost)
        val effectiveRtt = if (rtt > 0) rtt.toDouble() else DEFAULT_RTT_MS.toDouble()
        val capacity = if (info.maxClients > 0) info.maxClients.toDouble() else DEFAULT_CAPACITY.toDouble()
        return effectiveRtt * (1.0 + info.clients.toDouble() / capacity * loadFactor)
    }

    private fun brokerRttLocked(info: ExitInfo, brokerHost: String): Int {
        info.brokerRttMs[brokerHost]?.let { return it }
        val bare = stripPort(brokerHost)
        for ((host, rtt) in info.brokerRttMs) {
            if (stripPort(host) == bare) return rtt
        }
        return 0
    }

    private fun isStaleLocked(info: ExitInfo): Boolean =
        clock() - info.receivedAtMillis > staleAgeMillis

    companion object {
        /** /24 minus reserved hosts (.0, .1 gw, .255 broadcast, .2-.254 client pool). */
        private const val DEFAULT_CAPACITY = 253

        /** Used when no broker-RTT entry exists for the requested host. */
        private const val DEFAULT_RTT_MS = 100

        private fun stripPort(host: String): String {
            val colon = host.lastIndexOf(':')
            return if (colon > 0) host.substring(0, colon) else host
        }
    }
}
