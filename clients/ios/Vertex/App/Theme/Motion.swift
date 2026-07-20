import SwiftUI

/// Vertex motion tokens — see design/UI-SPEC.md §4.
/// Reduce-motion fallbacks live at call sites (use `accessibilityReduceMotion`).
enum VxMotion {
    // Durations (seconds)
    static let heroReplace: Double         = 0.28
    static let heroBreathPeriod: Double    = 2.4   // connected idle
    static let heroPulsePeriod: Double     = 0.9   // connecting handshake
    static let heroReassertPeriod: Double  = 1.4
    static let heroErrorShake: Double      = 0.36
    static let heroDisconnectFade: Double  = 0.6

    static let buttonPress: Double         = 0.12
    static let buttonGlowPeriod: Double    = 1.8

    static let sheetPresent: Double        = 0.36
    static let sheetDismiss: Double        = 0.28

    static let navPush: Double             = 0.32
    static let statsAppear: Double         = 0.28
    static let pillTextChange: Double      = 0.18
    static let numberTransition: Double    = 0.22
    static let edgeFlow: Double            = 5.0

    // Curves
    static let easeInOut = Animation.easeInOut(duration: heroReplace)
    static let breath    = Animation.easeInOut(duration: heroBreathPeriod / 2).repeatForever(autoreverses: true)
    static let pulse     = Animation.easeInOut(duration: heroPulsePeriod / 2).repeatForever(autoreverses: true)
    static let buttonGlow = Animation.easeInOut(duration: buttonGlowPeriod / 2).repeatForever(autoreverses: true)
    static let buttonPressSpring = Animation.spring(response: 0.18, dampingFraction: 0.7)
    static let errorShake = Animation.spring(response: 0.32, dampingFraction: 0.55)
    static let statsSpring = Animation.spring(response: 0.36, dampingFraction: 0.78)
}
