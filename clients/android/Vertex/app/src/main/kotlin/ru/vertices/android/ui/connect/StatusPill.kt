package ru.vertices.android.ui.connect

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.BlurredEdgeTreatment
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.delay
import ru.vertices.android.core.ipc.ConnectionState
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VxCalloutStyle
import ru.vertices.android.ui.theme.VxMonoStyle

/**
 * Status pill — capsule with state-keyed dot + label. Mirror of `StatusPillView.swift`.
 *
 * Format:
 *   - DISCONNECTED  → "Not connected"
 *   - CONNECTING    → "Connecting…"
 *   - HANDSHAKING   → "Handshaking…"
 *   - RECONNECTING  → "Reconnecting…"
 *   - CONNECTED     → "<assignedIP>  ·  <uptime>"  (uptime is m:ss or h:mm:ss)
 *
 * On CONNECTED a 14dp blurred glow disc sits behind the 6dp solid dot
 * (mirrors iOS `Color.glowPrimary` blur).
 */
@Composable
fun StatusPill(
    state: ConnectionState,
    assignedIp: String? = null,
    connectedSinceEpochMs: Long? = null,
    modifier: Modifier = Modifier,
) {
    val tokens = LocalVertexColors.current

    var nowMs by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(state == ConnectionState.CONNECTED) {
        if (state == ConnectionState.CONNECTED) {
            while (true) {
                nowMs = System.currentTimeMillis()
                delay(1000)
            }
        }
    }

    val text = labelFor(state, assignedIp, connectedSinceEpochMs, nowMs)
    val dotColor = when (state) {
        ConnectionState.CONNECTED    -> tokens.stateConnected
        ConnectionState.CONNECTING,
        ConnectionState.HANDSHAKING,
        ConnectionState.RECONNECTING -> tokens.stateTransitioning
        ConnectionState.DISCONNECTED -> tokens.stateDormant
    }

    Row(
        modifier = modifier
            .clip(CircleShape)
            .background(tokens.bgSurfaceElev)
            .border(0.5.dp, tokens.borderSubtle, CircleShape)
            .padding(PaddingValues(horizontal = 14.dp, vertical = 7.dp)),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(modifier = Modifier.size(14.dp), contentAlignment = Alignment.Center) {
            if (state == ConnectionState.CONNECTED) {
                Box(
                    modifier = Modifier
                        .size(14.dp)
                        .blur(4.dp, BlurredEdgeTreatment(CircleShape))
                        .clip(CircleShape)
                        .background(tokens.glowPrimary),
                )
            }
            Box(
                modifier = Modifier
                    .size(6.dp)
                    .clip(CircleShape)
                    .background(dotColor),
            )
        }
        // Use mono for the IP+uptime payload (matches iOS .monospacedDigit()),
        // fall back to callout text for plain status strings.
        val style = if (state == ConnectionState.CONNECTED && assignedIp != null) VxMonoStyle else VxCalloutStyle
        Text(
            text = text,
            color = tokens.textPrimary,
            style = style,
            modifier = Modifier.padding(start = 8.dp),
        )
    }
}

private fun labelFor(
    state: ConnectionState,
    ip: String?,
    sinceEpochMs: Long?,
    nowMs: Long,
): String = when (state) {
    ConnectionState.CONNECTED -> {
        val uptime = sinceEpochMs?.let { uptimeString(it, nowMs) }
        when {
            ip != null && uptime != null -> "$ip  ·  $uptime"
            ip != null                   -> ip
            else                         -> "Connected"
        }
    }
    ConnectionState.CONNECTING   -> "Connecting…"
    ConnectionState.HANDSHAKING  -> "Handshaking…"
    ConnectionState.RECONNECTING -> "Reconnecting…"
    ConnectionState.DISCONNECTED -> "Not connected"
}

private fun uptimeString(startMs: Long, nowMs: Long): String {
    val total = ((nowMs - startMs) / 1000).coerceAtLeast(0)
    val h = total / 3600
    val m = (total % 3600) / 60
    val s = total % 60
    return if (h > 0) "%d:%02d:%02d".format(h, m, s) else "%d:%02d".format(m, s)
}
