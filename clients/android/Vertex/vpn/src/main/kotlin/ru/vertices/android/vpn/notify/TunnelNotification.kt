package ru.vertices.android.vpn.notify

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import androidx.core.app.NotificationCompat
import ru.vertices.android.core.ipc.ConnectionState
import ru.vertices.android.core.ipc.ConnectionStatus

/**
 * Foreground service notification. Required: API 26+ kills any background
 * service after ~5 minutes; VpnService is no exception.
 *
 * The ACTION_DISCONNECT intent is consumed by [VertexVpnService.onStartCommand].
 */
internal object TunnelNotification {

    const val CHANNEL_ID = "vpn_channel"
    const val NOTIFICATION_ID = 1

    fun ensureChannel(context: Context) {
        val mgr = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (mgr.getNotificationChannel(CHANNEL_ID) == null) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "Vertex VPN",
                NotificationManager.IMPORTANCE_LOW,
            ).apply {
                description = "VPN connection status"
                setShowBadge(false)
                enableLights(false)
                enableVibration(false)
                setSound(null, null)
            }
            mgr.createNotificationChannel(channel)
        }
    }

    fun build(context: Context, status: ConnectionStatus): Notification {
        ensureChannel(context)

        val title = "Vertex VPN"
        val content = when (status.state) {
            ConnectionState.CONNECTED -> {
                val exit = status.currentExit ?: "?"
                val broker = status.currentBroker ?: "?"
                "Connected via $exit ($broker)"
            }
            ConnectionState.CONNECTING,
            ConnectionState.HANDSHAKING -> "Connecting…"
            ConnectionState.RECONNECTING -> "Reconnecting…"
            ConnectionState.DISCONNECTED -> "Disconnected"
        }

        val tapIntent = context.packageManager
            .getLaunchIntentForPackage(context.packageName)
            ?.apply { flags = Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP }

        val tap = tapIntent?.let {
            PendingIntent.getActivity(
                context, 0, it,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
        }

        val disconnectIntent = Intent(context, ru.vertices.android.vpn.VertexVpnService::class.java).apply {
            action = ru.vertices.android.vpn.VertexVpnService.ACTION_DISCONNECT
        }
        val disconnectPi = PendingIntent.getService(
            context, 1, disconnectIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

        return NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(ru.vertices.android.vpn.R.drawable.ic_vtx_notif)
            .setContentTitle(title)
            .setContentText(content)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setContentIntent(tap)
            .addAction(0, "Disconnect", disconnectPi)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .build()
    }
}
