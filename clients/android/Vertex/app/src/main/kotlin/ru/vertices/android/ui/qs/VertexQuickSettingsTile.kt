package ru.vertices.android.ui.qs

import android.app.PendingIntent
import android.content.Intent
import android.graphics.drawable.Icon
import android.net.VpnService
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import dagger.hilt.android.AndroidEntryPoint
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import ru.vertices.android.R
import ru.vertices.android.app.MainActivity
import ru.vertices.android.core.ipc.ConnectionState
import ru.vertices.android.core.ipc.ConnectionStatus
import ru.vertices.android.di.ApplicationScope
import ru.vertices.android.repository.PasswordRepository
import ru.vertices.android.repository.SettingsRepository
import ru.vertices.android.repository.SrvDiscovery
import ru.vertices.android.repository.TunnelController
import ru.vertices.android.vpn.TunnelStateBus
import javax.inject.Inject
import timber.log.Timber

/**
 * Quick Settings tile that mirrors the big Connect button on the home screen.
 *
 * Lifecycle:
 *  - `onStartListening` opens a coroutine scope that mirrors
 *    [TunnelStateBus.status] into the tile's state/label/subtitle. The first
 *    paint comes from `TunnelStateBus.status.value` so a tunnel that's already
 *    up doesn't flash "Off" for a frame.
 *  - `onStopListening` cancels that collector. We re-subscribe cheaply on the
 *    next pull-down because [TunnelStateBus] is a process-singleton hot
 *    StateFlow that retains the latest value across the gap.
 *
 * Click semantics:
 *  - Disconnected + permission granted + creds present → [TunnelController.connect].
 *    The connect launch runs in [appScope] (application-lifetime), **not** the
 *    listening scope, because the shade collapses on tap and `onStopListening`
 *    arrives milliseconds later — a viewModelScope-style cancellation would
 *    silently drop the service-start intent.
 *  - Disconnected + missing permission OR missing creds → [launchMainForPermission].
 *    The system VPN dialog can only be shown by an Activity, and the
 *    onboarding flow lives in MainActivity, so for both cases we punt to the
 *    UI rather than dispatch a doomed connect.
 *  - Any state ≠ DISCONNECTED → [TunnelController.disconnect]. Clicking
 *    during CONNECTING is a perfectly reasonable cancel and the service treats
 *    it as an early stop.
 *
 * Phase 2 caveat: [TunnelStateBus] is a process-local singleton. Once
 * `VertexVpnService` moves to `android:process=":vpn"` (see PLAN), the tile
 * (which lives in the main process) will silently observe stale state. The
 * fix at that point is to swap the StateFlow read for the same Messenger /
 * AIDL bus the UI ViewModels migrate to.
 */
@AndroidEntryPoint
class VertexQuickSettingsTile : TileService() {

    @Inject lateinit var controller: TunnelController
    @Inject lateinit var settings: SettingsRepository
    @Inject lateinit var srv: SrvDiscovery
    @Inject lateinit var passwords: PasswordRepository
    @Inject @ApplicationScope lateinit var appScope: CoroutineScope

    /**
     * Coroutine scope owned by the tile's listening window. Re-created in
     * [onStartListening] and cancelled in [onStopListening] so a tile that
     * scrolled out of view stops collecting.
     *
     * Used **only** for status mirroring — click-time work uses [appScope] so
     * it survives the shade collapse that follows every tap.
     */
    private var listenScope: CoroutineScope? = null

    /**
     * Per-click guard. Synchronously set in [onClick] before any async work,
     * so a 50 ms double-tap can't dispatch two service-start intents (each
     * `onClick` reads `TunnelStateBus.status.value` which only updates once
     * the foreground service has actually run, several Binder hops later).
     */
    @Volatile private var clickInFlight: Job? = null

    override fun onStartListening() {
        super.onStartListening()
        renderTile(TunnelStateBus.status.value)
        val s = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        listenScope = s
        s.launch { TunnelStateBus.status.collect { renderTile(it) } }
    }

    override fun onStopListening() {
        listenScope?.cancel()
        listenScope = null
        super.onStopListening()
    }

    override fun onClick() {
        super.onClick()
        if (clickInFlight?.isActive == true) {
            Timber.tag(TAG).i("click ignored — previous click still in flight")
            return
        }
        val current = TunnelStateBus.status.value.state
        Timber.tag(TAG).i("click state=%s", current)
        clickInFlight = appScope.launch {
            try {
                if (current == ConnectionState.DISCONNECTED) {
                    handleConnectClick()
                } else {
                    controller.disconnect()
                }
            } finally {
                // Free the latch ~immediately — we only need it to survive
                // the few-ms-window between two consecutive Binder dispatches
                // for the same tap gesture, not for the whole connect.
                clickInFlight = null
            }
        }
    }

