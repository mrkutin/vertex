package ru.vertices.android.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import ru.vertices.android.ui.theme.LocalVertexColors
import ru.vertices.android.ui.theme.VxFootnoteStyle
import ru.vertices.android.ui.theme.VxRadius
import ru.vertices.android.ui.theme.VxSectionHeaderStyle
import ru.vertices.android.ui.theme.VxSpace

/**
 * Vertex section primitive — mirror of `VxSection.swift`.
 *
 * Grouped container with optional uppercased header and footer, rendered as
 * a `bgSurface` plate with a hairline `borderSubtle` outline. Rows live as
 * direct children. Used by Settings, IdentityKey, About, BrokerList, ExitList
 * — every inner screen.
 */
@Composable
fun VxSection(
    header: String? = null,
    footer: String? = null,
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    val tokens = LocalVertexColors.current
    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(VxSpace.s2),
    ) {
        if (header != null) {
            Text(
                text = header.uppercase(),
                style = VxSectionHeaderStyle,
                color = tokens.textTertiary,
                textDecoration = TextDecoration.None,
                modifier = Modifier.padding(start = VxSpace.s4),
            )
        }
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(VxRadius.lg))
                .background(tokens.bgSurface)
                .border(0.5.dp, tokens.borderSubtle, RoundedCornerShape(VxRadius.lg)),
            content = content,
        )
        if (footer != null) {
            Text(
                text = footer,
                style = VxFootnoteStyle,
                color = tokens.textSecondary,
                modifier = Modifier
                    .padding(horizontal = VxSpace.s4)
                    .padding(top = VxSpace.s1),
            )
        }
    }
}

/**
 * Single row inside a [VxSection]. Default 12dp vertical / 16dp horizontal padding,
 * 44dp minimum height — same touch target as iOS rows.
 */
@Composable
fun VxRow(
    modifier: Modifier = Modifier,
    content: @Composable RowScope.() -> Unit,
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .heightIn(min = 44.dp)
            .padding(horizontal = VxSpace.s4, vertical = VxSpace.s3),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(VxSpace.s3),
        content = content,
    )
}

/**
 * Hairline divider between rows. Indented from the leading edge to align with
 * row text content (matches iOS list separator inset).
 */
@Composable
fun VxDivider(leadingInset: Dp = VxSpace.s4) {
    val tokens = LocalVertexColors.current
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .padding(start = leadingInset)
            .height(0.5.dp)
            .background(tokens.borderSubtle),
    )
    // Spacer to keep section heights consistent when divider is drawn between
    // rows that already provide their own vertical padding.
    Spacer(Modifier.height(0.dp))
}
