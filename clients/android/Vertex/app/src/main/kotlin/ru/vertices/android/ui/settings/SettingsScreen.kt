package ru.vertices.android.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.VpnKey
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material.icons.filled.Waves
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import ru.vertices.android.core.util.NodeLabels
import ru.vertices.android.ui.components.VxDivider
import ru.vertices.android.ui.components.VxProtocolBadge
import ru.vertices.android.ui.components.VxRow
import ru.vertices.android.ui.components.VxSection
import ru.vertices.android.ui.connect.VxAsteriskGlyph
import ru.vertices.android.ui.connect.VxEdgeGlyph
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VxBodyStyle
import ru.vertices.android.ui.theme.VxCaptionMonoStyle
import ru.vertices.android.ui.theme.VxCaptionStyle
import ru.vertices.android.ui.theme.VxSpace
import ru.vertices.android.viewmodel.ConnectViewModel
import ru.vertices.android.viewmodel.SettingsViewModel

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(
    onBack: () -> Unit,
    onIdentityClick: () -> Unit,
    onDiagnosticsClick: () -> Unit,
    onAboutClick: () -> Unit,
    settingsViewModel: SettingsViewModel = hiltViewModel(),
    connectViewModel: ConnectViewModel = hiltViewModel(),
) {
    val tokens = LocalVertexColors.current
    val ui by settingsViewModel.ui.collectAsState()
    val connectUi by connectViewModel.state.collectAsState()

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Settings") },
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
            IdentitySection(ui, settingsViewModel)
            DiscoverySection(ui, settingsViewModel, connectViewModel)
            RoutingSection(ui, settingsViewModel)
            ActiveConfigSection(connectUi.availableBrokers, connectUi.availableExits, connectUi.exitDisplayNames)
            NavSection(
                onIdentityClick = onIdentityClick,
                onDiagnosticsClick = onDiagnosticsClick,
                onAboutClick = onAboutClick,
            )
            Spacer(Modifier.size(VxSpace.s8))
        }
    }
}

// MARK: - Identity

@Composable
private fun IdentitySection(ui: ru.vertices.android.viewmodel.SettingsUi, vm: SettingsViewModel) {
    val tokens = LocalVertexColors.current
    var showPassword by remember { mutableStateOf(false) }

    VxSection(header = "Identity") {
        VxRow {
            Text("Client name", color = tokens.textSecondary, modifier = Modifier.weight(1f))
            InlineTextField(
                value = ui.clientName,
                onChange = vm::setClientName,
                placeholder = "e.g. android",
                keyboardType = KeyboardType.Ascii,
            )
        }
        VxDivider()
        VxRow {
            Text("Password", color = tokens.textSecondary, modifier = Modifier.weight(1f))
            InlineTextField(
                value = ui.password,
                onChange = vm::setPassword,
                placeholder = "password",
                keyboardType = KeyboardType.Password,
                visualTransformation = if (showPassword) VisualTransformation.None else PasswordVisualTransformation(),
                modifier = Modifier.width(160.dp),
            )
            IconButton(
                onClick = { showPassword = !showPassword },
                modifier = Modifier.size(28.dp),
            ) {
                Icon(
                    imageVector = if (showPassword) Icons.Default.VisibilityOff else Icons.Default.Visibility,
                    contentDescription = if (showPassword) "Hide password" else "Show password",
                    tint = tokens.textTertiary,
                )
            }
        }
    }
}

// MARK: - Discovery

@Composable
private fun DiscoverySection(
    ui: ru.vertices.android.viewmodel.SettingsUi,
    vm: SettingsViewModel,
    connectVm: ConnectViewModel,
) {
    val tokens = LocalVertexColors.current
    VxSection(
        header = "Discovery",
        footer = "Vertices and edges are resolved via DNS SRV records from this domain.",
    ) {
        VxRow {
            Text("Discovery domain", color = tokens.textSecondary, modifier = Modifier.weight(1f))
            InlineTextField(
                value = ui.domain,
                onChange = vm::setDomain,
                placeholder = "vertices.ru",
                keyboardType = KeyboardType.Uri,
            )
        }
        VxDivider()
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .clickable { connectVm.resolveSrv() }
                .padding(horizontal = VxSpace.s4, vertical = VxSpace.s3),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(VxSpace.s2),
        ) {
            Icon(Icons.Default.Refresh, contentDescription = null, tint = tokens.accentPrimary, modifier = Modifier.size(20.dp))
            Text("Refresh", color = tokens.accentPrimary, style = VxBodyStyle)
        }
    }
}

// MARK: - Routing

