package ru.vertices.android.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VxCaptionMonoStyle

/**
 * Compact protocol chip used in Settings → Active Configuration.
 * Mirror of `VxProtocolBadge.swift`. Caller passes label uppercased.
 */
@Composable
fun VxProtocolBadge(label: String, isPrimary: Boolean, modifier: Modifier = Modifier) {
    val tokens = LocalVertexColors.current
    Text(
        text = label,
        style = VxCaptionMonoStyle,
        color = if (isPrimary) tokens.accentPrimary else tokens.textTertiary,
        modifier = modifier
            .clip(RoundedCornerShape(4.dp))
            .background(tokens.bgSurfaceMuted)
            .padding(horizontal = 6.dp, vertical = 2.dp),
    )
}
