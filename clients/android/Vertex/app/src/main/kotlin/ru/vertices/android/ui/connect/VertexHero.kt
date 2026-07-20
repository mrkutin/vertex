package ru.vertices.android.ui.connect

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch
import kotlin.math.PI
import kotlin.math.log10
import kotlin.math.min
import kotlin.math.pow
import kotlin.math.sin
import ru.vertices.android.core.ipc.ConnectionState
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VertexColors
import ru.vertices.android.ui.theme.VxMotion

/**
 * Marquee hero for ConnectScreen. Port of `clients/ios/Vertex/App/Views/Components/VertexHero.swift`.
 *
 * Renders the V-asterisk geometry from the app icon with state-keyed glow,
 * node activation during connecting, gentle "breath" while connected, edge
 * shimmer driven by upload/download rates, and a shake on error.
 *
 * Layers (back to front):
 *   1   ambient halo (radial gradient)
 *   2/3 endpoint glows (rate-driven on connected)
 *   4   vertex glow (largest)
 *   5/6 edges as full extended through-dot lines
 *   6a/b active-segment halo bloom (endpoint→vertex only, rate-driven)
 *   7/8 edge shimmer (active-traffic only)
 *   9   endpoint cores (cover lines at endpoints)
 *  10   vertex core ON TOP
 *
 * Time source: a single `withFrameNanos` loop owned by a [LaunchedEffect] keyed
 * on the state's pause flag. Identical to iOS `TimelineView(.animation)` —
 * paused when disconnected to preserve battery.
 */
@Composable
fun VertexHero(
    modifier: Modifier = Modifier,
    state: ConnectionState = ConnectionState.DISCONNECTED,
    uploadRateBps: Double = 0.0,
    downloadRateBps: Double = 0.0,
    contentSize: Int = 220,
    haloPadding: Int = 24,
) {
    val tokens = LocalVertexColors.current
    val frame = contentSize + 2 * haloPadding

    // Smoothed (animated) rate scalars in [0,1].
    val rUp = remember { Animatable(0f) }
    val rDn = remember { Animatable(0f) }
    val scope = rememberCoroutineScope()

    LaunchedEffect(uploadRateBps) {
        scope.launch {
            rUp.animateTo(
                targetValue = quantizedIntensity(uploadRateBps).toFloat(),
                animationSpec = tween(durationMillis = 400),
            )
        }
    }
    LaunchedEffect(downloadRateBps) {
        scope.launch {
            rDn.animateTo(
                targetValue = quantizedIntensity(downloadRateBps).toFloat(),
                animationSpec = tween(durationMillis = 400),
            )
        }
    }

    // Time tick — paused while disconnected to save battery, identical
    // semantics to iOS TimelineView's `paused:` flag.
    var nowMs by remember { mutableLongStateOf(0L) }
    val pauseAnim = state == ConnectionState.DISCONNECTED
    LaunchedEffect(pauseAnim) {
        if (pauseAnim) return@LaunchedEffect
        val start = withFrameNanos { it }
        while (true) {
            withFrameNanos { ns -> nowMs = (ns - start) / 1_000_000L }
        }
    }

    val time = nowMs / 1000.0
    val targets = stateTargets(state, time, rUp.value.toDouble(), rDn.value.toDouble(), tokens)

    Canvas(
        modifier = modifier
            .size(frame.dp)
            .padding(haloPadding.dp)
            .graphicsLayer { /* shake hook for future error state */ },
    ) {
        drawHero(
            scale = size.width / 220f,
            time = time,
            rUp = rUp.value.toDouble(),
            rDn = rDn.value.toDouble(),
            t = targets,
        )
    }
}

// MARK: - Geometry (220×220 reference plane, copied byte-exact from iOS)

private object Geom {
    val endpointA = Offset(60.2f, 65.3f)
    val endpointB = Offset(159.8f, 65.3f)
    val vertex = Offset(110.0f, 154.7f)

