package ru.vertices.android.ui.pickers

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import ru.vertices.android.core.ipc.ConnectionState
import ru.vertices.android.core.util.NodeLabels
import ru.vertices.android.ui.components.VxDivider
import ru.vertices.android.ui.components.VxSection
import ru.vertices.android.ui.components.VxSelectionGlyph
import ru.vertices.android.ui.connect.VxAsteriskGlyph
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VxBodyEmphasizedStyle
import ru.vertices.android.ui.theme.VxBodyStyle
import ru.vertices.android.ui.theme.VxCaptionMonoStyle
import ru.vertices.android.ui.theme.VxSpace
import ru.vertices.android.ui.util.Haptics
import ru.vertices.android.viewmodel.ConnectViewModel

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun BrokerListScreen(
    onBack: () -> Unit,
    viewModel: ConnectViewModel = hiltViewModel(),
) {
    val tokens = LocalVertexColors.current
    val haptic = LocalHapticFeedback.current
    val ui by viewModel.state.collectAsState()
    val presented = ui.presentedBrokers
    val selected = ui.selectedBroker

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Vertices") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
                actions = {
                    IconButton(onClick = { viewModel.resolveSrv() }) {
                        if (ui.isResolving) {
                            CircularProgressIndicator(
                                color = tokens.accentPrimary,
                                strokeWidth = 2.dp,
                                modifier = Modifier.padding(8.dp),
                            )
                        } else {
                            Icon(Icons.Default.Refresh, contentDescription = "Refresh", tint = tokens.accentPrimary)
                        }
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
            VxSection(
                header = "Available Vertices",
                footer = "Vertices are tried in SRV-priority order. The selected vertex leads; the rest serve as failover.",
            ) {
                presented.forEachIndexed { presentedIdx, item ->
                    if (presentedIdx > 0) VxDivider(leadingInset = 56.dp)
                    if (item == "auto") {
                        AutoBrokerRow(
                            isSelected = selected == "auto",
                            currentBroker = ui.status.currentBroker,
                            isConnected = ui.status.state == ConnectionState.CONNECTED,
                            onTap = {
                                viewModel.selectBroker("auto")
                                Haptics.selection(haptic)
                                onBack()
                            },
                        )
                    } else {
                        BrokerRow(
                            url = item,
                            unique = NodeLabels.uniqueHosts(ui.availableBrokers),
                            isSelected = item == selected,
                            onTap = {
                                viewModel.selectBroker(item)
                                Haptics.selection(haptic)
                                onBack()
                            },
                        )
                    }
                }
            }
        }
    }
}

/**
 * "Auto" pseudo-row — TunnelEngine probes TCP-connect RTT to every
 * broker URL and connects to the fastest. Subtitle shows the live
 * resolved broker host while connected so users see what auto picked.
 *
 * `currentBroker` from `ConnectionStatus` is already a bare host
 * (`MqttTransport.currentBroker.host`) — no `NodeLabels.host()` call
 * needed.
 */
@Composable
private fun AutoBrokerRow(
    isSelected: Boolean,
    currentBroker: String?,
    isConnected: Boolean,
    onTap: () -> Unit,
) {
    val tokens = LocalVertexColors.current
    val subtitle: String = if (isConnected && !currentBroker.isNullOrBlank()) {
        val short = NodeLabels.vertexLabel(currentBroker, 0).shortName
        "Now: $short"
    } else {
        "Lowest TCP RTT"
    }
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 56.dp)
            .clickable(onClick = onTap)
            .padding(horizontal = VxSpace.s4, vertical = VxSpace.s3),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(VxSpace.s3),
    ) {
        Box(modifier = Modifier.width(28.dp)) {
            VxAsteriskGlyph(sizeDp = 22)
        }
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(
                text = "Auto",
                style = if (isSelected) VxBodyEmphasizedStyle else VxBodyStyle,
                color = tokens.textPrimary,
            )
            Text(text = subtitle, style = VxCaptionMonoStyle, color = tokens.textTertiary)
        }
        if (isSelected) {
            Spacer(Modifier.width(8.dp))
            VxSelectionGlyph(sizeDp = 18)
        }
    }
}

@Composable
private fun BrokerRow(
    url: String,
    unique: List<String>,
    isSelected: Boolean,
    onTap: () -> Unit,
) {
    val tokens = LocalVertexColors.current
    val host = NodeLabels.host(url) ?: url
    val idx = unique.indexOf(host).coerceAtLeast(0)
    val code = NodeLabels.vertexLabel(host, idx).code
    val schemeDisplay = run {
        val scheme = NodeLabels.scheme(url)?.uppercase() ?: ""
        val port = NodeLabels.port(url)
        if (scheme.isNotEmpty() && port != null) "$scheme · $port" else scheme
    }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 56.dp)
            .clickable(onClick = onTap)
            .padding(horizontal = VxSpace.s4, vertical = VxSpace.s3),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(VxSpace.s3),
    ) {
        Box(modifier = Modifier.width(28.dp)) {
            VxAsteriskGlyph(sizeDp = 22)
        }
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(
                text = host,
                style = if (isSelected) VxBodyEmphasizedStyle else VxBodyStyle,
                color = tokens.textPrimary,
            )
            Text(text = schemeDisplay, style = VxCaptionMonoStyle, color = tokens.textTertiary)
        }
        Text(text = code, style = VxCaptionMonoStyle, color = tokens.textTertiary)
        if (isSelected) {
            Spacer(Modifier.width(8.dp))
            VxSelectionGlyph(sizeDp = 18)
        }
    }
}
