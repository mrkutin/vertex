package ru.vertices.android.ui.theme

/**
 * Motion durations & period tokens. Mirror of `clients/ios/Vertex/App/Theme/Motion.swift`
 * — same numbers so the hero/pill animations feel identical on both platforms.
 *
 * Values in seconds.
 */
object VxMotion {
    const val HERO_REPLACE_S: Double         = 0.28
    const val HERO_BREATH_PERIOD_S: Double   = 2.4    // connected idle
    const val HERO_PULSE_PERIOD_S: Double    = 0.9    // connecting handshake
    const val HERO_REASSERT_PERIOD_S: Double = 1.4
    const val HERO_ERROR_SHAKE_S: Double     = 0.36
    const val HERO_DISCONNECT_FADE_S: Double = 0.6

    const val BUTTON_PRESS_S: Double         = 0.12
    const val BUTTON_GLOW_PERIOD_S: Double   = 1.8

    const val SHEET_PRESENT_S: Double        = 0.36
    const val SHEET_DISMISS_S: Double        = 0.28

    const val NAV_PUSH_S: Double             = 0.32
    const val STATS_APPEAR_S: Double         = 0.28
    const val PILL_TEXT_CHANGE_S: Double     = 0.18
    const val NUMBER_TRANSITION_S: Double    = 0.22
    const val EDGE_FLOW_S: Double            = 5.0
}
