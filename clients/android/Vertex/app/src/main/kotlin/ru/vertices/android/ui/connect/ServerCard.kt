package ru.vertices.android.ui.connect

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import ru.vertices.android.core.util.NodeLabels
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VxBodyEmphasizedStyle
import ru.vertices.android.ui.theme.VxCaptionMonoStyle
import ru.vertices.android.ui.theme.VxRadius
import ru.vertices.android.ui.theme.VxSpace
import ru.vertices.android.ui.theme.VxSubheadlineStyle

/**
 * Vertex / Edge selector card. Mirror of `ServerCard.swift`.
 *
 *   - Top row: VxAsteriskGlyph + "Vertex" subhead + broker host + V₀ · NAME accessory + chevron.
 *   - Hairline separator (56dp leading inset).
 *   - Bottom row: VxEdgeGlyph + "Edge" subhead + city display + E₀ · ID accessory + chevron.
 *
 * Tapping either row navigates to its picker.
 */
@Composable
fun ServerCard(
    selectedBroker: String,
    availableBrokers: List<String>,
    selectedExit: String,
    availableExits: List<String>,
    exitDisplayNames: Map<String, String> = emptyMap(),
    /**
     * When `selectedExit == "auto"` and the extension has resolved it to a
     * concrete edge (e.g. "sto"), surface the resolved ID so the card
     * reads "Auto · STO" instead of just "Auto". Null while disconnected
     * or while the extension is still resolving.
     */
    resolvedExit: String? = null,
    /**
     * When `selectedBroker == "auto"` and TunnelEngine has resolved it to
     * a concrete vertex (e.g. "mqtt-yc.vertices.ru"), surface the host so
     * the broker row reads "Auto · YC" instead of just "Auto". Already a
     * bare host — `MqttTransport.currentBroker.host` strips the scheme.
     * Null while disconnected.
     */
    resolvedBrokerHost: String? = null,
    isDisabled: Boolean = false,
    onBrokerTap: () -> Unit = {},
    onExitTap: () -> Unit = {},
    modifier: Modifier = Modifier,
) {
    val tokens = LocalVertexColors.current
    val isAutoBroker = selectedBroker == "auto"
    val brokerDisplay: String
    val vertexCode: String?
    if (isAutoBroker) {
        brokerDisplay = if (!resolvedBrokerHost.isNullOrBlank()) {
            val short = NodeLabels.vertexLabel(resolvedBrokerHost, 0).shortName
            "Auto · $short"
        } else {
            "Auto"
        }
        vertexCode = null
    } else {
        val host = NodeLabels.host(selectedBroker) ?: selectedBroker
        val unique = NodeLabels.uniqueHosts(availableBrokers)
        val brokerIdx = unique.indexOf(host).coerceAtLeast(0)
        brokerDisplay = host
        vertexCode = NodeLabels.vertexLabel(host, brokerIdx).code
    }

    val isAutoExit = selectedExit == "auto"
    val edgeDisplay: String
    val edgeCode: String?
    if (isAutoExit) {
        edgeDisplay = if (!resolvedExit.isNullOrBlank() && resolvedExit != "auto") {
            "Auto · ${resolvedExit.uppercase()}"
        } else {
            "Auto"
        }
        edgeCode = null
    } else {
        val edgeIdx = availableExits.indexOf(selectedExit).coerceAtLeast(0)
        val edge = NodeLabels.edgeLabel(selectedExit, edgeIdx, exitDisplayNames[selectedExit])
        edgeDisplay = edge.display
        edgeCode = edge.code
    }

    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(VxRadius.lg))
            .background(tokens.bgSurface)
            .border(0.5.dp, tokens.borderSubtle, RoundedCornerShape(VxRadius.lg))
            .alpha(if (isDisabled) 0.55f else 1f),
    ) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(modifier = Modifier.padding(start = VxSpace.s4).width(28.dp)) {
                VxAsteriskGlyph(sizeDp = 22)
            }
            Row(
                modifier = Modifier
                    .weight(1f)
                    .clickable(enabled = !isDisabled, onClick = onBrokerTap)
                    .padding(end = VxSpace.s4, top = 14.dp, bottom = 14.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Spacer(Modifier.width(14.dp))
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text("Vertex", style = VxSubheadlineStyle, color = tokens.textSecondary)
                    Text(
                        text = brokerDisplay.ifBlank { "—" },
                        style = VxBodyEmphasizedStyle,
                        color = tokens.textPrimary,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                if (vertexCode != null) {
                    Text(
                        text = vertexCode,
                        style = VxCaptionMonoStyle,
                        color = tokens.textTertiary,
                        modifier = Modifier.padding(end = 8.dp),
                    )
                }
                Icon(
                    imageVector = Icons.AutoMirrored.Filled.KeyboardArrowRight,
                    contentDescription = null,
                    tint = tokens.textTertiary,
                )
            }
        }
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .padding(start = 56.dp)
                .height(0.5.dp)
                .background(tokens.borderSubtle),
        )
        Row(
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(modifier = Modifier.padding(start = VxSpace.s4).width(28.dp)) {
                VxEdgeGlyph(sizeDp = 22)
            }
            Row(
                modifier = Modifier
                    .weight(1f)
                    .clickable(enabled = !isDisabled, onClick = onExitTap)
                    .padding(end = VxSpace.s4, top = 14.dp, bottom = 14.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Spacer(Modifier.width(14.dp))
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text("Edge", style = VxSubheadlineStyle, color = tokens.textSecondary)
                    Text(
                        text = edgeDisplay,
                        style = VxBodyEmphasizedStyle,
                        color = tokens.textPrimary,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                if (edgeCode != null) {
                    Text(
                        text = edgeCode,
                        style = VxCaptionMonoStyle,
                        color = tokens.textTertiary,
                        modifier = Modifier.padding(end = 8.dp),
                    )
                }
                Icon(
                    imageVector = Icons.AutoMirrored.Filled.KeyboardArrowRight,
                    contentDescription = null,
                    tint = tokens.textTertiary,
                )
            }
        }
    }
}
