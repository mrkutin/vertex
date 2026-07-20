package ru.vertices.android.ui.components

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.unit.dp
import ru.vertices.android.ui.theme.LocalVertexColors

/**
 * Selected-row picker mark — V-asterisk with a soft accent halo. Mirror of
 * `VxSelectionGlyph.swift`. Rendered as: blurred halo disc (alpha 0.35,
 * radius ~size+12, simulated with a radial gradient) under a solid
 * V-asterisk in `accentPrimary`.
 *
 * Compose's `Modifier.blur` is hardware-blur-only (API 31+); a radial gradient
 * achieves the same visual on every API level we support and renders identically
 * to the iOS version in side-by-side screenshots.
 */
@Composable
fun VxSelectionGlyph(modifier: Modifier = Modifier, sizeDp: Int = 20) {
    val tokens = LocalVertexColors.current
    val frame = sizeDp + 14
    Box(
        modifier = modifier.size(frame.dp),
        contentAlignment = Alignment.Center,
    ) {
        Canvas(modifier = Modifier.size((sizeDp + 12).dp)) {
            val r = size.minDimension / 2f
            drawCircle(
                brush = Brush.radialGradient(
                    colors = listOf(
                        tokens.accentPrimary.copy(alpha = 0.35f),
                        tokens.accentPrimary.copy(alpha = 0f),
                    ),
                    center = Offset(size.width / 2f, size.height / 2f),
                    radius = r,
                ),
                radius = r,
            )
        }
        // Solid V-asterisk on top.
        Canvas(modifier = Modifier.size(sizeDp.dp)) {
            val s = size.width / 24f
            val tint = tokens.accentPrimary
            val stroke = 2f * s
            drawLine(
                color = tint,
                start = Offset(6f * s, 8f * s),
                end = Offset(16.5f * s, 22f * s),
                strokeWidth = stroke,
                cap = StrokeCap.Round,
            )
            drawLine(
                color = tint,
                start = Offset(18f * s, 8f * s),
                end = Offset(7.5f * s, 22f * s),
                strokeWidth = stroke,
                cap = StrokeCap.Round,
            )
            drawCircle(color = tint, radius = 1.6f * s, center = Offset(6f * s, 8f * s))
            drawCircle(color = tint, radius = 1.6f * s, center = Offset(18f * s, 8f * s))
            drawCircle(color = tint, radius = 2.0f * s, center = Offset(12f * s, 16f * s))
        }
    }
}