    val lineAStart = Offset(51.8f, 50.3f)
    val lineAEnd = Offset(122.5f, 177.2f)
    val lineBStart = Offset(168.2f, 50.3f)
    val lineBEnd = Offset(97.5f, 177.2f)

    const val EDGE_STROKE_FULL_PT = 12.0f
    const val EDGE_STROKE_IDLE_PT = 9.0f
    const val ENDPOINT_RADIUS_PT = 10.3f
    const val VERTEX_RADIUS_PT = 15.5f

    const val ENDPOINT_GLOW_RADIUS_PT = 30.1f
    const val VERTEX_GLOW_RADIUS_PT = 51.6f
}

// MARK: - State targets

private data class HeroTargets(
    val nodeAColor: Color,
    val nodeBColor: Color,
    val vertexColor: Color,
    val edgeColor: Color,
    val nodeAIntensity: Double,
    val nodeBIntensity: Double,
    val vertexIntensity: Double,
    val edgeOpacity: Double,
    val edgeStrokePt: Float,
    val nodeAGlowRadiusPt: Float,
    val nodeBGlowRadiusPt: Float,
    val nodeAGlowAlphaCap: Double,
    val nodeBGlowAlphaCap: Double,
    val vertexGlowRadiusPt: Float,
    val edgeSweepA: Double?,
    val edgeSweepB: Double?,
    val shimmerASpeed: Double?,
    val shimmerBSpeed: Double?,
    val haloColor: Color,
    val haloIntensity: Double,
    val haloScale: Double,
)

