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
import ru.vertices.android.core.util.NodeLabels
import ru.vertices.android.core.ipc.ConnectionState
import ru.vertices.android.ui.components.VxDivider
import ru.vertices.android.ui.components.VxSection
import ru.vertices.android.ui.components.VxSelectionGlyph
import ru.vertices.android.ui.connect.VxAsteriskGlyph
import ru.vertices.android.ui.connect.VxEdgeGlyph
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VxBodyEmphasizedStyle
import ru.vertices.android.ui.theme.VxBodyStyle
import ru.vertices.android.ui.theme.VxCaptionMonoStyle
import ru.vertices.android.ui.theme.VxSpace
import ru.vertices.android.ui.util.Haptics
import ru.vertices.android.viewmodel.ConnectViewModel

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ExitListScreen(
    onBack: () -> Unit,
    viewModel: ConnectViewModel = hiltViewModel(),
) {
    val tokens = LocalVertexColors.current
    val haptic = LocalHapticFeedback.current
    val ui by viewModel.state.collectAsState()
    val presented = ui.presentedExits
    val selected = ui.selectedExit

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Edges") },
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
                header = "Available Edges",
                footer = "The edge is the network shoulder where your traffic exits to the public internet.",
            ) {
                presented.forEachIndexed { presentedIdx, exit ->
                    if (presentedIdx > 0) VxDivider(leadingInset = 56.dp)
                    if (exit == "auto") {
                        AutoRow(
                            isSelected = selected == "auto",
                            currentExit = ui.status.currentExit,
                            isConnected = ui.status.state == ConnectionState.CONNECTED,
                            displayOverride = ui.status.currentExit?.let { ui.exitDisplayNames[it] },
                            onTap = {
                                viewModel.selectExit("auto")
                                Haptics.selection(haptic)
                                onBack()
                            },
                        )
                    } else {
                        // Subscript index uses position in the real SRV
                        // list, NOT in `presented` — keeps E₀/E₁ stable
                        // across UI changes.
                        val realIdx = ui.availableExits.indexOf(exit).coerceAtLeast(0)
                        ExitRow(
                            exit = exit,
                            index = realIdx,
                            isSelected = exit == selected,
                            displayOverride = ui.exitDisplayNames[exit],
                            onTap = {
                                viewModel.selectExit(exit)
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
 * "Auto" pseudo-row — the extension resolves this to a concrete edge
 * after MQTT connect using broker-RTT and load score. Subtitle shows
 * the live resolved edge while connected so users see what auto picked.
 */
@Composable
private fun AutoRow(
    isSelected: Boolean,
    currentExit: String?,
    isConnected: Boolean,
    displayOverride: String?,
    onTap: () -> Unit,
) {
    val tokens = LocalVertexColors.current
    val subtitle: String = if (isConnected && !currentExit.isNullOrBlank() && currentExit != "auto") {
        val display = displayOverride ?: currentExit.uppercase()
        "Now: $display"
    } else {
        "Best edge per latency"
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
private fun ExitRow(
    exit: String,
    index: Int,
    isSelected: Boolean,
    displayOverride: String?,
    onTap: () -> Unit,
) {
    val tokens = LocalVertexColors.current
    val label = NodeLabels.edgeLabel(exit, index, displayOverride)
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
            VxEdgeGlyph(sizeDp = 22)
        }
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(
                text = label.display,
                style = if (isSelected) VxBodyEmphasizedStyle else VxBodyStyle,
                color = tokens.textPrimary,
            )
            Text(text = label.code, style = VxCaptionMonoStyle, color = tokens.textTertiary)
        }
        if (isSelected) {
            Spacer(Modifier.width(8.dp))
            VxSelectionGlyph(sizeDp = 18)
        }
    }
}
