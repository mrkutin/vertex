package ru.vertices.android.ui.settings

import android.content.Intent
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Share
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.hilt.navigation.compose.hiltViewModel
import ru.vertices.android.ui.components.VxDivider
import ru.vertices.android.ui.components.VxRow
import ru.vertices.android.ui.components.VxSection
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VxBodyStyle
import ru.vertices.android.ui.theme.VxCaptionMonoStyle
import ru.vertices.android.ui.theme.VxCaptionStyle
import ru.vertices.android.ui.theme.VxRadius
import ru.vertices.android.ui.theme.VxSpace
import ru.vertices.android.viewmodel.DiagnosticsViewModel
import ru.vertices.android.vpn.diag.BatterySnapshot
import ru.vertices.android.vpn.diag.MemorySnapshot

/**
 * Diagnostics screen.
 *
 * Surfaces the live process metrics (memory + battery) plus a tail of the
 * rolling file log so a user reporting "VPN drops every 5 minutes" can read
 * the last error in-app and tap Share to send the full bundle to support.
 *
 * Mirrors iOS `DiagnosticsView` — the same logical sections (memory, battery,
 * recent log lines, export). Android lacks MetricKit so the values come from
 * `Debug.getMemoryInfo` + the sticky `ACTION_BATTERY_CHANGED` broadcast,
 * sampled every few seconds by [DiagnosticsViewModel].
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DiagnosticsScreen(
    onBack: () -> Unit,
    vm: DiagnosticsViewModel = hiltViewModel(),
) {
    val tokens = LocalVertexColors.current
    val ui by vm.state.collectAsState()
    val share by vm.share.collectAsState()
    val ctx = LocalContext.current

    LaunchedEffect(share) {
        val ready = share as? DiagnosticsViewModel.ShareState.Ready ?: return@LaunchedEffect
        val send = Intent(Intent.ACTION_SEND).apply {
            type = "application/zip"
            putExtra(Intent.EXTRA_STREAM, ready.uri)
            putExtra(Intent.EXTRA_SUBJECT, "Vertex Android — Diagnostics")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        val chooser = Intent.createChooser(send, "Share diagnostics").apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        try {
            ContextCompat.startActivity(ctx, chooser, null)
        } catch (e: android.content.ActivityNotFoundException) {
            // No app accepts ACTION_SEND with application/zip on this device
            // (e.g. minimal AOSP image). Tell the VM so the screen surfaces
            // a Failed banner instead of swallowing silently.
            vm.shareFailed(e.message ?: "no app accepts shared diagnostics")
            return@LaunchedEffect
        }
        vm.consumeShare()
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Diagnostics") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = Color.Transparent,
                    titleContentColor = tokens.textPrimary,
                    navigationIconContentColor = tokens.textPrimary,
                ),
            )
        },
        containerColor = tokens.bgCanvas,
    ) { pad ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(pad)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = VxSpace.s5, vertical = VxSpace.s4),
            verticalArrangement = Arrangement.spacedBy(VxSpace.s8),
        ) {
            MemorySection(ui.memory)
            BatterySection(ui.battery)
            LogTailSection(ui.logTail)
            ExportSection(share, onExport = vm::exportZip, onDismissError = vm::consumeShare)
            Spacer(Modifier.height(VxSpace.s8))
        }
    }
}

@Composable
private fun MemorySection(mem: MemorySnapshot?) {
    val tokens = LocalVertexColors.current
    VxSection(
        header = "Memory",
        footer = "Process-level Java/native heap and PSS. Updated every few seconds while the screen is open.",
    ) {
        if (mem == null) {
            VxRow {
                Text("Sampling…", color = tokens.textSecondary, modifier = Modifier.weight(1f))
            }
            return@VxSection
        }
        Stat("Java heap", "${mem.javaHeapUsedBytes.toMb()} / ${mem.javaHeapMaxBytes.toMb()} MB")
        VxDivider()
        Stat("Native heap", "${mem.nativeHeapUsedBytes.toMb()} MB")
        VxDivider()
        Stat("Total PSS", "${(mem.totalPssKb / 1024.0).format1()} MB")
        VxDivider()
        Stat("Private dirty", "${(mem.privateDirtyKb / 1024.0).format1()} MB")
        if (mem.systemLowMemory) {
            VxDivider()
            VxRow {
                Text("System low memory", color = tokens.stateError, modifier = Modifier.weight(1f))
                Text("yes", color = tokens.stateError)
            }
        }
    }
}

@Composable
private fun BatterySection(bat: BatterySnapshot?) {
    val tokens = LocalVertexColors.current
    VxSection(header = "Battery") {
        if (bat == null) {
            VxRow {
                Text("Sampling…", color = tokens.textSecondary, modifier = Modifier.weight(1f))
            }
            return@VxSection
        }
        Stat("Level", if (bat.levelPercent < 0) "—" else "${bat.levelPercent}%")
        VxDivider()
        Stat("Charging", if (bat.charging) "yes" else "no")
        VxDivider()
        Stat("Plugged", bat.plugged.name.lowercase())
    }
}

@Composable
private fun LogTailSection(tail: String) {
    val tokens = LocalVertexColors.current
    VxSection(
        header = "Recent log",
        footer = "Tail of the rolling file log. The full history is included in the diagnostics zip below.",
    ) {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = VxSpace.s4, vertical = VxSpace.s3)
                .heightIn(min = 80.dp)
                .clip(RoundedCornerShape(VxRadius.md))
                .background(tokens.bgSurfaceMuted)
                .padding(VxSpace.s3),
        ) {
            Text(
                text = tail.ifEmpty { "No entries yet — keep the app running for a moment." },
                style = VxCaptionMonoStyle,
                color = tokens.textSecondary,
            )
        }
    }
}

@Composable
private fun ExportSection(
    share: DiagnosticsViewModel.ShareState,
    onExport: () -> Unit,
    onDismissError: () -> Unit,
) {
    val tokens = LocalVertexColors.current
    VxSection(
        header = "Share with support",
        footer = "Builds a zip with summary.txt, the rolling log files, and your identity public key. Broker passwords are never included.",
    ) {
        val (label, enabled) = when (share) {
            DiagnosticsViewModel.ShareState.Building -> "Building zip…" to false
            else -> "Export diagnostics zip" to true
        }
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .clickable(enabled = enabled) { onExport() }
                .padding(horizontal = VxSpace.s4, vertical = VxSpace.s3),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(VxSpace.s2),
        ) {
            Icon(
                Icons.Default.Share,
                contentDescription = null,
                tint = if (enabled) tokens.accentPrimary else tokens.textTertiary,
                modifier = Modifier.size(20.dp),
            )
            Text(
                label,
                color = if (enabled) tokens.accentPrimary else tokens.textTertiary,
                style = VxBodyStyle,
            )
        }
        if (share is DiagnosticsViewModel.ShareState.Failed) {
            VxDivider()
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable { onDismissError() }
                    .padding(horizontal = VxSpace.s4, vertical = VxSpace.s3),
                horizontalArrangement = Arrangement.spacedBy(VxSpace.s2),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    share.message,
                    color = tokens.stateError,
                    style = VxCaptionStyle,
                    modifier = Modifier.weight(1f),
                )
                Text("Dismiss", color = tokens.textTertiary, style = VxCaptionStyle)
            }
        }
    }
}

@Composable
private fun Stat(label: String, value: String) {
    val tokens = LocalVertexColors.current
    VxRow {
        Text(label, color = tokens.textSecondary, modifier = Modifier.weight(1f))
        Text(value, color = tokens.textPrimary, style = VxCaptionMonoStyle)
    }
}

private fun Long.toMb(): String = (this / 1_048_576.0).format1()
private fun Double.format1(): String = "%.1f".format(this)
