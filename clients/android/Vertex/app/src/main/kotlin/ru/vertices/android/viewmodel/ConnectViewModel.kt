package ru.vertices.android.viewmodel

import androidx.compose.ui.graphics.Color
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import ru.vertices.android.core.ipc.ConnectionState
import ru.vertices.android.core.ipc.ConnectionStatus
import ru.vertices.android.core.ipc.TunnelStats
import ru.vertices.android.repository.SettingsRepository
import ru.vertices.android.repository.SrvDiscovery
import ru.vertices.android.repository.TunnelController
import ru.vertices.android.vpn.TunnelStateBus
import java.net.InetSocketAddress
import java.net.Socket
import kotlin.math.max
import kotlin.system.measureTimeMillis
import timber.log.Timber

/**
 * Top-level state consumed by ConnectScreen / its widgets.
 *
 * Mirrors iOS `TunnelViewModel` — connection state, the rolling 3-second
 * upload/download rate and tunnel ping, the live broker/exit lists, and the
 * user's selections. The error string is read from
 * [ConnectionStatus.lastError] so the UI banner can be cleared by re-attempting
 * a connect.
 */
data class ConnectUiState(
    val status: ConnectionStatus = ConnectionStatus.DISCONNECTED,
    val stats: TunnelStats = TunnelStats.ZERO,
    val uploadBps: Double = 0.0,
    val downloadBps: Double = 0.0,
    val pingMs: Int? = null,
    val availableBrokers: List<String> = emptyList(),
    val availableExits: List<String> = emptyList(),
    /** `availableExits` is the SRV-resolved list of concrete edges; the
     * picker shows the synthetic `"auto"` option as a header element.
     * Kept out of `availableExits` itself so `NodeLabels.edgeLabel`
     * subscript indices don't shift when "auto" is added to the UI. */
    val presentedExits: List<String> = listOf("auto"),
    /** Same synthetic-head trick for brokers. `availableBrokers` stays
     * the SRV-resolved truth list; the picker shows `"auto"` first as
     * a sentinel meaning "let TunnelEngine probe TCP-RTT and reorder".
     * Indices into `availableBrokers` (used by `NodeLabels.vertexLabel`)
     * are unaffected. */
    val presentedBrokers: List<String> = listOf("auto"),
    /**
     * Per-exit display name from `SrvDiscoveryResult.exitDisplayNames`
     * (TXT records on the SRV target). Missing key → UI falls back to
     * uppercased exit ID via [NodeLabels.edgeLabel].
     */
    val exitDisplayNames: Map<String, String> = emptyMap(),
    val selectedBroker: String = SettingsRepository.DEFAULT_BROKER,
    val selectedExit: String = SettingsRepository.DEFAULT_EXIT,
    val domain: String = SettingsRepository.DEFAULT_DOMAIN,
    val isResolving: Boolean = false,
)

