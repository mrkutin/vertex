package ru.vertices.android.ui.connect

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.unit.dp
import ru.vertices.android.ui.theme.LocalVertexColors

/**
 * Mini V-asterisk glyph for "Vertex" rows. Geometry copied byte-exact from
 * `VxAsteriskGlyph.swift`: two strokes (6,8)→(16.5,22) and (18,8)→(7.5,22)
 * in a 24×24 box (stroke 2, round caps), three filled dots — endpoint dots
 * radius 1.6 at (6,8) and (18,8), vertex dot radius 2.0 at (12,16).
 */
@Composable
fun VxAsteriskGlyph(modifier: Modifier = Modifier, sizeDp: Int = 22, color: Color? = null) {
    val tokens = LocalVertexColors.current
    val tint = color ?: tokens.accentPrimary
    Canvas(modifier = modifier.size(sizeDp.dp)) {
        val s = size.width / 24f
        val stroke = 2f * s
        drawLine(
            color = tint,
            start = Offset(6f * s, 8f * s),
            end   = Offset(16.5f * s, 22f * s),
            strokeWidth = stroke,
            cap = StrokeCap.Round,
        )
        drawLine(
            color = tint,
            start = Offset(18f * s, 8f * s),
            end   = Offset(7.5f * s, 22f * s),
            strokeWidth = stroke,
            cap = StrokeCap.Round,
        )
        drawCircle(color = tint, radius = 1.6f * s, center = Offset(6f * s,  8f * s))
        drawCircle(color = tint, radius = 1.6f * s, center = Offset(18f * s, 8f * s))
        drawCircle(color = tint, radius = 2.0f * s, center = Offset(12f * s, 16f * s))
    }
}

/**
 * Single ascending edge glyph for "Edge" rows. Mirror of `VxEdgeGlyph.swift`:
 * one stroke (4,18)→(20,6) in a 24×24 box, stroke 2 round caps, plus a 2.4-radius
 * destination dot at (20,6).
 */
@Composable
fun VxEdgeGlyph(modifier: Modifier = Modifier, sizeDp: Int = 22, color: Color? = null) {
    val tokens = LocalVertexColors.current
    val tint = color ?: tokens.accentPrimary
    Canvas(modifier = modifier.size(sizeDp.dp)) {
        val s = size.width / 24f
        drawLine(
            color = tint,
            start = Offset(4f * s, 18f * s),
            end   = Offset(20f * s, 6f * s),
            strokeWidth = 2f * s,
            cap = StrokeCap.Round,
        )
        drawCircle(color = tint, radius = 2.4f * s, center = Offset(20f * s, 6f * s))
    }
}
