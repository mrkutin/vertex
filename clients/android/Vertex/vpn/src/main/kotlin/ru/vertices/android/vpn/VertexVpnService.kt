package ru.vertices.android.vpn

import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.VpnService
import android.os.Build
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject
import ru.vertices.android.core.config.BrokerUrl
import ru.vertices.android.core.config.TunnelConfig
import ru.vertices.android.core.ipc.ConnectionStatus
import ru.vertices.android.core.ipc.TunnelErrorKind
import ru.vertices.android.core.ipc.TunnelErrorReport
import ru.vertices.android.vpn.identity.KeystoreIdentityKeyStore
import ru.vertices.android.vpn.identity.PasswordStore
import ru.vertices.android.vpn.notify.TunnelNotification
import timber.log.Timber

/**
 * Vertex VPN service. Single tunnel at a time.
 *
 * Lifecycle:
 *   UI calls VpnService.prepare() → user accepts → UI calls
 *   `startForegroundService(Intent(ACTION_CONNECT) + extras)` → service handles
 *   the intent, runs [TunnelEngine].
 *
 *   To disconnect: UI sends `Intent(ACTION_DISCONNECT)` (or taps the
 *   notification's Disconnect action).
 *
 * Phase 1 keeps this service in the same OS process as the UI; in Phase 2 the
 * AndroidManifest entry will move to `android:process=":vpn"` and the
 * StateFlow-based [TunnelStateBus] will be replaced with a Messenger-backed
 * cross-process bus.
 */
@AndroidEntryPoint
class VertexVpnService : VpnService() {

    @Inject lateinit var passwordStore: PasswordStore

    private var engine: TunnelEngine? = null