@Composable
private fun RoutingSection(ui: ru.vertices.android.viewmodel.SettingsUi, vm: SettingsViewModel) {
    val tokens = LocalVertexColors.current
    val info by vm.ruNetsInfo.collectAsState()
    val refresh by vm.ruNetsRefresh.collectAsState()
    VxSection(
        header = "Routing",
        footer = "RU subnets bypass the tunnel and go through your provider directly. Takes effect on next connect. Refresh fetches the latest list from ipdeny.com.",
    ) {
        VxRow {
            Text("Split tunnel (RU direct)", color = tokens.textPrimary, modifier = Modifier.weight(1f))
            Switch(
                checked = ui.splitTunnel,
                onCheckedChange = vm::setSplitTunnel,
                colors = SwitchDefaults.colors(
                    checkedThumbColor = Color.White,
                    checkedTrackColor = tokens.accentPrimary,
                    uncheckedThumbColor = tokens.textSecondary,
                    uncheckedTrackColor = tokens.bgSurfaceMuted,
                ),
            )
        }
        VxDivider()
        VxRow {
            Column(modifier = Modifier.weight(1f)) {
                Text("RU networks", color = tokens.textPrimary)
                Text(
                    text = ruNetsCaption(info, refresh),
                    color = tokens.textTertiary,
                    style = VxCaptionStyle,
                )
            }
        }
        VxDivider()
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .clickable(enabled = refresh != ru.vertices.android.repository.RuNetsRepository.RefreshState.InProgress) {
                    vm.refreshRuNets()
                }
                .padding(horizontal = VxSpace.s4, vertical = VxSpace.s3),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(VxSpace.s2),
        ) {
            Icon(
                Icons.Default.Refresh,
                contentDescription = null,
                tint = if (refresh == ru.vertices.android.repository.RuNetsRepository.RefreshState.InProgress) tokens.textTertiary else tokens.accentPrimary,
                modifier = Modifier.size(20.dp),
            )
            Text(
                text = if (refresh == ru.vertices.android.repository.RuNetsRepository.RefreshState.InProgress) "Refreshing…" else "Refresh from ipdeny.com",
                color = if (refresh == ru.vertices.android.repository.RuNetsRepository.RefreshState.InProgress) tokens.textTertiary else tokens.accentPrimary,
                style = VxBodyStyle,
            )
        }
    }
}

private fun ruNetsCaption(
    info: ru.vertices.android.repository.RuNetsRepository.Info,
    refresh: ru.vertices.android.repository.RuNetsRepository.RefreshState,
): String {
    val source = when (info.source) {
        ru.vertices.android.repository.RuNetsRepository.Source.BUNDLED -> "bundled with app"
        ru.vertices.android.repository.RuNetsRepository.Source.UPDATED -> "updated ${formatRelative(info.updatedAtEpochMs)}"
    }
    val base = "${info.lineCount} CIDRs · $source"
    return when (refresh) {
        is ru.vertices.android.repository.RuNetsRepository.RefreshState.Failed -> "$base · last refresh failed: ${refresh.message}"
        else -> base
    }
}

private fun formatRelative(epochMs: Long?): String {
    if (epochMs == null) return "—"
    val deltaMs = System.currentTimeMillis() - epochMs
    val mins = deltaMs / 60_000L
    val hours = mins / 60L
    val days = hours / 24L
    return when {
        mins < 1 -> "just now"
        mins < 60 -> "${mins}m ago"
        hours < 24 -> "${hours}h ago"
        days < 30 -> "${days}d ago"
        else -> "${days / 30}mo ago"
    }
}

// MARK: - Active configuration (read-only inventory)

@Composable
private fun ActiveConfigSection(brokers: List<String>, exits: List<String>, exitDisplayNames: Map<String, String>) {
    val tokens = LocalVertexColors.current
    val unique = NodeLabels.uniqueHosts(brokers)
    VxSection(header = "Active Configuration") {
        if (unique.isEmpty() && exits.isEmpty()) {
            VxRow {
                Text("Resolving discovery…", style = VxBodyStyle, color = tokens.textSecondary, modifier = Modifier.weight(1f))
            }
        } else {
            if (unique.isNotEmpty()) {
                MiniHeader("Vertices")
                unique.forEachIndexed { idx, host ->
                    if (idx > 0) VxDivider(leadingInset = 56.dp)
                    VertexConfigRow(host = host, index = idx, brokers = brokers)
                }
            }
            if (unique.isNotEmpty() && exits.isNotEmpty()) {
                Spacer(Modifier.size(VxSpace.s2))
                VxDivider(leadingInset = 0.dp)
            }
            if (exits.isNotEmpty()) {
                MiniHeader("Edges")
                exits.forEachIndexed { idx, exit ->
                    if (idx > 0) VxDivider(leadingInset = 56.dp)
                    EdgeConfigRow(id = exit, index = idx, displayOverride = exitDisplayNames[exit])
                }
            }
        }
    }
}

