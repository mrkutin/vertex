import AppKit

/// macOS adapter for the iOS-style Haptics API used across views. AppKit's
/// `NSHapticFeedbackManager` only fires on Mac trackpads with Force Touch
/// and is a no-op on regular hardware — safe to call unconditionally.
@MainActor
enum Haptics {
    /// iOS impact-style cue. Maps to `.alignment` on Mac (the closest
    /// neutral feedback pattern).
    static func impact(_ style: ImpactStyle = .medium) {
        NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
    }

    /// Selection change in pickers / lists.
    static func selection() {
        NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
    }

    /// Notification-style cue (success / error / warning). Maps success to
    /// `.alignment` (subtle confirm) and error to `.levelChange` (firmer).
    static func notify(_ type: NotifyType) {
        switch type {
        case .success:
            NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
        case .error, .warning:
            NSHapticFeedbackManager.defaultPerformer.perform(.levelChange, performanceTime: .now)
        }
    }

    enum ImpactStyle { case light, medium, heavy, soft, rigid }
    enum NotifyType { case success, error, warning }
}
