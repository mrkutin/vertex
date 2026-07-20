package ru.vertices.android.ui.connect

import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.tween
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.BlurredEdgeTreatment
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import ru.vertices.android.core.ipc.ConnectionState
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.util.Haptics

/**
 * Primary action button. Mirror of `BigConnectButton.swift`.
 *
 *   - idle (DISCONNECTED) → fill = accentPrimary, text "Connect" / textOnAccent,
 *     soft pulsing accentPrimary glow behind the capsule (60% ↔ 100%, 1.8s).
 *   - transitioning → fill = accentPrimaryMuted, spinner + "Cancel" / textPrimary, no glow.
 *   - connected → fill = bgSurfaceMuted, "Disconnect" / stateError, no glow.
 *
 * Press feedback: 0.97 scale via spring + LongPress haptic on tap.
 */
@Composable
fun BigConnectButton(
    state: ConnectionState,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val tokens = LocalVertexColors.current
    val haptic = LocalHapticFeedback.current
    val transitioning = state == ConnectionState.CONNECTING ||
        state == ConnectionState.HANDSHAKING ||
        state == ConnectionState.RECONNECTING
    val isConnected = state == ConnectionState.CONNECTED

    val fill = when {
        transitioning -> tokens.accentPrimaryMuted
        isConnected   -> tokens.bgSurfaceMuted
        else          -> tokens.accentPrimary
    }
    val textColor = when {
        transitioning -> tokens.textPrimary
        isConnected   -> tokens.stateError
        else          -> tokens.textOnAccent
    }
    val title = when {
        transitioning -> "Cancel"
        isConnected   -> "Disconnect"
        else          -> "Connect"
    }
    val showsGlow = !transitioning && !isConnected

    val infinite = rememberInfiniteTransition(label = "btnGlow")
    val glowPhase by infinite.animateFloat(
        initialValue = 0f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            animation = tween(durationMillis = 1800),
            repeatMode = RepeatMode.Reverse,
        ),
        label = "glowPhase",
    )
    val glowOpacity = if (showsGlow) 0.6f + 0.4f * glowPhase else 0f

    val interactionSource = remember { MutableInteractionSource() }
    val pressed by interactionSource.collectIsPressedAsState()
    val pressScale = if (pressed) 0.97f else 1.0f

    Box(
        modifier = modifier
            .fillMaxWidth()
            .heightIn(min = 64.dp)
            .graphicsLayer {
                scaleX = pressScale
                scaleY = pressScale
            },
        contentAlignment = Alignment.Center,
    ) {
        if (showsGlow) {
            // BlurredEdgeTreatment(CircleShape) clips the source layer to the
            // capsule before blurring, so the glow matches the silhouette.
            // (Hardware blur — API 31+; on older devices renders as a solid
            // capsule which still reads as a soft accent halo.)
            Box(
                modifier = Modifier
                    .matchParentSize()
                    .alpha(glowOpacity)
                    .blur(24.dp, BlurredEdgeTreatment(CircleShape))
                    .background(tokens.accentPrimary, CircleShape),
            )
        }

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(min = 56.dp)
                .clip(CircleShape)
                .background(fill)
                .clickable(interactionSource = interactionSource, indication = null) {
                    Haptics.impact(haptic)
                    onClick()
                }
                .padding(horizontal = 24.dp, vertical = 14.dp),
            horizontalArrangement = Arrangement.Center,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (transitioning) {
                CircularProgressIndicator(
                    color = tokens.accentPrimary,
                    strokeWidth = 2.dp,
                    modifier = Modifier
                        .size(16.dp)
                        .padding(end = 10.dp),
                )
            }
            Text(
                text = title,
                color = textColor,
                style = TextStyle(
                    fontFamily = FontFamily.Default,
                    fontWeight = FontWeight.SemiBold,
                    fontSize = 18.sp,
                ),
            )
        }
    }
}
