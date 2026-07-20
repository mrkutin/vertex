package ru.vertices.android.ui.theme

import androidx.compose.material3.Typography
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

/**
 * Typography mirror of `Font+Vertex.swift`. We keep the same point sizes as
 * iOS — Material 3 Typography is filled out so default M3 components read
 * "Vertex" and not "Material default", and the additional Vx*Style values
 * cover the brand-specific hero/wordmark/mono moments.
 *
 * Three families:
 *   - Default (iOS uses SF Pro Text/Display) — body & titles
 *   - Default rounded (iOS SF Pro Rounded) — wordmark + hero status
 *   - Monospace (iOS SF Mono) — IPs, byte counts, fingerprints
 *
 * Compose has no direct "rounded" variant for the platform sans, so the brand
 * wordmark/hero status fall back to Default at the Bold/SemiBold weight.
 */
val VertexTypography: Typography = Typography(
    bodyLarge = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.Normal,
        fontSize = 17.sp,
        lineHeight = 22.sp,
    ),
    bodyMedium = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.Normal,
        fontSize = 15.sp,
        lineHeight = 20.sp,
    ),
    bodySmall = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.Normal,
        fontSize = 13.sp,
        lineHeight = 18.sp,
    ),
    titleLarge = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.SemiBold,
        fontSize = 22.sp,
        lineHeight = 28.sp,
    ),
    titleMedium = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.SemiBold,
        fontSize = 17.sp,
        lineHeight = 22.sp,
    ),
    titleSmall = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.Medium,
        fontSize = 15.sp,
        lineHeight = 20.sp,
    ),
    headlineLarge = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.SemiBold,
        fontSize = 28.sp,
        lineHeight = 32.sp,
    ),
    labelLarge = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.SemiBold,
        fontSize = 15.sp,
    ),
    labelMedium = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.Medium,
        fontSize = 13.sp,
    ),
    labelSmall = TextStyle(
        fontFamily = FontFamily.Default,
        fontWeight = FontWeight.Medium,
        fontSize = 12.sp,
    ),
)

// MARK: - Brand-specific styles

/** Brand wordmark "VERTEX" — capitals, tracked. iOS: 17pt rounded bold. */
val VxWordmarkStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontWeight = FontWeight.Bold,
    fontSize = 17.sp,
    letterSpacing = 1.5.sp,
)

/** Hero status caption ("Connected" / "Connecting…"). iOS: 28pt rounded semibold. */
val VxHeroStatusStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontWeight = FontWeight.SemiBold,
    fontSize = 28.sp,
    lineHeight = 32.sp,
    letterSpacing = (-0.4).sp,
)

/** Card title / row primary text. iOS vxHeadline (17pt semibold). */
val VxHeadlineStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontWeight = FontWeight.SemiBold,
    fontSize = 17.sp,
)

/** Body, default. iOS vxBody (17pt regular). */
val VxBodyStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontWeight = FontWeight.Normal,
    fontSize = 17.sp,
)

/** Body, selected list values. iOS vxBodyEmphasized (17pt medium). */
val VxBodyEmphasizedStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontWeight = FontWeight.Medium,
    fontSize = 17.sp,
)

/** StatusPill body. iOS vxCallout (16pt regular). */
val VxCalloutStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontWeight = FontWeight.Normal,
    fontSize = 16.sp,
)

/** Sub-labels in cards ("Vertex", "Edge"). iOS vxSubheadline (15pt regular). */
val VxSubheadlineStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontWeight = FontWeight.Normal,
    fontSize = 15.sp,
)

/** Footers, helper text. iOS vxFootnote (13pt regular). */
val VxFootnoteStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontWeight = FontWeight.Normal,
    fontSize = 13.sp,
)

/** Accessory chips. iOS vxCaption (12pt regular). */
val VxCaptionStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontWeight = FontWeight.Normal,
    fontSize = 12.sp,
    letterSpacing = 0.8.sp,
)

/** Schemes, codes (V₀ · YC). iOS vxCaptionMono (12pt mono medium). */
val VxCaptionMonoStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Monospace,
    fontWeight = FontWeight.Medium,
    fontSize = 12.sp,
)

/** Mono for IPs, hostnames, fingerprints. iOS vxMono (14pt mono medium). */
val VxMonoStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Monospace,
    fontWeight = FontWeight.Medium,
    fontSize = 14.sp,
    letterSpacing = 0.5.sp,
)

/** Stat values ("12.4 MB"). iOS statValue (17pt mono medium). */
val VxStatValueStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Monospace,
    fontWeight = FontWeight.Medium,
    fontSize = 17.sp,
)

/** Hero-scale mono. iOS statValueLarge (28pt mono semibold). */
val VxStatValueLargeStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Monospace,
    fontWeight = FontWeight.SemiBold,
    fontSize = 28.sp,
)

/** Pubkey hex. iOS identityHex (13pt mono regular). */
val VxIdentityHexStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Monospace,
    fontWeight = FontWeight.Normal,
    fontSize = 13.sp,
)

/** Section header (uppercased, tracked). Used by VxSection. */
val VxSectionHeaderStyle: TextStyle = TextStyle(
    fontFamily = FontFamily.Default,
    fontWeight = FontWeight.Normal,
    fontSize = 12.sp,
    letterSpacing = 0.8.sp,
)
