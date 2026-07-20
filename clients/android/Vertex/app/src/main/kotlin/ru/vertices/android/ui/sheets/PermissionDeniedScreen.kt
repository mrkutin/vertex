package ru.vertices.android.ui.sheets

import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import ru.vertices.android.core.ipc.ConnectionState
import ru.vertices.android.ui.connect.VertexHero
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VxBodyStyle
import ru.vertices.android.ui.theme.VxSpace

/**
 * Full-screen "Permission required" view. Mirror of `PermissionDeniedView.swift`.
 * Shown when the user dismisses the system VPN-permission prompt.
 */
@Composable
fun PermissionDeniedScreen(
    onTryAgain: () -> Unit,
    onCancel: () -> Unit,
) {
    val tokens = LocalVertexColors.current
    val context = LocalContext.current

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(tokens.bgCanvas)
            .padding(VxSpace.s6),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(VxSpace.s7),
    ) {
        Spacer(Modifier.size(0.dp).fillMaxWidth())
        LockedHero()
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(VxSpace.s3),
        ) {
            Text(
                text = "Permission required",
                style = TextStyle(
                    fontFamily = FontFamily.Default,
                    fontWeight = FontWeight.SemiBold,
                    fontSize = 24.sp,
                ),
                color = tokens.textPrimary,
                textAlign = TextAlign.Center,
            )
            Text(
                text = "Vertex needs permission to add a network configuration on this device. Tap Try again and approve the system prompt.",
                style = VxBodyStyle,
                color = tokens.textSecondary,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(horizontal = VxSpace.s6),
            )
        }

        Spacer(Modifier.size(0.dp).fillMaxWidth())

        Column(
            modifier = Modifier.fillMaxWidth(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(VxSpace.s3),
        ) {
            CapsuleButton(
                title = "Try again",
                fill = tokens.accentPrimary,
                textColor = tokens.textOnAccent,
                onClick = onTryAgain,
            )
            CapsuleOutlineButton(
                title = "Open Settings",
                onClick = {
                    val intent = Intent(Settings.ACTION_VPN_SETTINGS).apply {
                        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    }
                    runCatching { context.startActivity(intent) }
                        .onFailure {
                            // Fall back to the per-app settings page.
                            val fallback = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                                data = Uri.parse("package:${context.packageName}")
                                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                            }
                            context.startActivity(fallback)
                        }
                },
            )
            Box(
                modifier = Modifier
                    .heightIn(min = 44.dp)
                    .fillMaxWidth()
                    .clickable(onClick = onCancel),
                contentAlignment = Alignment.Center,
            ) {
                Text("Cancel", style = VxBodyStyle, color = tokens.textSecondary)
            }
        }
    }
}

@Composable
private fun LockedHero() {
    val tokens = LocalVertexColors.current
    Box(modifier = Modifier.size(216.dp), contentAlignment = Alignment.Center) {
        VertexHero(state = ConnectionState.DISCONNECTED, contentSize = 180, haloPadding = 18)
        // Amber glow overlay.
        Box(
            modifier = Modifier
                .size(220.dp)
                .background(
                    brush = Brush.radialGradient(
                        colors = listOf(
                            tokens.stateTransitioningGlow.copy(alpha = 0.4f),
                            Color.Transparent,
                        ),
                    ),
                ),
        )
        // Lock badge anchored at the bottom (over the vertex node).
        Column(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.Bottom,
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Box(
                modifier = Modifier
                    .padding(bottom = 28.dp)
                    .size(36.dp)
                    .clip(CircleShape)
                    .background(tokens.bgCanvas)
                    .border(1.dp, tokens.stateTransitioning, CircleShape),
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    imageVector = Icons.Default.Lock,
                    contentDescription = null,
                    tint = tokens.stateTransitioning,
                    modifier = Modifier.size(20.dp),
                )
            }
        }
    }
}

@Composable
private fun CapsuleButton(
    title: String,
    fill: Color,
    textColor: Color,
    onClick: () -> Unit,
) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 56.dp)
            .clip(CircleShape)
            .background(fill)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
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

@Composable
private fun CapsuleOutlineButton(title: String, onClick: () -> Unit) {
    val tokens = LocalVertexColors.current
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 52.dp)
            .clip(CircleShape)
            .border(1.dp, tokens.borderStrong, CircleShape)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = title,
            color = tokens.textPrimary,
            style = TextStyle(
                fontFamily = FontFamily.Default,
                fontWeight = FontWeight.Medium,
                fontSize = 17.sp,
            ),
        )
    }
}
