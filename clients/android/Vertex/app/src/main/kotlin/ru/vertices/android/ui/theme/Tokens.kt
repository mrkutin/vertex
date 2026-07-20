package ru.vertices.android.ui.theme

import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color

/**
 * Vertex design tokens. Mirror of `clients/ios/Vertex/App/Assets.xcassets`
 * (dark variants only — parity with iOS `preferredColorScheme(.dark)`).
 *
 * Hex values are extracted byte-for-byte from each `*.colorset/Contents.json`.
 * If you change one, sync it on both sides.
 */
@Immutable
data class VertexColors(
    val bgCanvas: Color,
    val bgCanvasTop: Color,
    val bgCanvasBottom: Color,
    val bgSurface: Color,
    val bgSurfaceElev: Color,
    val bgSurfaceMuted: Color,

    val borderSubtle: Color,
    val borderStrong: Color,

    val accentPrimary: Color,
    val accentPrimaryHover: Color,
    val accentPrimaryMuted: Color,

    val glowPrimary: Color,
    val glowCoreHot: Color,
    val glowWarm: Color,

    val textPrimary: Color,
    val textSecondary: Color,
    val textTertiary: Color,
    val textOnAccent: Color,

    val glyphPrimary: Color,
    val glyphDim: Color,

    val stateConnected: Color,
    val stateConnectedGlow: Color,
    val stateTransitioning: Color,
    val stateTransitioningGlow: Color,
    val stateDormant: Color,
    val stateError: Color,
    val stateErrorGlow: Color,
)

val VertexDarkTokens = VertexColors(
    bgCanvas        = Color(0xFF080F26),
    bgCanvasTop     = Color(0xFF162456),
    bgCanvasBottom  = Color(0xFF080F26),
    bgSurface       = Color(0xFF101A3D),
    bgSurfaceElev   = Color(0xFF162456),
    bgSurfaceMuted  = Color(0xFF0C1430),

    borderSubtle    = Color(0x14FFFFFF),
    borderStrong    = Color(0x477DB3FF),

    accentPrimary       = Color(0xFF7DB3FF),
    accentPrimaryHover  = Color(0xFF9CC6FF),
    accentPrimaryMuted  = Color(0x2E7DB3FF),

    glowPrimary  = Color(0x997DB3FF),
    glowCoreHot  = Color(0x99FFFFFF),
    glowWarm     = Color(0x8CFAF4E6),

    textPrimary    = Color(0xFFFFFFFF),
    textSecondary  = Color(0xC7C4D2EE),
    textTertiary   = Color(0x998497C2),
    textOnAccent   = Color(0xFF0B1638),

    glyphPrimary   = Color(0xFFFFFFFF),
    glyphDim       = Color(0x80C4D2EE),

    stateConnected         = Color(0xFF7DB3FF),
    stateConnectedGlow     = Color(0xBFFFFFFF),
    stateTransitioning     = Color(0xFFFAD27A),
    stateTransitioningGlow = Color(0x80FAD27A),
    stateDormant           = Color(0xFF8497C2),
    stateError             = Color(0xFFFF6E78),
    stateErrorGlow         = Color(0x80FF6E78),
)

/** Composition-local for the Vertex tokens, populated by [VertexTheme]. */
val LocalVertexColors = staticCompositionLocalOf { VertexDarkTokens }