@HiltViewModel
class ConnectViewModel @Inject constructor(
    private val controller: TunnelController,
    private val settings: SettingsRepository,
    private val srv: SrvDiscovery,
) : ViewModel() {

    // Mutable slices that the screen reflects directly (not in TunnelStateBus).
    private val _availableBrokers = MutableStateFlow<List<String>>(emptyList())
    private val _availableExits = MutableStateFlow<List<String>>(emptyList())
    private val _exitDisplayNames = MutableStateFlow<Map<String, String>>(emptyMap())
    private val _resolving = MutableStateFlow(false)
    /**
     * End-to-end RTT through the active tunnel, in milliseconds. Measured by
     * TCP-connecting to 1.1.1.1:443 (Cloudflare anycast — never blocked,
     * fastest possible upstream); time from `connect()` to socket-ready
     * approximates one round-trip.
     *
     * Sticky: `null` until the first successful measurement; cleared only on
     * actual disconnect. Transient probe failures (Wi-Fi handoff that briefly
     * breaks 1.1.1.1 reachability) keep the last value visible — mirrors iOS
     * `feedback_pingms_sticky`.
     */
    private val _pingMs = MutableStateFlow<Int?>(null)

    /**
     * Rolling rate window (mirror of iOS rateWindow / historyHorizon). A short
     * deque of (epochMs, bytesUp, bytesDown) samples; the rate is bytes-delta
     * over the window in seconds.
     */
    private data class StatsSample(val epochMs: Long, val up: Long, val down: Long)
    private val statsHistory = ArrayDeque<StatsSample>()

    private val _uploadBps = MutableStateFlow(0.0)
    private val _downloadBps = MutableStateFlow(0.0)

    private data class Tunnel(val status: ConnectionStatus, val stats: TunnelStats)
    private data class Rates(val up: Double, val down: Double, val ping: Int?)
    private data class Picks(val brokers: List<String>, val exits: List<String>, val selBroker: String, val selExit: String)
    private data class Misc(val domain: String, val resolving: Boolean, val exitDisplayNames: Map<String, String>)

    val state: StateFlow<ConnectUiState> = combine(
        combine(TunnelStateBus.status, TunnelStateBus.stats, ::Tunnel),
        combine(_uploadBps, _downloadBps, _pingMs, ::Rates),
        combine(_availableBrokers, _availableExits, settings.selectedBroker, settings.selectedExit, ::Picks),
        combine(settings.domain, _resolving, _exitDisplayNames, ::Misc),
    ) { tunnel, rates, picks, misc ->
        ConnectUiState(
            status = tunnel.status,
            stats = tunnel.stats,
            uploadBps = rates.up,
            downloadBps = rates.down,
            pingMs = rates.ping,
            availableBrokers = picks.brokers,
            availableExits = picks.exits,
            presentedExits = listOf("auto") + picks.exits,
            presentedBrokers = listOf("auto") + picks.brokers,
            exitDisplayNames = misc.exitDisplayNames,
            selectedBroker = picks.selBroker,
            selectedExit = picks.selExit,
            domain = misc.domain,
            isResolving = misc.resolving,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), ConnectUiState())

    private val rateMutex = Mutex()
    private var pollJob: Job? = null
    private var pingJob: Job? = null

    init {
        // Track stats arriving from the VpnService — drive the rolling rate
        // window. This is process-local in Phase 1 (no IPC required).
        viewModelScope.launch {
            TunnelStateBus.stats.collect { onStatsSample(it) }
        }
        viewModelScope.launch {
            var lastPersistedExit: String? = null
            TunnelStateBus.status.collect { status ->
                when (status.state) {
                    ConnectionState.CONNECTED -> startPolling()
                    ConnectionState.DISCONNECTED -> {
                        stopPolling()
                        // Sticky pingMs is reset only on actual disconnect —
                        // transient probe failures keep the last value
                        // visible. Mirrors iOS `feedback_pingms_sticky`.
                        _pingMs.value = null
                    }
                    else -> { /* keep current polling state */ }
                }
                // Mirror the live resolved exit into the DataStore-backed
                // `lastGoodExit` so the auto-resolve fallback chain on the
                // next connect can pick it up when discovery is empty. We
                // skip "auto" itself and dedupe per process to avoid
                // hammering DataStore on every status republish.
                val exit = status.currentExit
                if (status.state == ConnectionState.CONNECTED &&
                    !exit.isNullOrBlank() && exit != "auto" && exit != lastPersistedExit) {
                    lastPersistedExit = exit
                    runCatching { settings.setLastGoodExit(exit) }
                }
            }
        }
        // Seed broker/exit lists from cache so the picker isn't empty on cold
        // start. The first resolveSrv() call refreshes them in the background.
        // First launch on a clean install with no cache leaves the lists
        // empty — the screen renders "Resolving discovery…" until DoH SRV
        // returns. We deliberately don't carry a hardcoded fallback list:
        // brokers and exits live in DNS (SRV) so a deploy change doesn't
        // require rebuilding the app.
        viewModelScope.launch {
            srv.loadCache()?.let { cached ->
                _availableBrokers.value = cached.brokerUrls
                _availableExits.value = cached.exitIds
                _exitDisplayNames.value = cached.exitDisplayNames
            }
            ensureValidSelections()
            resolveSrv()
        }
    }

    fun onConnectClicked() {
        // Drop any error message from a previous failed attempt so the
        // StatusPill doesn't display a stale "Authentication failed…"
        // banner during the new CONNECTING phase.
        TunnelStateBus.clearLastError()
        val brokers = _availableBrokers.value
        if (brokers.isEmpty()) {
            // Don't dispatch a connect when SRV hasn't populated the broker
            // list yet — the service-start intent would arrive with an empty
            // CSV, parseConfig would reject it, and the historical bug was a
            // `ForegroundServiceDidNotStartInTimeException` because the
            // bail-out path skipped startForeground. The service is hardened
            // against that now, but starting and immediately stopping a
            // foreground service flashes a notification at the user for no
            // reason. Surface a status banner and trigger a fresh DoH lookup
            // so the next tap has something to dispatch.
            TunnelStateBus.setLastError("Discovery hasn't resolved yet — try again in a moment.")
            resolveSrv(force = true)
            return
        }
        viewModelScope.launch(Dispatchers.IO) {
            controller.connect(brokers)
        }
    }

    fun onDisconnectClicked() {
        controller.disconnect()
        // Reset pingMs immediately so the SpeedPill doesn't show a stale
        // value during the brief teardown window before the DISCONNECTED
        // status arrives.
        _pingMs.value = null
    }

    fun selectBroker(url: String) {
        viewModelScope.launch { settings.setSelectedBroker(url) }
    }

    fun selectExit(id: String) {
        viewModelScope.launch { settings.setSelectedExit(id) }
    }

    fun setDomain(value: String) {
        viewModelScope.launch { settings.setDomain(value) }
    }

    /**
     * Refresh broker/exit list via DoH. Mirror of iOS `resolveSRV()`.
     *
     * @param force when true, bypasses the cache TTL and always re-resolves.
     *              Manual "Refresh" tap → force=true; cold start →
     *              force=false so a recent cache short-circuits the round-trip.
     */
    fun resolveSrv(force: Boolean = false) {
        viewModelScope.launch {
            val domain = settings.domain.first()
            if (domain.isBlank()) return@launch
            if (!force) {
                val cached = srv.loadCache()
                if (cached != null && cached.domain == domain && cached.isFresh()) {
                    _availableBrokers.value = cached.brokerUrls
                    _availableExits.value = cached.exitIds
                    _exitDisplayNames.value = cached.exitDisplayNames
                    ensureValidSelections()
                    return@launch
                }
            }
            _resolving.value = true
            try {
                val result = srv.resolveWithFallback(domain)
                if (result != null) {
                    _availableBrokers.value = result.brokerUrls
                    _availableExits.value = result.exitIds
                    _exitDisplayNames.value = result.exitDisplayNames
                }
                ensureValidSelections()
            } finally {
                _resolving.value = false
            }
        }
    }

    private suspend fun ensureValidSelections() {
        val brokers = _availableBrokers.value
        val exits = _availableExits.value
        // Same logic for both pickers (mirrors iOS `validateSelections`):
        // "auto" is always valid; an explicit pick is overwritten only if
        // the SRV list is populated AND the saved value is no longer in
        // it. The empty-list guard protects a saved value during cold
        // start before SRV resolves — without it we'd silently overwrite
        // a saved URL/exit with "auto" the first time the cache miss path
        // runs.
        val selBroker = settings.selectedBroker.first()
        if (selBroker != "auto" && brokers.isNotEmpty() && selBroker !in brokers) {
            settings.setSelectedBroker(SettingsRepository.DEFAULT_BROKER)
        }
        val selExit = settings.selectedExit.first()
        if (selExit != "auto" && exits.isNotEmpty() && selExit !in exits) {
            settings.setSelectedExit(SettingsRepository.DEFAULT_EXIT)
        }
    }

    // MARK: - Rolling rate

    private suspend fun onStatsSample(s: TunnelStats) {
        val nowMs = System.currentTimeMillis()
        rateMutex.withLock {
            statsHistory.addLast(StatsSample(nowMs, s.bytesUp, s.bytesDown))
            val cutoff = nowMs - HISTORY_HORIZON_MS
            while (statsHistory.firstOrNull()?.let { it.epochMs < cutoff } == true) {
                statsHistory.removeFirst()
            }
            // Compute rate over the last RATE_WINDOW_MS.
            val newest = statsHistory.last()
            val rateCutoff = nowMs - RATE_WINDOW_MS
            val oldest = statsHistory.firstOrNull { it.epochMs >= rateCutoff }
            if (oldest != null && oldest.epochMs < newest.epochMs) {
                val elapsedSec = (newest.epochMs - oldest.epochMs) / 1000.0
                if (elapsedSec > 0.0) {
                    _uploadBps.value = max(0.0, (newest.up - oldest.up) / elapsedSec)
                    _downloadBps.value = max(0.0, (newest.down - oldest.down) / elapsedSec)
                    return@withLock
                }
            }
            _uploadBps.value = 0.0
            _downloadBps.value = 0.0
        }
    }

    private fun startPolling() {
        if (pingJob?.isActive == true) return
        pingJob = viewModelScope.launch {
            // First measurement after a small delay so the tunnel is settled.
            delay(2_000)
            measurePing()
            while (true) {
                delay(PING_INTERVAL_MS)
                measurePing()
            }
        }
    }

    private fun stopPolling() {
        pollJob?.cancel(); pollJob = null
        pingJob?.cancel(); pingJob = null
        // pingMs intentionally NOT cleared here — see [_pingMs] sticky
        // semantics. Disconnect / DISCONNECTED collector reset it
        // explicitly. Mirrors iOS `stopPolling` after the sticky-pingMs
        // change.
        statsHistory.clear()
        _uploadBps.value = 0.0
        _downloadBps.value = 0.0
    }

    private suspend fun measurePing() {
        val rtt = withContext(Dispatchers.IO) {
            withTimeoutOrNull(PING_TIMEOUT_MS) {
                val socket = Socket()
                try {
                    runCatching {
                        val ms = measureTimeMillis {
                            socket.connect(InetSocketAddress(PING_HOST, PING_PORT), PING_TIMEOUT_MS.toInt())
                        }
                        ms.toInt()
                    }.onFailure { Timber.tag(TAG).w(it, "ping failed") }.getOrNull()
                } finally {
                    // Always close, including the case where withTimeoutOrNull
                    // tripped mid-connect — otherwise the socket stays in the
                    // OS in TIME_WAIT until GC runs the Socket finalizer.
                    runCatching { socket.close() }
                }
            }
        }
        if (rtt != null) _pingMs.value = rtt
    }

    val isBusy: Boolean
        get() = state.value.status.state.let {
            it == ConnectionState.CONNECTING || it == ConnectionState.HANDSHAKING || it == ConnectionState.RECONNECTING
        }

    val isConnected: Boolean
        get() = state.value.status.state == ConnectionState.CONNECTED

    private companion object {
        const val TAG = "vtx-vm-connect"
        const val RATE_WINDOW_MS = 3_000L
        const val HISTORY_HORIZON_MS = 5_000L
        const val PING_HOST = "1.1.1.1"
        const val PING_PORT = 443
        const val PING_INTERVAL_MS = 60_000L
        const val PING_TIMEOUT_MS = 2_500L
    }
}

// MARK: - Status text/color helpers shared with screens

fun ConnectionState.statusText(): String = when (this) {
    ConnectionState.DISCONNECTED -> "Not connected"
    ConnectionState.CONNECTING   -> "Connecting…"
    ConnectionState.HANDSHAKING  -> "Handshaking…"
    ConnectionState.RECONNECTING -> "Reconnecting…"
    ConnectionState.CONNECTED    -> "Connected"
}

fun ConnectionState.statusColor(
    connected: Color,
    transitioning: Color,
    dormant: Color,
): Color = when (this) {
    ConnectionState.CONNECTED    -> connected
    ConnectionState.CONNECTING,
    ConnectionState.HANDSHAKING,
    ConnectionState.RECONNECTING -> transitioning
    ConnectionState.DISCONNECTED -> dormant
}
