package ru.vertices.android.vpn

import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import ru.vertices.android.vpn.routing.PriorityBypassNets
import ru.vertices.android.vpn.routing.RUNetsLoader
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import ru.vertices.android.core.config.BrokerUrl
import ru.vertices.android.core.config.TunnelConfig
import ru.vertices.android.core.crypto.IdentityKey
import ru.vertices.android.core.crypto.SessionCrypto
import ru.vertices.android.core.crypto.X25519
import ru.vertices.android.core.discovery.DiscoveryTracker
import ru.vertices.android.core.identity.IdentityKeyStore
import ru.vertices.android.core.ipc.ConnectionState
import ru.vertices.android.core.ipc.ConnectionStatus
import ru.vertices.android.core.ipc.TunnelErrorKind
import ru.vertices.android.core.ipc.TunnelErrorReport
import ru.vertices.android.core.ipc.TunnelStats
import ru.vertices.android.core.mqtt.MqttTransport
import ru.vertices.android.core.mqtt.SocketProtector
import ru.vertices.android.core.mqtt.TransportState
import ru.vertices.android.core.probe.BrokerProbe
import ru.vertices.android.core.protocol.AssignMessage
import ru.vertices.android.core.protocol.DiscoveryHeartbeat
import ru.vertices.android.core.protocol.JoinMessage
import ru.vertices.android.core.protocol.Topics
import ru.vertices.android.core.protocol.WireJson
import ru.vertices.android.core.util.toBase64
import ru.vertices.android.core.util.base64ToBytes
import timber.log.Timber
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.contentOrNull

/**
 * Orchestrates a Vertex tunnel: MQTT transport ↔ exit handshake ↔ packet plane.
 *
 *  1. Start `MqttTransport` against the broker list, wait for CONNACK.
 *  2. Subscribe to `discovery/exits/{exit}` (or `+` for auto), wait for
 *     a heartbeat — that gives us the exit's static DH pubkey.
 *  3. Generate ephemeral X25519, build [JoinMessage] (with identity proof),
 *     publish to `vpn/{exit}/control/join`, await [AssignMessage] on
 *     `vpn/{exit}/{name}/control` (≤ 30 s; 2 s republish on no-reply).
 *  4. Derive [SessionCrypto], hand the FD off to [PacketPipeline].
 *  5. Re-publish the join every 60 s as a keepalive (matches Go / Swift).
 *
 * Thread model: state mutation runs on the engine's coroutine scope (single
 * dispatcher); MQTT transport callbacks land on the transport scheduler thread
 * and post back via the scope.
 */
