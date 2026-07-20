package ru.vertices.android.ui.connect

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowDownward
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Warning
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
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.hilt.navigation.compose.hiltViewModel
import ru.vertices.android.BuildConfig
import ru.vertices.android.core.ipc.ConnectionState
import ru.vertices.android.ui.sheets.StatsSheet
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VxCaptionStyle
import ru.vertices.android.ui.theme.VxFootnoteStyle
import ru.vertices.android.ui.theme.VxHeroStatusStyle
import ru.vertices.android.ui.theme.VxRadius
import ru.vertices.android.ui.theme.VxSpace
import ru.vertices.android.ui.theme.VxStatValueStyle
import ru.vertices.android.ui.theme.VxWordmarkStyle
import ru.vertices.android.viewmodel.ConnectViewModel
import ru.vertices.android.viewmodel.statusColor
import ru.vertices.android.viewmodel.statusText

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ConnectScreen(
    onSettingsClick: () -> Unit,
    onRequestVpnPermission: () -> Boolean,
    onBrokerPickerClick: () -> Unit,
    onExitPickerClick: () -> Unit,
    viewModel: ConnectViewModel = hiltViewModel(),
) {
    val tokens = LocalVertexColors.current
    val ui by viewModel.state.collectAsState()
    val status = ui.status
    val state = status.state
    val isConnected = state == ConnectionState.CONNECTED
    val isTransitioning = state == ConnectionState.CONNECTING ||
        state == ConnectionState.HANDSHAKING ||
        state == ConnectionState.RECONNECTING

    var showStats by remember { mutableStateOf(false) }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("VERTEX", style = VxWordmarkStyle, color = tokens.textPrimary) },
                actions = {
                    IconButton(onClick = onSettingsClick) {
                        Icon(
                            imageVector = Icons.Default.Settings,
                            contentDescription = "Settings",
                            tint = tokens.accentPrimary,
                        )
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = Color.Transparent,
                    titleContentColor = tokens.textPrimary,
                    actionIconContentColor = tokens.accentPrimary,
                ),
            )
        },
        containerColor = tokens.bgCanvas,
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = VxSpace.s5, vertical = VxSpace.s4),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(VxSpace.s7),
        ) {
            HeroSection(
                state = state,
                uploadBps = ui.uploadBps,
                downloadBps = ui.downloadBps,
                pingMs = ui.pingMs,
                assignedIp = status.assignedIp,
                connectedSinceEpochMs = status.connectedSinceEpochMs,
            )

            ServerCard(
                selectedBroker = ui.selectedBroker,
                availableBrokers = ui.availableBrokers,
                selectedExit = ui.selectedExit,
                availableExits = ui.availableExits,
                exitDisplayNames = ui.exitDisplayNames,
                resolvedExit = ui.status.currentExit,
                resolvedBrokerHost = ui.status.currentBroker,
                isDisabled = isConnected || isTransitioning,
                onBrokerTap = onBrokerPickerClick,
                onExitTap = onExitPickerClick,
                modifier = Modifier.widthIn(max = 480.dp),
            )

            BigConnectButton(
                state = state,
                onClick = {
                    if (isConnected) {
                        viewModel.onDisconnectClicked()
                    } else if (onRequestVpnPermission()) {
                        viewModel.onConnectClicked()
                    }
                },
                modifier = Modifier.widthIn(max = 480.dp),
            )

            // Stats card — visible when connected. Tap opens the bottom sheet.
            if (isConnected) {
                StatRowCard(
                    bytesUp = ui.stats.bytesUp,
                    bytesDown = ui.stats.bytesDown,
                    onClick = { showStats = true },
                    modifier = Modifier.widthIn(max = 480.dp),
                )
            }

            // Error banner — shown when the last status carries an error and
            // we're not currently connecting.
            status.lastError
                ?.takeIf { it.isNotBlank() && state == ConnectionState.DISCONNECTED }
                ?.let {
                    ErrorBanner(it, modifier = Modifier.widthIn(max = 480.dp))
                }

            if (BuildConfig.DEBUG) {
                DebugBadge()
            }
            Spacer(Modifier.height(VxSpace.s4))
        }
    }

    if (showStats) {
        StatsSheet(
            onDismiss = { showStats = false },
            viewModel = viewModel,
        )
    }
}