@Composable
private fun MiniHeader(text: String) {
    val tokens = LocalVertexColors.current
    Text(
        text = text.uppercase(),
        style = VxCaptionStyle,
        color = tokens.textTertiary,
        modifier = Modifier
            .padding(start = VxSpace.s4, top = VxSpace.s3, bottom = VxSpace.s1),
    )
}

@Composable
private fun VertexConfigRow(host: String, index: Int, brokers: List<String>) {
    val tokens = LocalVertexColors.current
    val label = NodeLabels.vertexLabel(host, index)
    val short = host.split(".").firstOrNull() ?: host
    val schemes = NodeLabels.protocols(host, brokers)
    VxRow {
        Box(modifier = Modifier.width(28.dp)) {
            VxAsteriskGlyph(sizeDp = 18)
        }
        Text(label.code, color = tokens.textPrimary, style = VxBodyStyle)
        Text(
            text = short,
            style = VxCaptionMonoStyle,
            color = tokens.textTertiary,
            modifier = Modifier.weight(1f),
        )
        Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            schemes.forEachIndexed { i, scheme ->
                VxProtocolBadge(label = scheme, isPrimary = i == 0)
            }
        }
    }
}

@Composable
private fun EdgeConfigRow(id: String, index: Int, displayOverride: String?) {
    val tokens = LocalVertexColors.current
    val label = NodeLabels.edgeLabel(id, index, displayOverride)
    val city = label.display.split(",").firstOrNull()?.trim() ?: label.display
    VxRow {
        Box(modifier = Modifier.width(28.dp)) {
            VxEdgeGlyph(sizeDp = 18)
        }
        Text(label.code, color = tokens.textPrimary, style = VxBodyStyle)
        Text(city, style = VxCaptionMonoStyle, color = tokens.textTertiary, modifier = Modifier.weight(1f))
    }
}

// MARK: - Nav links

@Composable
private fun NavSection(
    onIdentityClick: () -> Unit,
    onDiagnosticsClick: () -> Unit,
    onAboutClick: () -> Unit,
) {
    VxSection {
        NavRow(icon = Icons.Default.VpnKey, title = "Identity Key", onClick = onIdentityClick)
        VxDivider()
        NavRow(icon = Icons.Default.Waves, title = "Diagnostics", onClick = onDiagnosticsClick)
        VxDivider()
        NavRow(icon = Icons.Default.Info, title = "About", onClick = onAboutClick)
    }
}

@Composable
private fun NavRow(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    title: String,
    onClick: () -> Unit,
) {
    val tokens = LocalVertexColors.current
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = VxSpace.s4, vertical = VxSpace.s3),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(VxSpace.s3),
    ) {
        Box(modifier = Modifier.width(28.dp)) {
            Icon(imageVector = icon, contentDescription = null, tint = tokens.accentPrimary)
        }
        Text(text = title, color = tokens.textPrimary, modifier = Modifier.weight(1f))
        Icon(
            imageVector = Icons.AutoMirrored.Filled.KeyboardArrowRight,
            contentDescription = null,
            tint = tokens.textTertiary,
        )
    }
}

// MARK: - Inline text field

@Composable
private fun InlineTextField(
    value: String,
    onChange: (String) -> Unit,
    placeholder: String,
    keyboardType: KeyboardType,
    visualTransformation: VisualTransformation = VisualTransformation.None,
    modifier: Modifier = Modifier,
) {
    val tokens = LocalVertexColors.current
    Box(modifier = modifier, contentAlignment = Alignment.CenterEnd) {
        if (value.isEmpty()) {
            Text(
                text = placeholder,
                color = tokens.textTertiary,
                style = LocalTextStyle.current,
                textAlign = TextAlign.End,
            )
        }
        BasicTextField(
            value = value,
            onValueChange = onChange,
            singleLine = true,
            keyboardOptions = KeyboardOptions(
                keyboardType = keyboardType,
                capitalization = KeyboardCapitalization.None,
            ),
            visualTransformation = visualTransformation,
            textStyle = LocalTextStyle.current.copy(
                color = tokens.textPrimary,
                textAlign = TextAlign.End,
            ),
            cursorBrush = SolidColor(tokens.accentPrimary),
        )
    }
}
