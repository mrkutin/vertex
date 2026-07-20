package ru.vertices.android.ui.sheets

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.hilt.navigation.compose.hiltViewModel
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import ru.vertices.android.core.util.NodeLabels
import ru.vertices.android.ui.components.VxDivider
import ru.vertices.android.ui.components.VxRow
import ru.vertices.android.ui.components.VxSection
import ru.vertices.android.ui.connect.VertexHero
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VxHeroStatusStyle
import ru.vertices.android.ui.theme.VxSpace
import ru.vertices.android.ui.theme.VxStatValueStyle
import ru.vertices.android.viewmodel.ConnectViewModel
import ru.vertices.android.viewmodel.statusColor
import ru.vertices.android.viewmodel.statusText

/**
 * Modal bottom sheet that mirrors `StatsSheet.swift`. Shows a smaller hero,
 * status, the assigned IP, current vertex/edge, connect time, and the
 * cumulative traffic counters (sent/received + packet counts).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun StatsSheet(
    onDismiss: () -> Unit,
    viewModel: ConnectViewModel = hiltViewModel(),
) {
    val tokens = LocalVertexColors.current
    val ui by viewModel.state.collectAsState()
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = false)

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState,
        containerColor = tokens.bgCanvas,
        scrimColor = tokens.bgCanvas.copy(alpha = 0.5f),
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = VxSpace.s5, vertical = VxSpace.s4),
            verticalArrangement = Arrangement.spacedBy(VxSpace.s5),
        ) {
            Text(
                text = "Connection",
                style = VxHeroStatusStyle.copy(fontSize = 22.sp),
                color = tokens.textPrimary,
                modifier = Modifier.fillMaxWidth(),
                textAlign = TextAlign.Center,
            )

            // Mini hero + status caption.
            Column(
                modifier = Modifier.fillMaxWidth(),
                horizontalAlignment = androidx.compose.ui.Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(VxSpace.s3),
            ) {
                VertexHero(
                    state = ui.status.state,
                    uploadRateBps = ui.uploadBps,
                    downloadRateBps = ui.downloadBps,
                    contentSize = 96,
                    haloPadding = 12,
                )
                Text(
                    text = ui.status.state.statusText(),
                    style = VxHeroStatusStyle.copy(fontSize = 18.sp),
                    color = ui.status.state.statusColor(
                        connected = tokens.stateConnected,
                        transitioning = tokens.stateTransitioning,
                        dormant = tokens.textPrimary,
                    ),
                )
            }

            VxSection(header = "Vertex") {
                ui.status.assignedIp?.takeIf { it.isNotBlank() }?.let {
                    VxRow {
                        Text("Assigned IP", color = tokens.textSecondary, modifier = Modifier.weight(1f))
                        Text(it, style = VxStatValueStyle, color = tokens.textPrimary)
                    }
                    VxDivider()
                }
                ui.status.currentBroker?.takeIf { it.isNotBlank() }?.let { url ->
                    VxRow {
                        Text("Vertex", color = tokens.textSecondary, modifier = Modifier.weight(1f))
                        Text(NodeLabels.host(url) ?: url, color = tokens.textPrimary)
                    }
                    VxDivider()
                }
                VxRow {
                    Text("Edge", color = tokens.textSecondary, modifier = Modifier.weight(1f))
                    Text(ui.selectedExit.uppercase(), color = tokens.textPrimary)
                }
                ui.status.connectedSinceEpochMs?.let {
                    VxDivider()
                    VxRow {
                        Text("Connected", color = tokens.textSecondary, modifier = Modifier.weight(1f))
                        Text(
                            text = TIME_FMT.format(Date(it)),
                            color = tokens.textPrimary,
                            style = VxStatValueStyle,
                        )
                    }
                }
            }

            VxSection(header = "Traffic") {
                VxRow {
                    Text("Sent", color = tokens.textSecondary, modifier = Modifier.weight(1f))
                    Text(formatBytes(ui.stats.bytesUp), style = VxStatValueStyle, color = tokens.textPrimary)
                }
                VxDivider()
                VxRow {
                    Text("Received", color = tokens.textSecondary, modifier = Modifier.weight(1f))
                    Text(formatBytes(ui.stats.bytesDown), style = VxStatValueStyle, color = tokens.textPrimary)
                }
                VxDivider()
                VxRow {
                    Text("Packets up", color = tokens.textSecondary, modifier = Modifier.weight(1f))
                    Text("${ui.stats.packetsUp}", style = VxStatValueStyle, color = tokens.textPrimary)
                }
                VxDivider()
                VxRow {
                    Text("Packets down", color = tokens.textSecondary, modifier = Modifier.weight(1f))
                    Text("${ui.stats.packetsDown}", style = VxStatValueStyle, color = tokens.textPrimary)
                }
            }

            Spacer(Modifier.height(VxSpace.s8))
        }
    }
}

private val TIME_FMT = SimpleDateFormat("HH:mm", Locale.getDefault())

private fun formatBytes(n: Long): String {
    val units = arrayOf("B", "KB", "MB", "GB", "TB")
    var v = n.toDouble()
    var u = 0
    while (v >= 1024 && u < units.size - 1) { v /= 1024; u++ }
    return "%.1f %s".format(v, units[u])
}