@Composable
private fun HeroSection(
    state: ConnectionState,
    uploadBps: Double,
    downloadBps: Double,
    pingMs: Int?,
    assignedIp: String?,
    connectedSinceEpochMs: Long?,
) {
    val tokens = LocalVertexColors.current
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(VxSpace.s4),
    ) {
        VertexHero(
            state = state,
            uploadRateBps = uploadBps,
            downloadRateBps = downloadBps,
        )
        Text(
            text = state.statusText(),
            style = VxHeroStatusStyle,
            color = state.statusColor(
                connected = tokens.stateConnected,
                transitioning = tokens.stateTransitioning,
                dormant = tokens.textPrimary,
            ),
        )
        StatusPill(
            state = state,
            assignedIp = assignedIp,
            connectedSinceEpochMs = connectedSinceEpochMs,
        )
        AnimatedVisibility(
            visible = state == ConnectionState.CONNECTED,
            enter = fadeIn() + scaleIn(initialScale = 0.96f),
            exit = fadeOut() + scaleOut(targetScale = 0.96f),
        ) {
            SpeedPill(
                uploadBps = uploadBps,
                downloadBps = downloadBps,
                pingMs = pingMs,
            )
        }
    }
}

/**
 * Sent / Received card. Mirror of `StatRowView.swift` — two icon-circle stats
 * separated by a hairline. Tappable: opens the StatsSheet when clicked.
 */
@Composable
private fun StatRowCard(
    bytesUp: Long,
    bytesDown: Long,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val tokens = LocalVertexColors.current
    Row(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(VxRadius.lg))
            .background(tokens.bgSurface)
            .border(0.5.dp, tokens.borderSubtle, RoundedCornerShape(VxRadius.lg))
            .clickable(onClick = onClick)
            .padding(horizontal = VxSpace.s4, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(VxSpace.s6),
    ) {
        Stat(
            iconUp = true,
            label = "Sent",
            value = formatBytes(bytesUp),
            modifier = Modifier.weight(1f),
        )
        Box(
            modifier = Modifier
                .size(width = 0.5.dp, height = 28.dp)
                .background(tokens.borderSubtle),
        )
        Stat(
            iconUp = false,
            label = "Received",
            value = formatBytes(bytesDown),
            modifier = Modifier.weight(1f),
        )
    }
}

@Composable
private fun Stat(iconUp: Boolean, label: String, value: String, modifier: Modifier = Modifier) {
    val tokens = LocalVertexColors.current
    Row(
        modifier = modifier,
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(VxSpace.s2),
    ) {
        Box(
            modifier = Modifier
                .size(28.dp)
                .clip(CircleShape)
                .background(tokens.accentPrimaryMuted),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                imageVector = if (iconUp) Icons.Default.ArrowUpward else Icons.Default.ArrowDownward,
                contentDescription = null,
                tint = tokens.accentPrimary,
                modifier = Modifier.size(14.dp),
            )
        }
        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(text = label, style = VxCaptionStyle, color = tokens.textSecondary)
            Text(text = value, style = VxStatValueStyle, color = tokens.textPrimary)
        }
    }
}

@Composable
private fun ErrorBanner(error: String, modifier: Modifier = Modifier) {
    val tokens = LocalVertexColors.current
    Row(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(VxRadius.lg))
            .background(tokens.stateError.copy(alpha = 0.12f))
            .border(0.5.dp, tokens.stateError.copy(alpha = 0.35f), RoundedCornerShape(VxRadius.lg))
            .padding(VxSpace.s3),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        verticalAlignment = Alignment.Top,
    ) {
        Icon(
            imageVector = Icons.Default.Warning,
            contentDescription = null,
            tint = tokens.stateError,
            modifier = Modifier.size(20.dp),
        )
        Text(
            text = error,
            style = VxFootnoteStyle,
            color = tokens.textSecondary,
            modifier = Modifier.weight(1f),
        )
    }
}

@Composable
private fun DebugBadge() {
    val tokens = LocalVertexColors.current
    Box(
        modifier = Modifier
            .clip(CircleShape)
            .background(tokens.stateTransitioning.copy(alpha = 0.12f))
            .border(0.5.dp, tokens.stateTransitioning.copy(alpha = 0.35f), CircleShape)
            .padding(horizontal = 8.dp, vertical = 3.dp),
    ) {
        Text(
            text = "DEBUG BUILD",
            style = androidx.compose.ui.text.TextStyle(
                fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace,
                fontWeight = androidx.compose.ui.text.font.FontWeight.SemiBold,
                fontSize = 10.sp,
                letterSpacing = 1.2.sp,
            ),
            color = tokens.stateTransitioning,
            textAlign = TextAlign.Center,
        )
    }
}

private fun formatBytes(n: Long): String {
    val units = arrayOf("B", "KB", "MB", "GB", "TB")
    var v = n.toDouble()
    var u = 0
    while (v >= 1024 && u < units.size - 1) { v /= 1024; u++ }
    return "%.1f %s".format(v, units[u])
}

