package ru.vertices.android.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider

/**
 * Material 3 wrapper that loads the Vertex tokens. Always dark — no light theme,
 * no Material You / dynamic color (brand consistency, see PLAN.md).
 */
@Composable
fun VertexTheme(content: @Composable () -> Unit) {
    val tokens = VertexDarkTokens
    val scheme = darkColorScheme(
        primary       = tokens.accentPrimary,
        onPrimary     = tokens.textOnAccent,
        secondary     = tokens.accentPrimaryHover,
        onSecondary   = tokens.textOnAccent,
        background    = tokens.bgCanvas,
        onBackground  = tokens.textPrimary,
        surface       = tokens.bgSurface,
        onSurface     = tokens.textPrimary,
        surfaceVariant       = tokens.bgSurfaceElev,
        onSurfaceVariant     = tokens.textSecondary,
        outline       = tokens.borderStrong,
        outlineVariant = tokens.borderSubtle,
        error         = tokens.stateError,
        onError       = tokens.textOnAccent,
    )
    CompositionLocalProvider(LocalVertexColors provides tokens) {
        MaterialTheme(
            colorScheme = scheme,
            typography  = VertexTypography,
            content = content,
        )
    }
}
