package ru.vertices.android.ui.util

import androidx.compose.ui.hapticfeedback.HapticFeedback
import androidx.compose.ui.hapticfeedback.HapticFeedbackType

/**
 * Thin wrapper around the Compose-provided [HapticFeedback] so calls read like
 * iOS `Haptics.impact(.medium) / Haptics.selection() / Haptics.notify(.success)`.
 *
 * Compose only exposes two real haptic types — LongPress (medium impact) and
 * TextHandleMove (light selection tick). Notification semantics ("success",
 * "error") map to the same primitives — Android's Vibrator API would give a
 * richer pattern but the platform-default is intentional: respects the user's
 * system-wide haptic preferences without a Vibrator permission.
 */
object Haptics {
    fun impact(haptic: HapticFeedback) {
        haptic.performHapticFeedback(HapticFeedbackType.LongPress)
    }

    fun selection(haptic: HapticFeedback) {
        haptic.performHapticFeedback(HapticFeedbackType.TextHandleMove)
    }

    fun notifySuccess(haptic: HapticFeedback) {
        haptic.performHapticFeedback(HapticFeedbackType.LongPress)
    }

    fun notifyError(haptic: HapticFeedback) {
        haptic.performHapticFeedback(HapticFeedbackType.LongPress)
    }
}
