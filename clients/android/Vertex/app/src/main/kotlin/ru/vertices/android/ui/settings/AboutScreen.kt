package ru.vertices.android.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import ru.vertices.android.BuildConfig
import ru.vertices.android.ui.components.VxDivider
import ru.vertices.android.ui.components.VxRow
import ru.vertices.android.ui.components.VxSection
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VxFootnoteStyle
import ru.vertices.android.ui.theme.VxSpace

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AboutScreen(onBack: () -> Unit) {
    val tokens = LocalVertexColors.current
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("About") },
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
            VersionSection()
            HowItWorksSection()
            Spacer(Modifier.height(VxSpace.s8))
        }
    }
}

@Composable
private fun VersionSection() {
    val tokens = LocalVertexColors.current
    val configuration = if (BuildConfig.DEBUG) "Debug" else "Release"
    val configurationColor = if (BuildConfig.DEBUG) tokens.stateTransitioning else tokens.textPrimary
    VxSection {
        VxRow {
            Text("Version", color = tokens.textSecondary, modifier = Modifier.weight(1f))
            Text(BuildConfig.VERSION_NAME, color = tokens.textPrimary)
        }
        VxDivider()
        VxRow {
            Text("Build", color = tokens.textSecondary, modifier = Modifier.weight(1f))
            Text(BuildConfig.VERSION_CODE.toString(), color = tokens.textPrimary)
        }
        VxDivider()
        VxRow {
            Text("Configuration", color = tokens.textSecondary, modifier = Modifier.weight(1f))
            Text(
                text = configuration,
                color = configurationColor,
                style = TextStyle(fontFamily = FontFamily.Monospace),
            )
        }
        VxDivider()
        VxRow {
            Text("Copyright", color = tokens.textSecondary, modifier = Modifier.weight(1f))
            Text("© 2026 Mr. Kutin", color = tokens.textPrimary)
        }
        VxDivider()
        VxRow {
            Box(modifier = Modifier.fillMaxWidth()) {
                Text(
                    text = "Where paths meet",
                    style = TextStyle(
                        fontFamily = FontFamily.Default,
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Normal,
                        fontStyle = FontStyle.Italic,
                    ),
                    color = tokens.textTertiary,
                    modifier = Modifier.fillMaxWidth(),
                    textAlign = androidx.compose.ui.text.style.TextAlign.End,
                )
            }
        }
    }
}

@Composable
private fun HowItWorksSection() {
    val tokens = LocalVertexColors.current
    VxSection(header = "How it works") {
        VxRow {
            Text(
                text = "Vertex routes your device through a trusted network vertex — a meeting point where edges converge. Every connection is end-to-end protected with modern cryptography (X25519 + ChaCha20-Poly1305); no relay along the path can read or alter your data.",
                style = VxFootnoteStyle,
                color = tokens.textSecondary,
            )
        }
    }
}
