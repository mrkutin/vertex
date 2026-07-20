package ru.vertices.android.core.mqtt

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import ru.vertices.android.core.config.BrokerUrl
import ru.vertices.android.core.protocol.Topics
import timber.log.Timber
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit

/** Coarse transport state for the UI. Mirror of Swift `TransportState`. */
sealed interface TransportState {
    data object Disconnected : TransportState
    data class Connecting(val broker: String) : TransportState
    data class Connected(val broker: String) : TransportState
    data class Reconnecting(val broker: String, val attempt: Int) : TransportState
}

/**
 * MQTT 5.0 transport. Mirror of Swift `MQTTTransport` — multi-broker failover
 * with sticky reconnect, a small fixed backoff, and resubscribe on each CONNACK.
 *
 * Threading: all state mutation happens on the scheduler's single thread. Public
 * methods marshal onto it. The state flow can be observed from any thread.
 *
 * Liveness model: a single application-level signal — PINGRESP timeout — drives
 * "link dead" detection. Same reasoning as iOS: every other system network API
 * (NWPathMonitor, default-path KVO) was unreliable on at least one real device
 * scenario. Android `ConnectivityManager.NetworkCallback` is consulted for hints
 * (Phase 2+), but PINGRESP is authoritative.
 */
class MqttTransport(
    initialBrokers: List<BrokerUrl>,
    private val username: String,
    private val password: String,
    private val clientId: String,
    private val keepAliveSeconds: Int = 20,
    private val socketProtector: SocketProtector? = null,
    private val socketFactory: (BrokerUrl) -> MqttSocket = { defaultSocketFor(it, socketProtector) },
    private val scheduler: ScheduledExecutorService = defaultScheduler(),
    private val onFatalError: ((reason: String) -> Unit)? = null,
    private val onAuthFailure: ((reasonCode: Int, reasonString: String) -> Unit)? = null,
) {

    private val brokers: MutableList<BrokerUrl> = ArrayList(initialBrokers)
    init { require(brokers.isNotEmpty()) { "at least one broker required" } }

    // pattern → handler. Map (not list of pairs) so a second `subscribe` for the
    // same pattern replaces the handler instead of stacking — otherwise dispatch
    // would fan a single broker payload out to every accumulated handler. The
    // race that motivated the change: subscribe arrived between `ready=true`
    // and the resubscribe loop in `handleConnectionEvent.Connected`, ending up
    // double-registered in the previous list-based representation.
    // LinkedHashMap to preserve insertion order in `dispatch` for determinism.
    private val subscriptions: LinkedHashMap<String, (String, ByteArray) -> Unit> = LinkedHashMap()

    private val _state = MutableStateFlow<TransportState>(TransportState.Disconnected)
    val state: StateFlow<TransportState> = _state.asStateFlow()

    @Volatile private var ready = false
    @Volatile private var shouldReconnect = false
    @Volatile private var connection: MqttConnection? = null
    private var currentBrokerIndex = 0
    private var reconnectAttempt = 0
    private var consecutiveConnectFailures = 0
    private var connectTimeoutTask: ScheduledFuture<*>? = null
    private var reconnectTask: ScheduledFuture<*>? = null

    val isReady: Boolean get() = ready
    val currentBroker: BrokerUrl? get() = if (ready) brokers.getOrNull(currentBrokerIndex) else null

    fun start() {
        scheduler.execute {
            shouldReconnect = true
            connectToCurrentBroker()
        }
    }

    fun stop() {
        scheduler.execute {
            shouldReconnect = false
            cancelAllTimers()
            connection?.disconnect()
            connection = null
            ready = false
            _state.value = TransportState.Disconnected
        }
    }

    fun publish(topic: String, payload: ByteArray) {
        // Fire-and-forget — drop if not ready (caller retries via app-level keepalive).
        scheduler.execute {
            connection?.publish(topic, payload, retain = false, messageExpirySeconds = 10)
        }
    }

    fun subscribe(pattern: String, handler: (String, ByteArray) -> Unit) {
        scheduler.execute {
            val isNew = !subscriptions.containsKey(pattern)
            subscriptions[pattern] = handler
            // Only ask the broker to subscribe the first time we see the pattern;
            // a re-subscribe with the same filter is harmless on MQTT 5 but
            // doubles message dispatch when paired with a list-based local map.
            if (ready && isNew) connection?.subscribe(listOf(pattern))
        }
    }

    /**
     * Stop dispatching messages for [pattern] locally. We do not send an MQTT
     * UNSUBSCRIBE today (the codec doesn't encode it yet — Phase 2); the broker
     * will keep delivering, but [dispatch] becomes a no-op for this filter, so
     * the cost is just one ignored frame per heartbeat / control message.
     */
    fun unsubscribe(pattern: String) {
        scheduler.execute {
            subscriptions.remove(pattern)
        }
    }

    /** Trigger a fresh PINGREQ ahead of cadence. Used on network change hints. */
    fun checkLiveness() {
        scheduler.execute { connection?.pingNow() }
    }

    // ===== Test seams (internal — accessible only inside the :core module) =====

    /**
     * **Test-only.** Snapshot the broker order from the scheduler thread.
     * Used by [MqttTransportBug1Test] to assert that the Bug #1 demotion
     * reordered the failover list after a CONNACK auth rejection.
     * Production callers should observe broker selection through
     * [currentBroker] / [state].
     */
    internal fun _testBrokerHosts(): List<String> =
        scheduler.submit<List<String>> { brokers.map { it.host } }.get()

    /**
     * **Test-only.** Synthesise a `MqttConnection.Event.Disconnected`
     * carrying [connackReason] on the transport's scheduler thread —
     * the same thread the real listener callback uses. Lets a regression
     * test for the sticky-vs-auth-failure interaction
     * (clients/PENDING_TRANSPORT_FIXES.md "Bug #1") run without standing
     * up a real broker.
     */
    internal fun _testFireAuthFailureDisconnect(connackReason: Int = 0x86) {
        scheduler.submit {
            handleConnectionEvent(MqttConnection.Event.Disconnected(
                cause = null, linkDead = false, connackReason = connackReason))
        }.get()
    }

    /**
     * **Test-only.** Force [currentBrokerIndex] for tests that want to
     * exercise behaviour while connected to a non-primary (e.g. "auth
     * failure on the backup must NOT demote the primary it never
     * touched"). Real production transitions go through `Connected`'s
     * sticky-promote path.
     */
    internal fun _testSetCurrentBrokerIndex(index: Int) {
        scheduler.submit { currentBrokerIndex = index }.get()
    }

    fun forceReconnect(reason: String) {
        scheduler.execute {
            if (!shouldReconnect) return@execute
            Timber.tag(TAG).i("force reconnect: $reason")
            ready = false
            connection?.disconnect()
            connection = null
            reconnectAttempt = 0
            consecutiveConnectFailures = 0
            cancelAllTimers()
            _state.value = TransportState.Reconnecting(brokers[currentBrokerIndex].host, 0)
            connectToCurrentBroker()
        }
    }

    // ---- internal ----

    private fun connectToCurrentBroker() {
        if (!shouldReconnect) return
        val b = brokers[currentBrokerIndex]
        Timber.tag(TAG).i("connecting to ${b.host}:${b.port} (${b.scheme.raw})")
        _state.value = TransportState.Connecting(b.host)

        val sock = socketFactory(b)
        val conn = MqttConnection(
            socket = sock,
            clientId = clientId,
            username = username,
            password = password,
            keepAliveSeconds = keepAliveSeconds,
            scheduler = scheduler,
        )
        connection = conn
        scheduleConnectTimeout()

        conn.connect(
            listener = { handleConnectionEvent(it) },
            onPublish = { topic, payload -> dispatch(topic, payload) },
        )
    }

    private fun handleConnectionEvent(event: MqttConnection.Event) {
        when (event) {
            MqttConnection.Event.Connected -> {
                cancelConnectTimeout()
                Timber.tag(TAG).i("connected to ${brokers[currentBrokerIndex].host}")
                ready = true
                reconnectAttempt = 0
                consecutiveConnectFailures = 0

                // Sticky reconnect: move winner to front so next reconnect tries it first.
                if (currentBrokerIndex > 0) {
                    val winner = brokers.removeAt(currentBrokerIndex)
                    brokers.add(0, winner)
                    currentBrokerIndex = 0
                }
                // Resubscribe everything we have on file.
                val patterns = subscriptions.keys.toList()
                if (patterns.isNotEmpty()) {
                    connection?.subscribe(patterns)
                    Timber.tag(TAG).i("resubscribed ${patterns.size} topics")
                }
                _state.value = TransportState.Connected(brokers[0].host)
            }
            is MqttConnection.Event.Disconnected -> {
                cancelConnectTimeout()
                val wasReady = ready
                ready = false
                connection = null
                val cause = event.cause
                Timber.tag(TAG).w(cause, "MQTT disconnected linkDead=${event.linkDead} connackReason=${event.connackReason}")

                // Auth/CONNACK rejection: short-circuit. Same creds will keep failing.
                val rc = event.connackReason
                if (rc != null && rc != 0) {
                    Timber.tag(TAG).w("CONNACK rejected (code=$rc) — escalating, no retry")

                    // Un-sticky: a previous successful connect promoted this
                    // broker to index 0 (sticky reconnect). Auth-rejecting now
                    // means the stickied broker is misconfigured (creds rotated,
                    // ACL tightened, …). Demote it to the tail so a future
                    // start() tries the original primary first — the user
                    // shouldn't be locked out by one bad broker once another
                    // is healthy. See clients/PENDING_TRANSPORT_FIXES.md "Bug #1".
                    if (brokers.size > 1 && currentBrokerIndex == 0) {
                        val loser = brokers.removeAt(0)
                        brokers.add(loser)
                        // Fresh start counters so the next start() begins its
                        // backoff cycle from 0 (paritет с iOS / macOS fix).
                        reconnectAttempt = 0
                        consecutiveConnectFailures = 0
                    }

                    shouldReconnect = false
                    cancelAllTimers()
                    onAuthFailure?.invoke(rc, ReasonStrings.connack(rc))
                    return
                }

                // linkDead means the underlying network path died: PINGRESP
                // timeout fired, or socket I/O threw post-handshake. The TUN
                // keeps running — only the MQTT transport needs to rebuild
                // on whatever path is now default. Mirrors iOS, where the
                // extension cancels itself with `cancelTunnelWithError` and
                // the on-demand rule restarts it; Android has no on-demand
                // equivalent so we reconnect in-place. Previously this path
                // escalated to fatal, killing the whole tunnel on every
                // wifi/cell handoff and stranding the user with no auto-
                // reconnect.
                if (event.linkDead) {
                    Timber.tag(TAG).i("link dead — reconnecting")
                }

                if (wasReady) {
                    _state.value = TransportState.Reconnecting(brokers[currentBrokerIndex].host, reconnectAttempt)
                }
                scheduleReconnect()
            }
        }
    }

    private fun scheduleConnectTimeout() {
        cancelConnectTimeout()
        connectTimeoutTask = scheduler.schedule({
            if (!ready && shouldReconnect) {
                consecutiveConnectFailures++
                Timber.tag(TAG).w("connect timeout — aborting ($consecutiveConnectFailures consecutive)")
                connection?.disconnect()
                connection = null
                if (consecutiveConnectFailures >= 3) onFatalError?.let {
                    shouldReconnect = false
                    cancelAllTimers()
                    it("Persistent connect failures ($consecutiveConnectFailures)")
                    return@schedule
                }
                scheduleReconnect()
            }
        }, CONNECT_TIMEOUT_SEC, TimeUnit.SECONDS)
    }

    private fun cancelConnectTimeout() {
        connectTimeoutTask?.cancel(false); connectTimeoutTask = null
    }

    private fun cancelAllTimers() {
        cancelConnectTimeout()
        reconnectTask?.cancel(false); reconnectTask = null
    }

    private fun scheduleReconnect() {
        if (!shouldReconnect) return
        reconnectTask?.cancel(false)

        reconnectAttempt++
        currentBrokerIndex = reconnectAttempt % brokers.size

        val cycleIndex = (reconnectAttempt / brokers.size).coerceAtMost(BACKOFF_DELAYS_SEC.size - 1)
        val delaySec = BACKOFF_DELAYS_SEC[cycleIndex]

        if (delaySec == 0.0) {
            connectToCurrentBroker(); return
        }

        Timber.tag(TAG).i("reconnect in ${delaySec}s (attempt=$reconnectAttempt, broker=${brokers[currentBrokerIndex].host})")
        _state.value = TransportState.Reconnecting(brokers[currentBrokerIndex].host, reconnectAttempt)
        reconnectTask = scheduler.schedule(::connectToCurrentBroker,
            (delaySec * 1000).toLong(), TimeUnit.MILLISECONDS)
    }

    private fun dispatch(topic: String, payload: ByteArray) {
        // Snapshot to avoid ConcurrentModificationException — `subscribe` /
        // `unsubscribe` mutate `subscriptions` from the same scheduler thread,
        // but dispatch is invoked from MqttConnection's read callback which
        // can fire while a subscribe is in-flight in a queued runnable.
        val snapshot = subscriptions.entries.toList()
        for ((pattern, handler) in snapshot) {
            if (Topics.matches(topic, pattern)) {
                handler(topic, payload)
            }
        }
    }

    companion object {
        private const val TAG = "vtx-mqtt-tx"
        private const val CONNECT_TIMEOUT_SEC = 15L
        private val BACKOFF_DELAYS_SEC = doubleArrayOf(0.0, 0.5, 1.0, 2.0, 5.0)

        private fun defaultScheduler(): ScheduledExecutorService =
            Executors.newSingleThreadScheduledExecutor { r ->
                Thread(r, "vtx-mqtt-tx").apply { isDaemon = true }
            }

        private fun defaultSocketFor(b: BrokerUrl, protector: SocketProtector?): MqttSocket =
            if (b.isWebSocket) WssMqttSocket(b) else TlsMqttSocket(b, protector)
    }
}
