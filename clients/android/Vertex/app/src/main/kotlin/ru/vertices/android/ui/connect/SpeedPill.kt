package ru.vertices.android.ui.connect

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowDownward
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Timer
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VxCalloutStyle

/**
 * Compact capsule with current upload/download rate + tunnel ping —
 * sibling of [StatusPill], rendered just below it on ConnectScreen while
 * connected. Mirror of `SpeedPillView.swift`.
 *
 * Rates come from a 3s rolling average of TunnelStats samples on the
 * ViewModel side. Ping is a TCP-connect RTT to 1.1.1.1:443 measured
 * through the tunnel — `null` until first measurement.
 */
@Composable
fun SpeedPill(
    uploadBps: Double,
    downloadBps: Double,
    pingMs: Int?,
    modifier: Modifier = Modifier,
) {
    val tokens = LocalVertexColors.current
    Row(
        modifier = modifier
            .clip(CircleShape)
            .background(tokens.bgSurfaceElev)
            .border(0.5.dp, tokens.borderSubtle, CircleShape)
            .padding(PaddingValues(horizontal = 14.dp, vertical = 7.dp)),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Slot(
            icon = Icons.Default.ArrowUpward,
            iconTint = tokens.accentPrimary,
            text = formatRate(uploadBps),
            textColor = tokens.textPrimary,
        )
        Pip()
        Slot(
            icon = Icons.Default.ArrowDownward,
            iconTint = tokens.accentPrimary,
            text = formatRate(downloadBps),
            textColor = tokens.textPrimary,
        )
        Pip()
        Slot(
            icon = Icons.Default.Timer,
            iconTint = pingIconColor(pingMs, tokens.textTertiary, tokens.stateConnected,
                tokens.accentPrimary, tokens.stateTransitioning),
            text = formatPing(pingMs),
            textColor = tokens.textPrimary,
        )
    }
}

@Composable
private fun Slot(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    iconTint: Color,
    text: String,
    textColor: Color,
) {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        Icon(imageVector = icon, contentDescription = null, tint = iconTint, modifier = Modifier.size(12.dp))
        Text(text = text, style = VxCalloutStyle, color = textColor)
    }
}

@Composable
private fun Pip() {
    val tokens = LocalVertexColors.current
    Box(
        modifier = Modifier
            .width(0.5.dp)
            .height(12.dp)
            .background(tokens.borderSubtle),
    )
}

private fun pingIconColor(
    pingMs: Int?,
    dim: Color,
    good: Color,
    okay: Color,
    slow: Color,
): Color = when {
    pingMs == null    -> dim
    pingMs < 80       -> good
    pingMs < 200      -> okay
    else              -> slow
}

private fun formatPing(ms: Int?): String = ms?.let { "$it ms" } ?: "—"

/**
 * Bytes-per-second → bits-per-second formatted as Kbps/Mbps/Gbps (decimal SI
 * prefixes — convention for network speeds, matches Speedtest and ISP
 * advertised throughput). Returns "—" below 1 Kbps to avoid noise.
 */
private fun formatRate(bytesPerSec: Double): String {
    val bps = bytesPerSec * 8.0
    if (bps < 1_000) return "—"
    if (bps >= 1_000_000_000) return "%.1f Gbps".format(bps / 1_000_000_000)
    if (bps >= 1_000_000) return "%.1f Mbps".format(bps / 1_000_000)
    return "%.0f Kbps".format(bps / 1_000)
}