internal class TunnelEngine(
    private val service: VpnService,
    private val config: TunnelConfig,
    private val mqttPassword: String,
    private val identityStore: IdentityKeyStore,
    private val onErrorReport: (TunnelErrorReport) -> Unit,
    private val onTerminate: (reason: String) -> Unit,
    /**
     * Last successfully-connected exit ID, used by the auto-resolve
     * fallback chain when the discovery tracker stays empty after the
     * gather window. Caller (VertexVpnService) reads it from the same
     * persistent store that [onResolvedExit] writes to. Null on first
     * launch.
     */
    private val lastGoodExit: String? = null,
    /**
     * Invoked once per successful connect with the resolved exit ID.
     * Caller persists it for the next session's auto-resolve fallback
     * chain. Default no-op so test paths don't need the callback.
     */
    private val onResolvedExit: (String) -> Unit = {},
) {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private lateinit var transport: MqttTransport
    private var pipeline: PacketPipeline? = null
    private var tunFd: ParcelFileDescriptor? = null

    /** Identity for proof of ownership. Generated/loaded lazily. */
    private val identity: IdentityKey by lazy { identityStore.loadOrCreate() }

    /** Ephemeral DH for this session — fresh on every connect. */
    private val ephemeralDh: X25519.KeyPair by lazy { X25519.generate() }

    /** Set once we've received the discovery heartbeat with the exit's DH pubkey. */
    @Volatile private var exitDhPubkey: ByteArray? = null

    /** Set once we've received the assign response. */
    @Volatile private var assigned: AssignMessage? = null

    /** Resolved exit ID. Equals `config.selectedExit` for explicit picks
     * and is replaced with the auto-resolve winner when the user picked
     * "auto". Reset to "auto" while we resolve so the discovery subscribe
     * filters and the join handshake see the real exit only after the
     * scoring decision is final. */
    @Volatile private var resolvedExit: String = if (config.selectedExit == "auto") "auto" else config.selectedExit

    /** Accumulates retained discovery heartbeats from `discovery/exits/+`
     * and runs the same scoring logic as Go and Swift. Same shared
     * formula (`score = rtt * (1 + clients/cap * 2.0)`) and 1.5x flap-guard. */
    private val discoveryTracker = DiscoveryTracker()

    /** Start time stamp for stats reporting. */
    private var connectedSinceEpochMs: Long? = null

    /**
     * Flips to true once [PacketPipeline.start] has been invoked with a valid
     * crypto session. While true, transport-level reconnect churn (CONNACK
     * lost / Reconnecting) is *not* propagated as a UI status downgrade — the
     * data plane keeps shipping packets across short MQTT hiccups, and a
     * status flap to RECONNECTING/HANDSHAKING also nukes
     * [connectedSinceEpochMs] in [publishStatus], which the StatusPill renders
     * as "uptime resets every blip". Real teardowns route through
     * [MqttTransport.onFatalError] → [onTerminate] → [stop].
     */
    @Volatile private var dataPlaneUp: Boolean = false

    /** ConnectivityManager network callback; non-null only between [start]/[stop]. */
    private var networkCallback: ConnectivityManager.NetworkCallback? = null

    fun start() {
        publishStatus(ConnectionState.CONNECTING)
        // Defer transport creation until after the broker probe — the probe
        // may take up to BROKER_PROBE_TIMEOUT_MS, and we want the very first
        // connect attempt to go to the closest broker, not rely on autopaho's
        // sticky reorder to kick in after the first timeout. See the
        // matching iOS comment in PacketTunnelProvider startTunnel step 5.
        scope.launch { runHandshakeAndDataPlane() }
    }

    fun stop() {
        unregisterNetworkCallback()
        dataPlaneUp = false
        // Cancel our coroutines *before* tearing the transport down, so any
        // queued events on the MQTT scheduler thread don't bounce into a
        // partly-shut-down scope.launch and log a CancellationException —
        // and so the transport-state collector can't fire one last
        // DISCONNECTED → DISCONNECTED transition right after we publish the
        // explicit stop status below.
        scope.cancel()
        // `transport` is created inside runHandshakeAndDataPlane after the
        // probe — a fast disconnect (rapid Connect → Disconnect tap, or
        // Connect intent landing right before service.onDestroy) can call
        // stop() before that point, leaving the lateinit uninitialized.
        // Use the explicit isInitialized guard rather than catching a
        // generic Throwable so unrelated transport.stop() failures still
        // surface in logs.
        if (::transport.isInitialized) {
            try { transport.stop() } catch (t: Throwable) {
                Timber.tag(TAG).w(t, "transport.stop() raised")
            }
        }
        // PacketPipeline owns the TUN fd and closes it in stop(); calling
        // ParcelFileDescriptor.close() a second time throws IllegalStateException.
        pipeline?.stop()
        tunFd = null
        publishStatus(ConnectionState.DISCONNECTED)
    }

    // ---------------- Handshake + data plane ----------------

    private suspend fun runHandshakeAndDataPlane() {
        try {
            // 0. Probe broker TCP-RTT and reorder when the user picked
            // "auto". For an explicit pick TunnelController has already
            // moved the chosen URL to the front; we skip the probe and
            // honour user choice — matches iOS PacketTunnelProvider step 5.
            val probedBrokers: List<BrokerUrl> = if (config.selectedBroker == "auto") {
                val (sorted, rtts) = BrokerProbe.reorderWithRtts(
                    config.brokerUrls,
                    timeoutMs = BROKER_PROBE_TIMEOUT_MS,
                )
                Timber.tag(TAG).i(
                    "broker probe (auto, ${sorted.size}): ${BrokerProbe.formatOrder(sorted, rtts)}"
                )
                sorted
            } else {
                Timber.tag(TAG).i("broker pinned by user: ${config.selectedBroker}")
                config.brokerUrls
            }

            // 1. Build MQTT transport on the probed/explicit broker order.
            val protector = SocketProtector { socket -> service.protect(socket) }
            // clientId carries a per-session suffix so a freshly-launched
            // tunnel doesn't collide with a still-expiring ghost session of
            // the previous process on the broker. With cleanStart=true *and*
            // identical clientId, mosquitto sometimes lets both sessions
            // exist for up to one session-expiry window and they take turns
            // kicking each other — observed as ~30 s reconnect churn after
            // rapid stop/start cycles. The auth username stays canonical
            // (broker ACLs key off it).
            val username = "vtx-client-${config.clientName}"
            val clientId = "$username-${System.currentTimeMillis().toString(36)}"
            transport = MqttTransport(
                initialBrokers = probedBrokers,
                username = username,
                password = mqttPassword,
                clientId = clientId,
                keepAliveSeconds = 20,
                socketProtector = protector,
                onAuthFailure = { rc, reason ->
                    writeReport(TunnelErrorKind.AUTHENTICATION, "code=$rc — $reason")
                    onTerminate("auth: $reason")
                },
                onFatalError = { reason ->
                    writeReport(TunnelErrorKind.UNKNOWN, reason)
                    onTerminate(reason)
                },
            )

            // Wire transport state into UI status. After data-plane up the
            // collector ignores transport churn for everything but the
            // broker-name refresh — see [dataPlaneUp].
            scope.launch {
                transport.state.collect { st ->
                    if (dataPlaneUp) {
                        if (st is TransportState.Connected) {
                            publishStatus(ConnectionState.CONNECTED, broker = st.broker)
                        }
                        return@collect
                    }
                    when (st) {
                        is TransportState.Connecting -> publishStatus(ConnectionState.CONNECTING, broker = st.broker)
                        is TransportState.Connected -> publishStatus(ConnectionState.HANDSHAKING, broker = st.broker)
                        is TransportState.Reconnecting -> publishStatus(ConnectionState.RECONNECTING, broker = st.broker)
                        TransportState.Disconnected -> publishStatus(ConnectionState.DISCONNECTED)
                    }
                }
            }

            registerNetworkCallback()

            transport.start()
            // 2. Wait for transport ready (CONNACK arrived). 15s budget.
            val ready = withTimeoutOrNull(15_000) {
                while (scope.isActive) {
                    if (transport.isReady) return@withTimeoutOrNull true
                    delay(50)
                }
                false
            }
            if (ready != true) {
                writeReport(TunnelErrorKind.UNKNOWN, "MQTT broker timeout")
                onTerminate("broker timeout")
                return
            }

            // 2. Subscribe to all discovery heartbeats. Always wildcard so
            // the tracker accumulates every exit's score regardless of
            // whether the user picked auto or a specific edge — a Phase 2
            // rebalance can then act on a populated dataset.
            val discoveryTopic = Topics.DISCOVERY_ALL
            val heartbeatChan = kotlinx.coroutines.channels.Channel<DiscoveryHeartbeat>(capacity = 16)
            transport.subscribe(discoveryTopic) { _, payload ->
                runCatching { WireJson.decodeFromString(DiscoveryHeartbeat.serializer(), String(payload, Charsets.UTF_8)) }
                    .getOrNull()?.let { hb ->
                        discoveryTracker.handle(hb)
                        heartbeatChan.trySend(hb)
                    }
            }

            // 3. Resolve "auto" via DiscoveryTracker (1.5s gather window
            // for retained heartbeats, then poll up to 10s, then fallback
            // chain). Explicit picks stay as-is.
            val brokerHost = transport.currentBroker?.host ?: ""
            if (resolvedExit == "auto") {
                resolvedExit = resolveAutoExit(brokerHost, timeoutMs = 10_000)
                Timber.tag(TAG).i("auto-resolved exit: $resolvedExit (broker=$brokerHost)")
            }

            // 4. Get exit's DH pubkey for identity proof. Prefer the
            // tracker — when the heartbeat already arrived (every auto
            // resolve and most explicit picks once retained-message round
            // arrives), this is instant. Otherwise wait on the channel
            // for the matching heartbeat with timeout.
            val hb: DiscoveryHeartbeat? = discoveryTracker.info(resolvedExit)?.let {
                if (it.dhPubkey.isNullOrEmpty()) null
                else DiscoveryHeartbeat(
                    id = it.id, country = it.country, clients = it.clients,
                    maxClients = it.maxClients, brokerRttMs = it.brokerRttMs,
                    uptime = null, ts = null, dhPubkey = it.dhPubkey,
                )
            } ?: run {
                // Tracker had nothing for `resolvedExit`. Common cause: the
                // fallback chain landed on `lastGoodExit` (or last-resort
                // "aws") because no heartbeats arrived within the gather
                // window. If the chosen exit is genuinely offline now, this
                // wait will time out at 10s and we'll surface
                // DISCOVERY_TIMEOUT — log explicitly so logcat tells the
                // operator why a connect attempt feels slow.
                Timber.tag(TAG).w("no heartbeat in tracker for $resolvedExit — waiting on channel (offline exit?)")
                withTimeoutOrNull(10_000) {
                    while (scope.isActive) {
                        val h = heartbeatChan.receive()
                        if (h.dhPubkey.isNullOrEmpty()) continue
                        if (h.id == resolvedExit) return@withTimeoutOrNull h
                    }
                    null
                }
            }
            if (hb == null) {
                writeReport(TunnelErrorKind.DISCOVERY_TIMEOUT, resolvedExit)
                onTerminate("discovery timeout")
                return
            }
            exitDhPubkey = hb.dhPubkey?.base64ToBytes()
            // Stop dispatching heartbeats — broker keeps publishing them at
            // its own cadence, but we no longer need them after the join.
            // Without this the channel saturates at capacity and the lambda
            // keeps decoding JSON for every payload until [stop].
            transport.unsubscribe(discoveryTopic)
            heartbeatChan.close()
            Timber.tag(TAG).i("discovered exit ${hb.id}; subscribing to control topic")

            // 4. Subscribe to our control topic and wait for AssignMessage.
            val assignChan = kotlinx.coroutines.channels.Channel<AssignMessage>(capacity = 4)
            transport.subscribe(Topics.control(resolvedExit, config.clientName)) { _, payload ->
                val text = String(payload, Charsets.UTF_8)
                runCatching { WireJson.decodeFromString(AssignMessage.serializer(), text) }
                    .getOrNull()?.let { assignChan.trySend(it) }
                    ?: parseControlError(text)?.let { detail ->
                        writeReport(TunnelErrorKind.IDENTITY_REJECTED, detail)
                        onTerminate("control error: $detail")
                    }
            }

            // 5. Build join + retry every 2s up to 30s.
            val join = buildJoin(exitDhPubkey!!)
            val joinTopic = Topics.join(resolvedExit)
            val assign = withTimeoutOrNull(30_000) {
                while (scope.isActive) {
                    transport.publish(joinTopic, WireJson.encodeToString(JoinMessage.serializer(), join).toByteArray())
                    val a = withTimeoutOrNull(2_000) { assignChan.receive() }
                    if (a != null) return@withTimeoutOrNull a
                }
                null
            }
            if (assign == null) {
                writeReport(TunnelErrorKind.JOIN_TIMEOUT, resolvedExit)
                onTerminate("join timeout")
                return
            }
            assigned = assign
            // Same as the heartbeat channel: we have the assignment; further
            // control-topic deliveries are not consumed (the topic is only
            // used for the initial assign + occasional error replies, which
            // are now best-effort logged in [parseControlError] off-path).
            transport.unsubscribe(Topics.control(resolvedExit, config.clientName))
            assignChan.close()
            Timber.tag(TAG).i("assigned ip=${assign.ip} gw=${assign.gw}")

            // 6. Bring up the TUN.
            val fd = openTun(assign) ?: run {
                writeReport(TunnelErrorKind.CONFIGURATION, "TUN.establish() returned null")
                onTerminate("tun establish failed")
                return
            }
            tunFd = fd

            // 7. Derive crypto. Session DH peer key MUST come from AssignMessage,
            // not from the heartbeat: the heartbeat-published `dhPubkey` is used
            // for the identity proof in [buildJoin] (mirroring iOS), while the
            // assign payload carries the exit's session-bound DH pubkey. If the
            // broker omits assign.dh, treat the exchange as malformed and fail
            // — silently substituting the heartbeat key would couple proof and
            // session keys and break once the exit starts rotating session DH.
            val sessionExitPub = assign.dh?.base64ToBytes() ?: run {
                writeReport(TunnelErrorKind.CONFIGURATION, "assign without dh field")
                onTerminate("missing assign.dh")
                return
            }
            val session = SessionCrypto.fromDH(
                myPrivateKey = ephemeralDh.privateKey,
                theirPublicKey = sessionExitPub,
                clientPublicKey = ephemeralDh.publicKey,
                exitPublicKey = sessionExitPub,
            )

            // 8. Subscribe to download topic, hook pipeline.
            val pipe = PacketPipeline(fd, publishUpload = { sealed ->
                transport.publish(Topics.upload(resolvedExit, config.clientName), sealed)
            })
            pipe.setSession(session)
            pipe.start { handler ->
                transport.subscribe(Topics.download(resolvedExit, config.clientName)) { _, payload ->
                    handler(payload)
                }
            }
            pipeline = pipe

            // 9. Mark connected; start keepalive. From this point on the
            // transport-state collector treats CONNECTED as sticky — see
            // [dataPlaneUp]. Setting the flag *before* publishStatus so a
            // racing TransportState.Connected from the same scheduler tick
            // can't downgrade us to HANDSHAKING right after we publish.
            connectedSinceEpochMs = System.currentTimeMillis()
            dataPlaneUp = true
            publishStatus(
                ConnectionState.CONNECTED,
                broker = transport.currentBroker?.host,
                exit = resolvedExit,
                ip = assign.ip,
            )
            // Persist resolved exit so the host UI shows the correct edge
            // immediately on relaunch (Auto · STO) and the auto-resolve
            // fallback chain can pick it up on the next connect when the
            // discovery tracker is empty.
            runCatching { onResolvedExit(resolvedExit) }
            startKeepalive(joinTopic, join)
            startStatsTicker()
        } catch (ce: kotlinx.coroutines.CancellationException) {
            // Coroutine cancellation must propagate so child structured-
            // concurrency blocks (probe, withTimeoutOrNull, transport
            // collector) tear down deterministically. `stop()` already
            // ran scope.cancel(); rethrow ends this launch quietly without
            // funnelling through onTerminate (which would re-trigger
            // VertexVpnService.stopTunnel and racing the in-flight stop).
            throw ce
        } catch (t: Throwable) {
            if (scope.isActive) {
                Timber.tag(TAG).e(t, "tunnel orchestration failed")
                writeReport(TunnelErrorKind.UNKNOWN, t.message ?: "exception")
                onTerminate(t.message ?: "exception")
            }
        }
    }

    /**
     * Apply networking flags before [openTun] builds the TUN. Lifted out so
     * the call site reads as a flat builder chain.
     *
     * - `setMetered(false)`: tunnel mirrors the underlying Wi-Fi's unmetered
     *   posture; Play Store / GMS otherwise throttle background work and
     *   skip some probes on a metered link.
     * - `allowBypass()`: lets apps that explicitly request a non-VPN network
     *   (`ConnectivityManager.bindProcessToNetwork(...)`, `NetworkRequest`
     *   with NOT_VPN, etc.) actually reach the underlying transport. The
     *   "no internet" cross next to the Wi-Fi icon and the GMS auth
     *   final-step failures both stem from system clients getting routed
     *   through a tunnel they didn't ask for; allowing the bypass restores
     *   their ability to use the validated base network — which is also
     *   what stock VpnService clients (sing-box-for-android, WireGuard,
     *   Outline) ship with.
     *
     * `service.setUnderlyingNetworks(null)` was tried here too but it
     * triggered a system_server crash on Sony Xperia (Android 13):
     * `com.sonymobile.smartnetworkengine` died with DeadSystemException
     * trying to read the VPN's underlying-networks list, cascade-killing
     * com.android.settings and the launcher. AOSP defaults to "system
     * tracks the active default network" without that call, so we leave
     * it out and let the platform decide.
     */
    private fun applyVpnNetworkPolicy(builder: VpnService.Builder) {
        runCatching { builder.setMetered(false) }
        runCatching { builder.allowBypass() }
    }

    private fun openTun(a: AssignMessage): ParcelFileDescriptor? {
        val builder = service.Builder()
            .setSession("Vertex")
            .setMtu(PacketPipeline.MTU)
            .addDnsServer("1.1.1.1")
            .addDnsServer("8.8.8.8")
            .addRoute("0.0.0.0", 0)
            // IPv6 default route into the tunnel even though our backend is
            // v4-only — without it Android sees an IPv4-only VPN and treats
            // the validation status as partial (contributing to the Wi-Fi
            // cross), and IPv6-capable apps may dual-stack out via the base
            // network and leak. PacketPipeline drops non-IPv4 frames in
            // runUpLoop, so v6 traffic dies in the kernel rather than
            // escaping. Same behaviour as sing-box-for-android / WireGuard.
            .addRoute("::", 0)
        applyVpnNetworkPolicy(builder)

        // Split tunnel: exclude RU CIDRs from the tunnel so .ru/.рф traffic
        // resolved to RU IPs goes straight via the underlying network. iOS
        // does this via NEIPv4Settings.excludedRoutes; Android exposes the
        // equivalent only on API 33+ (Tiramisu) — on older devices we keep
        // full-tunnel routing and log a warning.
        if (config.splitTunnelEnabled) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                val cidrs = RUNetsLoader.load(service.applicationContext)
                var added = 0
                for (p in cidrs) {
                    runCatching { builder.excludeRoute(p); added++ }
                }
                Timber.tag(TAG).i("split-tunnel: excluded $added RU CIDRs")
            } else {
                Timber.tag(TAG).w("split-tunnel requested but API ${Build.VERSION.SDK_INT} < 33 — running full tunnel")
            }
        }

        // Operator-owned RU services that must ALWAYS bypass the tunnel — even
        // with split-tunnel off and regardless of the RUNetsLoader cap that
        // drops the /20–/24 tail of the RU zone (the mutter home server lives
        // in that dropped tail). Without this the app can't reach its own
        // RU-hosted server while the VPN is on. See PriorityBypassNets.
        // excludeRoute is API 33+ only, same as the RU exclusions above.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            var pinned = 0
            for (p in PriorityBypassNets.cidrs) {
                runCatching { builder.excludeRoute(p); pinned++ }
            }
            if (pinned > 0) Timber.tag(TAG).i("priority bypass: excluded $pinned operator CIDRs")
        }

        // Keep our own app's traffic OFF the tunnel — otherwise the MQTT
        // keepalive socket loops back through the very tunnel it's carrying
        // and stalls within seconds (DNS lookups for the broker host fail
        // with EAI_NODATA the moment the TUN takes over default routing).
        // Standard Android VPN-client pattern (used by WireGuard etc.):
        // VpnService.protect() can only succeed AFTER establish() has run,
        // so we can't rely on protect() for the very first MQTT socket; the
        // disallowed-application list is enforced from the moment the TUN
        // comes up and is the only safe primitive for chicken-and-egg
        // bootstrap traffic on Android.
        runCatching { builder.addDisallowedApplication(service.packageName) }

        // Address with /24 — same default as Go exits.
        val prefix = parsePrefix(a.mask) ?: 24
        builder.addAddress(a.ip, prefix)

        val fd = builder.establish() ?: return null

        // Android opens the TUN file descriptor in NON-BLOCKING mode by default
        // (see VpnService.Builder.establish() Javadoc — "The descriptor is
        // non-blocking by default to allow asynchronous use"). With a blocking
        // FileInputStream wrapper, read() on an empty TUN returns 0 instead
        // of waiting for the next packet — which our up-loop interprets as
        // EOF and shuts the pipeline. Force blocking mode here so the read
        // loop can park on the kernel until a packet arrives.
        runCatching {
            val flags = android.system.Os.fcntlInt(fd.fileDescriptor, android.system.OsConstants.F_GETFL, 0)
            android.system.Os.fcntlInt(
                fd.fileDescriptor,
                android.system.OsConstants.F_SETFL,
                flags and android.system.OsConstants.O_NONBLOCK.inv(),
            )
        }
        return fd
    }

    /**
     * Resolve "auto" to a concrete exit ID using `discoveryTracker`'s
     * scoring logic.
     *
     * Always waits a minimum collection window (1.5s) before the first
     * decision: retained MQTT heartbeats arrive sequentially per topic
     * (`discovery/exits/aws`, `discovery/exits/sto`, …), and a no-wait
     * `bestExit` would race the first heartbeat in and pick that exit
     * as the only candidate. After the window, score-based selection
     * runs against the full snapshot. If the tracker is still empty,
     * the loop polls every 500ms up to `timeoutMs` total.
     *
     * Fallbacks when the tracker stays empty: (1) caller-supplied
     * `lastGoodExit`, (2) any stale tracker entry by minimum RTT,
     * (3) the hardcoded last-resort literal. Always returns a non-empty
     * exit ID.
     */
    private suspend fun resolveAutoExit(brokerHost: String, timeoutMs: Long): String {
        val gatherWindowMs = 1500L
        delay(gatherWindowMs)
        discoveryTracker.bestExit(brokerHost)?.let {
            logSnapshot(brokerHost, picked = it, source = "fast")
            return it
        }
        val deadline = System.currentTimeMillis() + (timeoutMs - gatherWindowMs).coerceAtLeast(0)
        while (System.currentTimeMillis() < deadline && scope.isActive) {
            delay(500)
            discoveryTracker.bestExit(brokerHost)?.let {
                logSnapshot(brokerHost, picked = it, source = "poll")
                return it
            }
        }
        lastGoodExit?.takeIf { it.isNotBlank() && it != "auto" }?.let {
            Timber.tag(TAG).w("auto-resolve fallback to lastGoodExit: $it")
            return it
        }
        discoveryTracker.snapshot(includeStale = true)
            .minByOrNull { it.brokerRttMs[brokerHost] ?: Int.MAX_VALUE }
            ?.let {
                Timber.tag(TAG).w("auto-resolve fallback to stale exit: ${it.id}")
                return it.id
            }
        Timber.tag(TAG).w("auto-resolve fallback to last-resort: $LAST_RESORT_EXIT")
        return LAST_RESORT_EXIT
    }

    /** Emit a one-line summary of every known exit's (rtt, clients,
     * computed score) for the given broker host plus the picked ID. Lets
     * us debug "why did Auto pick X" from `adb logcat` without re-running
     * the formula by hand. */
    private fun logSnapshot(brokerHost: String, picked: String, source: String) {
        val snapshot = discoveryTracker.snapshot(includeStale = true)
        val entries = snapshot.joinToString(" ") { info ->
            val rtt = info.brokerRttMs[brokerHost]
                ?: info.brokerRttMs.entries.firstOrNull { it.key.substringBefore(':') == brokerHost }?.value
                ?: -1
            val cap = if (info.maxClients > 0) info.maxClients else 253
            val rttD = if (rtt > 0) rtt.toDouble() else 100.0
            val score = rttD * (1.0 + info.clients.toDouble() / cap * 2.0)
            val ageS = (System.currentTimeMillis() - info.receivedAtMillis) / 1000
            "${info.id}(rtt=$rtt clients=${info.clients}/${info.maxClients} age=${ageS}s score=${"%.1f".format(score)})"
        }
        Timber.tag(TAG).i("auto-resolve [$source] broker=$brokerHost picked=$picked | $entries")
    }

    private fun parsePrefix(mask: String?): Int? {
        if (mask.isNullOrBlank()) return null
        return runCatching {
            // Pack the four octets into a single 32-bit big-endian integer and
            // enforce that bits form a contiguous run of 1s followed by 0s
            // (the only valid IPv4 mask shape). A naive bitCount accepts
            // 255.0.255.0 which would silently misroute traffic.
            val octets = mask.split('.')
            require(octets.size == 4) { "mask must have 4 octets, got ${octets.size}" }
            var packed = 0
            for (o in octets) {
                val n = o.toInt()
                require(n in 0..255) { "octet out of range: $n" }
                packed = (packed shl 8) or n
            }
            // Contiguous prefix: ~packed + 1 must be a power of two (or 0 for /0).
            // Equivalent: complement is one bit less than a power of two, i.e.
            // ((~packed) and ((~packed) + 1)) == ~packed when ~packed is a
            // contiguous run of low-order 1s. Easier check: leading-ones count
            // must equal total set bits.
            val ones = Integer.bitCount(packed)
            val leading = Integer.numberOfLeadingZeros(packed.inv())
            require(leading == ones) { "non-contiguous mask: $mask" }
            ones
        }.getOrNull()?.takeIf { it in 0..32 }
    }

    private fun buildJoin(exitDhPub: ByteArray): JoinMessage {
        val proof = identity.proof(exitDhPub, config.clientName)
        return JoinMessage(
            name = config.clientName,
            dh = ephemeralDh.publicKey.toBase64(),
            id = identity.publicKeyBytes.toBase64(),
            idSig = proof.toBase64(),
        )
    }

    private fun startKeepalive(joinTopic: String, join: JoinMessage) {
        scope.launch {
            val payload = WireJson.encodeToString(JoinMessage.serializer(), join).toByteArray()
            while (isActive) {
                delay(60_000)
                transport.publish(joinTopic, payload)
            }
        }
    }

    private fun startStatsTicker() {
        scope.launch {
            while (isActive) {
                delay(1_000)
                val pipe = pipeline ?: continue
                TunnelStateBus.publishStats(
                    TunnelStats(
                        bytesUp   = pipe.bytesUp.get(),
                        bytesDown = pipe.bytesDown.get(),
                        packetsUp = pipe.packetsUp.get(),
                        packetsDown = pipe.packetsDown.get(),
                    )
                )
            }
        }
    }

    // ---------------- Path monitor ----------------

    /**
     * Watch the *underlying* default network (i.e. the one the VPN sits on
     * top of, ignoring the TUN itself) and force-reconnect MQTT whenever
     * it actually changes. The MQTT TCP socket would otherwise sit blocked
     * on a silent path-change for the full keep-alive window (≥ 20 s)
     * before PINGRESP timeout flips the link to dead — visible to the user
     * as a dead tunnel that doesn't reconnect for half a minute after a
     * wifi/cell handoff.
     *
     * iOS gets this via [NWPathMonitor]; on Android we mirror what
     * sing-box-for-android does (battle-tested across thousands of devices
     * and OEM quirks):
     *
     *  - **API 31+:** `registerBestMatchingNetworkCallback(request)` —
     *    fires `onAvailable` once when the system's best-matching network
     *    *changes*, not for every interface that satisfies the filter.
     *  - **API 28-30:** `requestNetwork(request)` — explicit request to
     *    avoid the AOSP P-DP1 bug where `registerDefaultNetworkCallback`
     *    starts returning the VPN interface (us) once the tunnel is up.
     *  - **API 26-27:** `registerDefaultNetworkCallback()` — pre-bug.
     *
     * The request keeps `NET_CAPABILITY_NOT_VPN` (default on `Builder`)
     * so we never observe our own TUN.
     *
     * `onAvailable` for a *different* `Network` than the previous one is
     * a real default-network handoff event → MQTT must rebuild on the
     * new path, otherwise the existing socket is stuck on the dead one
     * until PINGRESP timeout (~20 s) terminates it.
     */
    private fun registerNetworkCallback() {
        val cm = service.getSystemService(ConnectivityManager::class.java) ?: return
        var lastHandle: Long? = null
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                val handle = network.networkHandle
                val previous = lastHandle
                lastHandle = handle
                if (previous == null) {
                    Timber.tag(TAG).i("default network: initial $handle (registration)")
                    return
                }
                if (previous == handle) return
                Timber.tag(TAG).i("default network change: $previous → $handle — force reconnect")
                runCatching { transport.forceReconnect("default-network-change") }
            }
            override fun onLost(network: Network) {
                if (lastHandle == network.networkHandle) {
                    Timber.tag(TAG).i("default network lost: ${network.networkHandle}")
                    lastHandle = null
                    // Don't force-reconnect here — when the only path goes
                    // away there is nothing to reconnect TO. MQTT's own
                    // PINGRESP timeout will mark the link dead, and the
                    // next `onAvailable` (when a network reappears) will
                    // trigger the reconnect on the new path.
                }
            }
            override fun onLinkPropertiesChanged(network: Network, lp: LinkProperties) {
                // Same Network handle, different interface — e.g. an
                // enterprise Wi-Fi roam. The TCP socket may now be bound
                // to a stale 5-tuple; force a fresh PINGREQ so PINGRESP
                // timeout fires within 5 s instead of waiting for the
                // next scheduled ping (~15 s).
                if (lastHandle == network.networkHandle) {
                    runCatching { transport.checkLiveness() }
                }
            }
            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) { /* no-op */ }
        }
        runCatching {
            // `NetworkRequest.Builder()` carries `NET_CAPABILITY_NOT_VPN`
            // by default → callbacks observe the physical transport, not
            // our own TUN once it's up.
            val request = android.net.NetworkRequest.Builder()
                .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                .build()
            when {
                Build.VERSION.SDK_INT >= Build.VERSION_CODES.S ->
                    cm.registerBestMatchingNetworkCallback(request, cb, android.os.Handler(android.os.Looper.getMainLooper()))
                Build.VERSION.SDK_INT >= Build.VERSION_CODES.P ->
                    cm.requestNetwork(request, cb)
                else ->
                    cm.registerDefaultNetworkCallback(cb)
            }
            networkCallback = cb
        }.onFailure { Timber.tag(TAG).w(it, "registerNetworkCallback failed") }
    }

    private fun unregisterNetworkCallback() {
        val cb = networkCallback ?: return
        networkCallback = null
        runCatching {
            service.getSystemService(ConnectivityManager::class.java)?.unregisterNetworkCallback(cb)
        }
    }

    // ---------------- Helpers ----------------

    private fun publishStatus(
        state: ConnectionState,
        broker: String? = null,
        exit: String? = null,
        ip: String? = null,
        error: String? = null,
    ) {
        val current = TunnelStateBus.status.value
        TunnelStateBus.publishStatus(
            ConnectionStatus(
                state = state,
                assignedIp = ip ?: current.assignedIp,
                currentBroker = broker ?: current.currentBroker,
                currentExit = exit ?: current.currentExit,
                connectedSinceEpochMs = if (state == ConnectionState.CONNECTED) connectedSinceEpochMs ?: current.connectedSinceEpochMs else null,
                lastError = error ?: current.lastError,
            )
        )
    }

    private fun parseControlError(payload: String): String? = try {
        val obj = WireJson.parseToJsonElement(payload) as? JsonObject ?: return null
        obj["error"]?.jsonPrimitive?.contentOrNull
    } catch (_: Throwable) {
        null
    }

    private fun writeReport(kind: TunnelErrorKind, detail: String) {
        onErrorReport(TunnelErrorReport(kind = kind, detail = detail))
    }

    /** Hand-off when [VpnService.protect] is needed for a non-MQTT socket — currently unused. */
    @Suppress("unused")
    fun protect(broker: BrokerUrl) {
        // intentionally empty — broker traffic is protected via MqttTransport's SocketProtector.
    }

    companion object {
        private const val TAG = "vtx-eng"

        /** Hardcoded last-resort exit when auto-resolve has nothing —
         * empty tracker, no `lastGoodExit`, no stale entries. Single
         * literal so the fallback chain references the same value as
         * iOS / macOS. */
        private const val LAST_RESORT_EXIT = "aws"

        /** Per-broker TCP-connect probe deadline. Long enough to cover
         * a worst-case cellular RTT to any of our brokers (≈400 ms
         * one-way from Russia + safety margin), short enough that
         * connect doesn't feel sluggish on a clean network. Failed
         * probes still go to the tail so a degraded broker stays
         * available as a fallback. Mirrors iOS `brokerProbeTimeout`. */
        private const val BROKER_PROBE_TIMEOUT_MS = 1_500L
    }
}