private fun stateTargets(
    state: ConnectionState,
    time: Double,
    rUp: Double,
    rDn: Double,
    tokens: VertexColors,
): HeroTargets {
    val twoPi = 2.0 * PI
    return when (state) {
        ConnectionState.CONNECTED -> {
            val phase = sin(time * (twoPi / VxMotion.HERO_BREATH_PERIOD_S))
            val breathAlpha = 0.55 + 0.15 * phase
            val breathScale = 1.0 + 0.04 * phase

            val nodeAIntensity = 0.55 + 0.45 * rUp
            val nodeBIntensity = 0.55 + 0.45 * rDn
            val nodeAGlowR = Geom.ENDPOINT_GLOW_RADIUS_PT + 14.9f * rUp.toFloat()
            val nodeBGlowR = Geom.ENDPOINT_GLOW_RADIUS_PT + 14.9f * rDn.toFloat()
            val nodeAGlowAlphaCap = 0.45 + 0.30 * rUp
            val nodeBGlowAlphaCap = 0.45 + 0.30 * rDn

            val shimmerA = if (rUp > 0.001) (0.6 + 1.4 * rUp) else null
            val shimmerB = if (rDn > 0.001) (0.6 + 1.4 * rDn) else null

            HeroTargets(
                nodeAColor = Color.White,
                nodeBColor = Color.White,
                vertexColor = Color.White,
                edgeColor = Color.White,
                nodeAIntensity = nodeAIntensity,
                nodeBIntensity = nodeBIntensity,
                vertexIntensity = 1.0,
                edgeOpacity = 1.0,
                edgeStrokePt = Geom.EDGE_STROKE_FULL_PT,
                nodeAGlowRadiusPt = nodeAGlowR,
                nodeBGlowRadiusPt = nodeBGlowR,
                nodeAGlowAlphaCap = nodeAGlowAlphaCap,
                nodeBGlowAlphaCap = nodeBGlowAlphaCap,
                vertexGlowRadiusPt = 38.0f,
                edgeSweepA = null,
                edgeSweepB = null,
                shimmerASpeed = shimmerA,
                shimmerBSpeed = shimmerB,
                haloColor = tokens.stateConnectedGlow,
                haloIntensity = 0.6 * breathAlpha,
                haloScale = breathScale,
            )
        }

        ConnectionState.CONNECTING -> {
            val cycle = VxMotion.HERO_PULSE_PERIOD_S * 3.0
            val cyclePos = (time % cycle) / VxMotion.HERO_PULSE_PERIOD_S
            val active = (cyclePos.toInt()) % 3
            val pulse = 0.55 + 0.45 * sin(time * (twoPi / VxMotion.HERO_PULSE_PERIOD_S))

            val aInt = if (active == 0) pulse else 0.45
            val bInt = if (active == 1) pulse else 0.45
            val vInt = if (active == 2) pulse else 0.55

            val sweepFrac = cyclePos % 1.0
            val sweepA = if (active == 0) sweepFrac else null
            val sweepB = if (active == 1) sweepFrac else null

            val connectingBreath = 0.7 + 0.3 * sin(time * (twoPi / VxMotion.HERO_PULSE_PERIOD_S))

            HeroTargets(
                nodeAColor = tokens.stateTransitioning,
                nodeBColor = tokens.stateTransitioning,
                vertexColor = tokens.stateTransitioning,
                edgeColor = tokens.stateTransitioning,
                nodeAIntensity = aInt,
                nodeBIntensity = bInt,
                vertexIntensity = vInt,
                edgeOpacity = 0.65,
                edgeStrokePt = Geom.EDGE_STROKE_FULL_PT,
                nodeAGlowRadiusPt = Geom.ENDPOINT_GLOW_RADIUS_PT,
                nodeBGlowRadiusPt = Geom.ENDPOINT_GLOW_RADIUS_PT,
                nodeAGlowAlphaCap = 0.55,
                nodeBGlowAlphaCap = 0.55,
                vertexGlowRadiusPt = Geom.VERTEX_GLOW_RADIUS_PT,
                edgeSweepA = sweepA,
                edgeSweepB = sweepB,
                shimmerASpeed = null,
                shimmerBSpeed = null,
                haloColor = tokens.stateTransitioningGlow,
                haloIntensity = 0.4 * connectingBreath,
                haloScale = 1.0,
            )
        }

        ConnectionState.HANDSHAKING,
        ConnectionState.RECONNECTING -> {
            val cycle = VxMotion.HERO_REASSERT_PERIOD_S * 3.0
            val cyclePos = (time % cycle) / VxMotion.HERO_REASSERT_PERIOD_S
            val active = (cyclePos.toInt()) % 3
            val pulse = 0.55 + 0.35 * sin(time * (twoPi / VxMotion.HERO_REASSERT_PERIOD_S))
            val aInt = if (active == 0) pulse else 0.45
            val bInt = if (active == 1) pulse else 0.45
            val vInt = if (active == 2) pulse else 0.55
            HeroTargets(
                nodeAColor = tokens.stateTransitioning,
                nodeBColor = tokens.stateTransitioning,
                vertexColor = tokens.stateTransitioning,
                edgeColor = tokens.stateTransitioning,
                nodeAIntensity = aInt,
                nodeBIntensity = bInt,
                vertexIntensity = vInt,
                edgeOpacity = 0.6,
                edgeStrokePt = Geom.EDGE_STROKE_FULL_PT,
                nodeAGlowRadiusPt = Geom.ENDPOINT_GLOW_RADIUS_PT,
                nodeBGlowRadiusPt = Geom.ENDPOINT_GLOW_RADIUS_PT,
                nodeAGlowAlphaCap = 0.55,
                nodeBGlowAlphaCap = 0.55,
                vertexGlowRadiusPt = Geom.VERTEX_GLOW_RADIUS_PT,
                edgeSweepA = null,
                edgeSweepB = null,
                shimmerASpeed = null,
                shimmerBSpeed = null,
                haloColor = tokens.stateTransitioningGlow,
                haloIntensity = 0.3,
                haloScale = 1.0,
            )
        }

        ConnectionState.DISCONNECTED -> HeroTargets(
            nodeAColor = tokens.glyphDim,
            nodeBColor = tokens.glyphDim,
            vertexColor = tokens.glyphDim,
            edgeColor = tokens.glyphDim,
            nodeAIntensity = 1.0,
            nodeBIntensity = 1.0,
            vertexIntensity = 1.0,
            edgeOpacity = 0.85,
            edgeStrokePt = Geom.EDGE_STROKE_IDLE_PT,
            nodeAGlowRadiusPt = Geom.ENDPOINT_GLOW_RADIUS_PT,
            nodeBGlowRadiusPt = Geom.ENDPOINT_GLOW_RADIUS_PT,
            nodeAGlowAlphaCap = 0.55,
            nodeBGlowAlphaCap = 0.55,
            vertexGlowRadiusPt = Geom.VERTEX_GLOW_RADIUS_PT,
            edgeSweepA = null,
            edgeSweepB = null,
            shimmerASpeed = null,
            shimmerBSpeed = null,
            haloColor = Color.Transparent,
            haloIntensity = 0.0,
            haloScale = 1.0,
        )
    }
}