    /**
     * Reference to the daemon thread that mirrors [TunnelStateBus] into the
     * foreground notification. Must be interruptible from [stopTunnel] so the
     * thread doesn't outlive the service when the process keeps running with
     * an active Activity (Application stays alive even after `stopSelf` if
     * the UI is in the foreground; the thread would otherwise keep firing
     * status notifications forever).
     */
    @Volatile private var statusObserverThread: Thread? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_DISCONNECT -> {
                Timber.tag(TAG).i("DISCONNECT requested")
                stopTunnel("user disconnect")
                return START_NOT_STICKY
            }
            ACTION_CONNECT, null -> startTunnel(intent)
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        Timber.tag(TAG).i("onDestroy")
        stopTunnel("service destroyed")
        super.onDestroy()
    }

    override fun onRevoke() {
        // Triggered when the user revokes VPN permission from system settings,
        // or another VPN takes over. Surface the cause to the UI so the
        // StatusPill explains why the tunnel went down — without this the
        // user only sees DISCONNECTED with no explanation.
        Timber.tag(TAG).w("VPN permission revoked")
        persistError(
            TunnelErrorReport(
                kind = TunnelErrorKind.UNKNOWN,
                detail = "VPN permission revoked by user or another VPN took over",
            )
        )
        stopTunnel("revoked")
        super.onRevoke()
    }

    private fun startTunnel(intent: Intent?) {
        if (engine != null) {
            Timber.tag(TAG).w("startTunnel called with engine already running")
            return
        }

        // Promote to foreground IMMEDIATELY, before *any* validation. We were
        // started via `startForegroundService()` and Android grants ~5 s to
        // call `startForeground()` — if validation fails first and we
        // `stopSelf()` without ever promoting, the system kills the whole
        // process with `ForegroundServiceDidNotStartInTimeException`. Bailing
        // cleanly on a bad start Intent (empty broker list from a tap before
        // SRV resolved, missing password, etc.) requires the foreground
        // promise to already be honored — so we always honor it first, then
        // validate, then `stopForeground` + `stopSelf` on failure paths.
        val notif = TunnelNotification.build(this, ConnectionStatus.DISCONNECTED.copy())
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(
                TunnelNotification.NOTIFICATION_ID,
                notif,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED,
            )
        } else {
            startForeground(TunnelNotification.NOTIFICATION_ID, notif)
        }

        val cfg = parseConfig(intent) ?: run {
            Timber.tag(TAG).e("missing or invalid config in start intent")
            stopTunnel("invalid config")
            return
        }
        // Password is *never* carried in the start Intent — Intent extras land
        // in ActivityManagerService logs on debug builds and survive system
        // always-on-VPN restart hooks (Intent==null on those restarts would
        // also produce an empty password). Read it from the encrypted prefs
        // store via Hilt, the same store the UI's PasswordRepository writes to.
        val password = passwordStore.get()
        if (password.isEmpty()) {
            Timber.tag(TAG).e("no MQTT password configured — aborting start")
            stopTunnel("no password")
            return
        }

        TunnelStateBus.publishStatus(ConnectionStatus.DISCONNECTED.copy())

        val lastGoodExit = intent?.getStringExtra(EXTRA_LAST_GOOD_EXIT)?.takeIf { it.isNotBlank() }
        engine = TunnelEngine(
            service = this,
            config = cfg,
            mqttPassword = password,
            identityStore = KeystoreIdentityKeyStore(this),
            onErrorReport = { report -> persistError(report) },
            onTerminate = { reason -> stopTunnel(reason) },
            lastGoodExit = lastGoodExit,
            // Resolved-exit persistence happens UI-side: ConnectViewModel
            // observes `connectionStatus.currentExit` from TunnelStateBus
            // and writes it into the DataStore-backed `lastGoodExit`. The
            // service's only job is to expose the resolved value through
            // the live status, which `publishStatus` already does on
            // connect. Callback is left no-op for symmetry with iOS/macOS.
        ).also { it.start() }

        // Reflect status to the foreground notification whenever it changes.
        // We use a tight-cadence StateFlow collector via a daemon thread.
        startStatusObserver()
    }

    private fun stopTunnel(reason: String) {
        Timber.tag(TAG).i("stopTunnel: $reason")
        engine?.stop()
        engine = null
        statusObserverThread?.interrupt()
        statusObserverThread = null
        try { stopForeground(STOP_FOREGROUND_REMOVE) } catch (_: Throwable) {}
        stopSelf()
    }

    private fun startStatusObserver() {
        // Short-lived thread re-renders the notification on each distinct
        // status. Kept in a field so [stopTunnel] can interrupt it — without
        // that the thread keeps the StateFlow collector alive for the
        // lifetime of the Application, even after the service is destroyed.
        val t = Thread({
            var last: ConnectionStatus? = null
            try {
                kotlinx.coroutines.runBlocking {
                    TunnelStateBus.status.collect {
                        if (it != last) {
                            last = it
                            try {
                                val notif = TunnelNotification.build(this@VertexVpnService, it)
                                getSystemService(android.app.NotificationManager::class.java)
                                    ?.notify(TunnelNotification.NOTIFICATION_ID, notif)
                            } catch (_: Throwable) { /* ignore */ }
                        }
                    }
                }
            } catch (_: Throwable) { /* service shutting down */ }
        }, "vtx-status-watch").apply { isDaemon = true }
        statusObserverThread = t
        t.start()
    }

    private fun parseConfig(intent: Intent?): TunnelConfig? {
        intent ?: return null
        val brokerStrs = intent.getStringArrayExtra(EXTRA_BROKERS)?.toList()
            ?: intent.getStringExtra(EXTRA_BROKERS_CSV)?.split(",")?.map { it.trim() }
            ?: return null
        val brokers = brokerStrs.mapNotNull { BrokerUrl.parse(it) }
        if (brokers.isEmpty()) return null
        val name = intent.getStringExtra(EXTRA_NAME)?.takeIf { it.isNotBlank() } ?: return null
        val exit = intent.getStringExtra(EXTRA_EXIT)?.takeIf { it.isNotBlank() } ?: "auto"
        // `selectedBroker` defaults to "auto" so always-on / system-restart
        // intents (which arrive without our extras) still get the probe
        // path instead of crashing on a blank pin.
        val broker = intent.getStringExtra(EXTRA_SELECTED_BROKER)?.takeIf { it.isNotBlank() } ?: "auto"
        val split = intent.getBooleanExtra(EXTRA_SPLIT_TUNNEL, false)
        return TunnelConfig(
            brokerUrls = brokers,
            clientName = name,
            selectedExit = exit,
            selectedBroker = broker,
            splitTunnelEnabled = split,
        )
    }

    private fun persistError(report: TunnelErrorReport) {
        // Write to private prefs so the UI can read it on the next launch.
        // Phase 1: simple sharedPreferences entry; Phase 2 will route via Messenger.
        try {
            val prefs = getSharedPreferences(LAST_ERROR_PREFS, MODE_PRIVATE)
            prefs.edit()
                .putString(LAST_ERROR_KEY,
                    ru.vertices.android.core.protocol.WireJson.encodeToString(
                        TunnelErrorReport.serializer(), report,
                    )
                )
                .apply()
        } catch (t: Throwable) {
            Timber.tag(TAG).w(t, "failed to persist error report")
        }
        // Reflect the latest error in the UI status flow as well.
        val cur = TunnelStateBus.status.value
        TunnelStateBus.publishStatus(cur.copy(lastError = report.userMessage))
    }

    companion object {
        private const val TAG = "vtx-svc"

        const val ACTION_CONNECT = "ru.vertices.android.vpn.action.CONNECT"
        const val ACTION_DISCONNECT = "ru.vertices.android.vpn.action.DISCONNECT"

        // Extras for the start intent.
        /** String[] of broker URLs (mqtts://, wss://, etc.). */
        const val EXTRA_BROKERS = "vtx.brokers"
        /** Comma-separated alternative for [EXTRA_BROKERS]. Either is accepted. */
        const val EXTRA_BROKERS_CSV = "vtx.brokers.csv"
        const val EXTRA_NAME = "vtx.name"
        const val EXTRA_EXIT = "vtx.exit"
        /** "auto" or one of the broker URLs. When "auto", TunnelEngine
         * runs a TCP-RTT probe and reorders by latency; an explicit URL
         * pins it to the head and skips the probe. */
        const val EXTRA_SELECTED_BROKER = "vtx.selectedBroker"
        /** Boolean — when true, RU CIDRs are excluded from the tunnel (default false). */
        const val EXTRA_SPLIT_TUNNEL = "vtx.splitTunnel"
        /** Last successfully-connected exit ID, optional. Used as a fallback by
         * [TunnelEngine] when the discovery tracker is empty after the
         * gather window. Empty / missing when no prior successful connect. */
        const val EXTRA_LAST_GOOD_EXIT = "vtx.lastGoodExit"

        const val LAST_ERROR_PREFS = "vtx_tunnel_error"
        const val LAST_ERROR_KEY = "last"
    }
}