    private suspend fun handleConnectClick() {
        // VpnService.prepare returns non-null when the user has not yet
        // confirmed the system VPN-permission dialog for this app. From a
        // TileService we cannot show the system dialog — only an Activity
        // context can — so collapse the shade into MainActivity and let the
        // existing permission flow run.
        if (VpnService.prepare(applicationContext) != null) {
            Timber.tag(TAG).i("VPN permission missing — punting to MainActivity")
            launchMainForPermission()
            return
        }
        // Refuse early on cold-start with no credentials: dispatching a connect
        // with an empty MQTT password would make the service authenticate, fail,
        // and stop without surfacing anything the user can act on from the
        // shade. Route through MainActivity so onboarding has a chance to run.
        if (passwords.get().isBlank()) {
            Timber.tag(TAG).i("no MQTT password configured — punting to MainActivity")
            launchMainForPermission()
            return
        }
        val brokers = currentBrokers()
        if (brokers.isEmpty()) {
            Timber.tag(TAG).w("no brokers available — punting to MainActivity")
            launchMainForPermission()
            return
        }
        controller.connect(brokers)
    }

    private suspend fun currentBrokers(): List<String> {
        // SRV cache is populated on the first successful DoH resolve from
        // the UI. If empty (clean install + offline + tile tap before the
        // user has ever opened the app), [handleConnectClick] punts to
        // MainActivity rather than dispatching a connect against zero
        // brokers — same path as missing VPN permission or empty password.
        return runCatching { srv.loadCache() }.getOrNull()?.brokerUrls.orEmpty()
    }

    private fun launchMainForPermission() {
        // System VPN dialog can only be shown by an Activity in the
        // foreground. From the lock screen, `startActivityAndCollapse` is
        // silently a no-op on most OEMs unless we first ask the system to
        // unlock — without unlockAndRun the shade collapses and nothing
        // visible happens. unlockAndRun is a no-op when already unlocked.
        if (isLocked) {
            unlockAndRun { startMainActivity() }
        } else {
            startMainActivity()
        }
    }

    private fun startMainActivity() {
        val launch = Intent(applicationContext, MainActivity::class.java).apply {
            action = Intent.ACTION_MAIN
            addCategory(Intent.CATEGORY_LAUNCHER)
            // Bring an existing Activity to the front instead of stacking — the
            // user expects to land where they left off, not on a fresh nav stack.
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            // API 34+: startActivityAndCollapse(Intent) is no longer permitted
            // — must use a PendingIntent so the launch passes through the
            // activity-starts-from-background allowlist properly.
            val pi = PendingIntent.getActivity(
                this,
                /* requestCode = */ 0,
                launch,
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )
            startActivityAndCollapse(pi)
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(launch)
        }
    }

    /**
     * Translate [ConnectionStatus] into the small set of tile-renderable
     * properties Android exposes. Idempotent — the StateFlow collector calls
     * it for every emission.
     */
    private fun renderTile(status: ConnectionStatus) {
        val tile = qsTile ?: return
        val (state, subtitle) = when (status.state) {
            ConnectionState.DISCONNECTED -> Tile.STATE_INACTIVE to getString(R.string.quick_tile_subtitle_disconnected)
            ConnectionState.CONNECTING,
            ConnectionState.HANDSHAKING,
            ConnectionState.RECONNECTING -> Tile.STATE_ACTIVE to getString(R.string.quick_tile_subtitle_connecting)
            ConnectionState.CONNECTED -> Tile.STATE_ACTIVE to getString(R.string.quick_tile_subtitle_connected)
        }
        tile.state = state
        tile.label = getString(R.string.quick_tile_label)
        tile.icon = Icon.createWithResource(this, R.drawable.ic_vertex_tile)
        // Subtitle is API 29+ only; older devices fall back to the label
        // alone — the platform's own decision about how much detail tiles
        // get. We don't try to compensate.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            tile.subtitle = subtitle
        }
        runCatching { tile.updateTile() }
            .onFailure { Timber.tag(TAG).w(it, "updateTile failed") }
    }

    override fun onTileAdded() {
        super.onTileAdded()
        Timber.tag(TAG).i("tile added")
    }

    override fun onTileRemoved() {
        super.onTileRemoved()
        Timber.tag(TAG).i("tile removed")
    }

    private companion object {
        const val TAG = "vtx-qs"
    }
}