// MARK: - Drawing

private fun DrawScope.drawHero(scale: Float, time: Double, rUp: Double, rDn: Double, t: HeroTargets) {
    val center = Offset(size.width / 2f, size.height / 2f)
    val a = Geom.endpointA.scaled(scale)
    val b = Geom.endpointB.scaled(scale)
    val v = Geom.vertex.scaled(scale)
    val edgeStroke = t.edgeStrokePt * scale
    val endpointR = Geom.ENDPOINT_RADIUS_PT * scale
    val vertexR = Geom.VERTEX_RADIUS_PT * scale
    val lineAStart = Geom.lineAStart.scaled(scale)
    val lineAEnd = Geom.lineAEnd.scaled(scale)
    val lineBStart = Geom.lineBStart.scaled(scale)
    val lineBEnd = Geom.lineBEnd.scaled(scale)

    // Layer 1 — ambient halo.
    if (t.haloIntensity > 0.001) {
        val r = (size.width * 0.5f * t.haloScale.toFloat())
        val haloAlpha = (t.haloColor.alpha * t.haloIntensity).toFloat().coerceIn(0f, 1f)
        drawCircle(
            brush = Brush.radialGradient(
                colors = listOf(
                    t.haloColor.copy(alpha = haloAlpha),
                    t.haloColor.copy(alpha = 0f),
                ),
                center = center,
                radius = r,
            ),
            radius = r,
            center = center,
        )
    }

    drawNodeGlow(a, t.nodeAGlowRadiusPt * scale, t.nodeAColor, t.nodeAGlowAlphaCap, t.nodeAIntensity)
    drawNodeGlow(b, t.nodeBGlowRadiusPt * scale, t.nodeBColor, t.nodeBGlowAlphaCap, t.nodeBIntensity)
    drawNodeGlow(v, t.vertexGlowRadiusPt * scale, t.vertexColor, 0.65, t.vertexIntensity)

    val edgeColor = t.edgeColor.copy(alpha = t.edgeOpacity.toFloat().coerceIn(0f, 1f))
    drawLine(edgeColor, lineAStart, lineAEnd, edgeStroke, StrokeCap.Round)
    drawLine(edgeColor, lineBStart, lineBEnd, edgeStroke, StrokeCap.Round)

    drawSegmentHalo(a, v, rUp, edgeStroke, t.edgeColor)
    drawSegmentHalo(b, v, rDn, edgeStroke, t.edgeColor)

    t.edgeSweepA?.let { drawSweep(lineAStart, lineAEnd, it, edgeStroke, t.edgeOpacity) }
    t.edgeSweepB?.let { drawSweep(lineBStart, lineBEnd, it, edgeStroke, t.edgeOpacity) }

    t.shimmerASpeed?.let { drawShimmer(lineAStart, lineAEnd, rUp, it, time, edgeStroke, t.edgeColor) }
    t.shimmerBSpeed?.let { drawShimmer(lineBEnd, lineBStart, rDn, it, time, edgeStroke, t.edgeColor) }

    drawCore(a, endpointR, t.nodeAColor, t.nodeAIntensity)
    drawCore(b, endpointR, t.nodeBColor, t.nodeBIntensity)
    drawCore(v, vertexR, t.vertexColor, t.vertexIntensity)
}

