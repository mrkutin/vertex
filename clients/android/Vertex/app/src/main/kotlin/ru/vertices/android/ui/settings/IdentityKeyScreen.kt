package ru.vertices.android.ui.settings

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.widget.Toast
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import ru.vertices.android.ui.components.VxSection
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VxBodyStyle
import ru.vertices.android.ui.theme.VxIdentityHexStyle
import ru.vertices.android.ui.theme.VxRadius
import ru.vertices.android.ui.theme.VxSpace
import ru.vertices.android.ui.theme.VxSubheadlineStyle
import ru.vertices.android.ui.util.Haptics
import ru.vertices.android.viewmodel.SettingsViewModel

/**
 * Identity Key screen. Mirror of `IdentityKeyView.swift`. Public-only display
 * — fingerprint = first 16 hex chars in 4×4 groups, full key revealed on
 * demand. Long-press / tap on copy button copies the full pubkey to the
 * clipboard for `vtx-admin reset-device`.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun IdentityKeyScreen(
    onBack: () -> Unit,
    viewModel: SettingsViewModel = hiltViewModel(),
) {
    val tokens = LocalVertexColors.current
    val context = LocalContext.current
    val haptic = LocalHapticFeedback.current
    val pubkeyHex = remember { viewModel.identityPubkeyHex }
    var isRevealed by remember { mutableStateOf(false) }
    var showResetDialog by remember { mutableStateOf(false) }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Public Identity") },
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
            VxSection(
                header = "Public Identity",
                footer = "This device's permanent identity in the Vertex graph. Stored in the Android Keystore — never leaves this device. Tap Copy to grab the full key for admin reset on the edge.",
            ) {
                if (pubkeyHex.isEmpty()) {
                    Box(modifier = Modifier.fillMaxWidth().padding(VxSpace.s4)) {
                        Text(
                            text = "No identity key generated yet. The key is created on first connect.",
                            style = VxBodyStyle,
                            color = tokens.textSecondary,
                        )
                    }
                } else {
                    IdentityContent(
                        pubkeyHex = pubkeyHex,
                        isRevealed = isRevealed,
                        onToggleReveal = {
                            isRevealed = !isRevealed
                            Haptics.selection(haptic)
                        },
                        onCopy = {
                            val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                            cm.setPrimaryClip(ClipData.newPlainText("Vertex identity public key", pubkeyHex))
                            Toast.makeText(context, "Copied", Toast.LENGTH_SHORT).show()
                            Haptics.notifySuccess(haptic)
                        },
                    )
                }
            }

            VxSection {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable { showResetDialog = true }
                        .padding(horizontal = VxSpace.s4, vertical = VxSpace.s3),
                ) {
                    Text("Reset identity key", color = tokens.stateError, style = VxBodyStyle)
                }
            }

            Spacer(Modifier.height(VxSpace.s8))
        }
    }

    if (showResetDialog) {
        AlertDialog(
            onDismissRequest = { showResetDialog = false },
            title = { Text("Reset identity?") },
            text = {
                Text(
                    "The next connection will register a fresh public key on the exit. Until the admin runs `vtx-admin reset-device <name>`, the exit will reject this device.",
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    viewModel.resetIdentity()
                    showResetDialog = false
                    Toast.makeText(context, "Identity reset", Toast.LENGTH_SHORT).show()
                }) { Text("Reset", color = tokens.stateError) }
            },
            dismissButton = {
                TextButton(onClick = { showResetDialog = false }) { Text("Cancel") }
            },
            containerColor = tokens.bgSurfaceElev,
            titleContentColor = tokens.textPrimary,
            textContentColor = tokens.textSecondary,
        )
    }
}

@Composable
private fun IdentityContent(
    pubkeyHex: String,
    isRevealed: Boolean,
    onToggleReveal: () -> Unit,
    onCopy: () -> Unit,
) {
    val tokens = LocalVertexColors.current
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(VxSpace.s4),
        verticalArrangement = Arrangement.spacedBy(VxSpace.s3),
    ) {
        Text("Fingerprint", style = VxSubheadlineStyle, color = tokens.textSecondary)

        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = fingerprintOf(pubkeyHex),
                style = VxIdentityHexStyle,
                color = tokens.textPrimary,
                modifier = Modifier.weight(1f).clickable(onClick = onCopy),
            )
            Row(
                modifier = Modifier.clickable(onClick = onToggleReveal),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = if (isRevealed) "Hide" else "Reveal",
                    color = tokens.accentPrimary,
                    style = VxBodyStyle,
                )
                Icon(
                    imageVector = Icons.Default.ExpandMore,
                    contentDescription = null,
                    tint = tokens.accentPrimary,
                    modifier = Modifier.rotate(if (isRevealed) 180f else 0f),
                )
            }
        }

        AnimatedVisibility(
            visible = isRevealed,
            enter = fadeIn() + expandVertically(),
            exit = fadeOut() + shrinkVertically(),
        ) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(VxRadius.md))
                    .background(tokens.bgSurfaceMuted)
                    .padding(VxSpace.s3)
                    .clickable(onClick = onCopy),
            ) {
                Box(modifier = Modifier.horizontalScroll(rememberScrollState())) {
                    Text(text = pubkeyHex, style = VxIdentityHexStyle, color = tokens.textPrimary)
                }
            }
        }
    }
}

/** First 16 hex chars in 4×4 groups, e.g. "a3f1c290b847fe05…" → "a3f1 c290 b847 fe05". */
private fun fingerprintOf(hex: String): String =
    hex.take(16).chunked(4).joinToString(" ")
