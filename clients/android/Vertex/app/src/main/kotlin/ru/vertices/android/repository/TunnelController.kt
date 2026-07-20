package ru.vertices.android.repository

import android.content.Context
import android.content.Intent
import android.net.VpnService
import android.os.Build
import dagger.hilt.android.qualifiers.ApplicationContext
import ru.vertices.android.vpn.VertexVpnService
import timber.log.Timber
import javax.inject.Inject
import javax.inject.Singleton

/** Thin wrapper around `VpnService.prepare()` + start/stop intents. */
@Singleton
class TunnelController @Inject constructor(
    @ApplicationContext private val appContext: Context,
    private val settings: SettingsRepository,
) {

    /** Returns the prepare-intent if the user hasn't yet granted VPN permission. */
    fun preparePermission(activityContext: Context): Intent? = VpnService.prepare(activityContext)

    /**
     * Start the foreground service with the broker list ordered so the
     * currently selected one is tried first; the rest stay in their original
     * order as failover. Caller must have already received RESULT_OK from the
     * prepare intent.
     */
    suspend fun connect(availableBrokers: List<String>) {
        val snap = settings.snapshot()
        // Pin the user's explicit broker pick to the front of the list when
        // an explicit URL is chosen — TunnelEngine then skips the probe and
        // connects to it. When the pick is "auto", leave the SRV order as-is
        // so TunnelEngine can probe and reorder by RTT. Mirrors iOS
        // TunnelViewModel.connect() ordering logic.
        val ordered = availableBrokers.toMutableList()
        if (snap.selectedBroker != "auto") {
            val selectedIdx = ordered.indexOf(snap.selectedBroker)
            if (selectedIdx > 0) {
                val sel = ordered.removeAt(selectedIdx)
                ordered.add(0, sel)
            } else if (selectedIdx < 0) {
                // Saved URL no longer in the SRV result (DNS deploy renamed
                // or removed it). The pin can't be honoured, but the UI
                // hasn't reset to "auto" yet — log a breadcrumb so a "I
                // picked YC but it didn't connect to YC" report has
                // something to point at in logcat.
                Timber.tag(TAG).w("Saved broker ${snap.selectedBroker} not in SRV list — using SRV order")
            }
        }
        val csv = ordered.joinToString(",")
        // Password is intentionally NOT in extras — VertexVpnService reads it
        // from the encrypted prefs store directly. See PasswordStore in :vpn.
        val intent = Intent(appContext, VertexVpnService::class.java).apply {
            action = VertexVpnService.ACTION_CONNECT
            putExtra(VertexVpnService.EXTRA_BROKERS_CSV, csv)
            putExtra(VertexVpnService.EXTRA_NAME, snap.clientName)
            putExtra(VertexVpnService.EXTRA_EXIT, snap.selectedExit)
            putExtra(VertexVpnService.EXTRA_SELECTED_BROKER, snap.selectedBroker)
            putExtra(VertexVpnService.EXTRA_SPLIT_TUNNEL, snap.splitTunnel)
            snap.lastGoodExit?.takeIf { it.isNotBlank() }
                ?.let { putExtra(VertexVpnService.EXTRA_LAST_GOOD_EXIT, it) }
        }
        startService(intent)
    }

    fun disconnect() {
        val intent = Intent(appContext, VertexVpnService::class.java).apply {
            action = VertexVpnService.ACTION_DISCONNECT
        }
        appContext.startService(intent)
    }

    private fun startService(intent: Intent) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            appContext.startForegroundService(intent)
        } else {
            appContext.startService(intent)
        }
    }

    private companion object {
        const val TAG = "vtx-ctl"
    }
}