private fun DrawScope.drawNodeGlow(
    centerPt: Offset,
    radius: Float,
    color: Color,
    innerAlphaCap: Double,
    intensity: Double,
) {
    if (intensity < 0.001 || radius < 0.5f) return
    drawCircle(
        brush = Brush.radialGradient(
            colors = listOf(
                color.copy(alpha = (intensity * innerAlphaCap).toFloat().coerceIn(0f, 1f)),
                color.copy(alpha = 0f),
            ),
            center = centerPt,
            radius = radius,
        ),
        radius = radius,
        center = centerPt,
    )
}

private fun DrawScope.drawCore(centerPt: Offset, radius: Float, color: Color, intensity: Double) {
    drawCircle(
        color = color.copy(alpha = intensity.toFloat().coerceIn(0f, 1f)),
        radius = radius,
        center = centerPt,
    )
}

private fun DrawScope.drawSegmentHalo(
    p0: Offset,
    p1: Offset,
    rate: Double,
    baseStroke: Float,
    color: Color,
) {
    if (rate < 0.001) return
    val width = baseStroke * (1.7f + 0.6f * rate.toFloat())
    val opacity = (0.10 + 0.28 * rate).toFloat().coerceIn(0f, 1f)
    drawLine(
        color = color.copy(alpha = opacity),
        start = p0,
        end = p1,
        strokeWidth = width,
        cap = StrokeCap.Round,
    )
}

private fun DrawScope.drawSweep(
    p0: Offset,
    p1: Offset,
    progress: Double,
    stroke: Float,
    edgeOpacity: Double,
) {
    val t = progress.coerceIn(0.0, 1.0).toFloat()
    if (t <= 0f) return
    val end = Offset(p0.x + (p1.x - p0.x) * t, p0.y + (p1.y - p0.y) * t)
    drawLine(
        color = Color.White.copy(alpha = (0.85 * edgeOpacity).toFloat().coerceIn(0f, 1f)),
        start = p0,
        end = end,
        strokeWidth = stroke * 0.7f,
        cap = StrokeCap.Round,
    )
}

private fun DrawScope.drawShimmer(
    p0: Offset,
    p1: Offset,
    rate: Double,
    speed: Double,
    time: Double,
    stroke: Float,
    color: Color,
) {
    if (rate < 0.001) return
    val u = (time * speed) % 1.0
    val half = 0.09
    val lo = (u - half).coerceAtLeast(0.0)
    val hi = (u + half).coerceAtMost(1.0)
    if (hi <= lo) return
    val s = Offset(p0.x + (p1.x - p0.x) * lo.toFloat(), p0.y + (p1.y - p0.y) * lo.toFloat())
    val e = Offset(p0.x + (p1.x - p0.x) * hi.toFloat(), p0.y + (p1.y - p0.y) * hi.toFloat())
    drawLine(
        color = color.copy(alpha = (0.20 + 0.35 * rate).toFloat().coerceIn(0f, 1f)),
        start = s,
        end = e,
        strokeWidth = stroke * 0.55f,
        cap = StrokeCap.Round,
    )
}

private fun Offset.scaled(s: Float): Offset = Offset(x * s, y * s)

/**
 * iOS RateMap.intensity — log curve from 50 Kbps (floor, 0) to 50 Mbps (ceil, 1)
 * with an exponent of 0.85 so the high end leans harder on the glow.
 */
private fun quantizedIntensity(bps: Double): Double {
    val floor = 50_000.0
    val ceil = 50_000_000.0
    val exponent = 0.85
    val raw = bps.coerceAtLeast(0.0)
    if (raw <= floor) return 0.0
    val clamped = min(raw, ceil)
    val num = log10(1.0 + clamped / floor)
    val den = log10(ceil / floor)
    val normalized = (num / den).coerceIn(0.0, 1.0)
    return normalized.pow(exponent)
}
