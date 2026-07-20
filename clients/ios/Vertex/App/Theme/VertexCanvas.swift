import SwiftUI

/// ZStack-based canvas modifiers. We use ZStack rather than `.background(Color)`
/// because on iOS 26 the latter triggers automatic Liquid Glass tinting on
/// scrollable containers — visible as a brighter navy haze. ZStack puts the
/// color as a sibling layer, which iOS leaves alone.

/// Marquee canvas (RootView / ConnectScreen). Same `bgCanvas` as inner
/// screens — keeps the dark/light contrast consistent: surface cards in
/// `bgSurface` (ServerCard, Settings sections) read as elevated plates on
/// a uniformly darker canvas across the whole app.
struct VertexCanvas: ViewModifier {
    func body(content: Content) -> some View {
        ZStack {
            Color.bgCanvas.ignoresSafeArea()
            content
        }
        .preferredColorScheme(.dark)
    }
}

/// Inner-screen canvas (Settings / IdentityKey / About / Pickers). Darker
/// `bgCanvas` so that `bgSurface`-filled section cards read as elevated.
struct VertexInnerCanvas: ViewModifier {
    func body(content: Content) -> some View {
        ZStack {
            Color.bgCanvas.ignoresSafeArea()
            content
        }
        .preferredColorScheme(.dark)
    }
}

extension View {
    /// Apply the marquee canvas (`bgSurface` flat deep-navy).
    func vertexCanvas() -> some View {
        modifier(VertexCanvas())
    }

    /// Apply the inner-screen canvas (`bgCanvas`, darker).
    func vertexInnerCanvas() -> some View {
        modifier(VertexInnerCanvas())
    }
}
