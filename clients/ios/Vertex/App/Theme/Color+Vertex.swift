import SwiftUI

// Vertex design tokens — see design/UI-SPEC.md §1.
// Color set names are flat camelCase (e.g. "bgCanvas") to avoid Asset Catalog
// namespace pitfalls; the spec's dotted names map 1:1 onto these symbols.
extension Color {
    // Surfaces & background
    static let bgCanvas         = Color("bgCanvas")
    static let bgCanvasTop      = Color("bgCanvasTop")
    static let bgCanvasBottom   = Color("bgCanvasBottom")
    static let bgSurface        = Color("bgSurface")
    static let bgSurfaceElev    = Color("bgSurfaceElev")
    static let bgSurfaceMuted   = Color("bgSurfaceMuted")
    static let borderSubtle     = Color("borderSubtle")
    static let borderStrong     = Color("borderStrong")

    // Accent & glow
    static let accentPrimary       = Color("accentPrimary")
    static let accentPrimaryHover  = Color("accentPrimaryHover")
    static let accentPrimaryMuted  = Color("accentPrimaryMuted")
    static let glowPrimary         = Color("glowPrimary")
    static let glowCoreHot         = Color("glowCoreHot")
    static let glowWarm            = Color("glowWarm")

    // Text & glyphs
    static let textPrimary         = Color("textPrimary")
    static let textSecondary       = Color("textSecondary")
    static let textTertiary        = Color("textTertiary")
    static let textOnAccent        = Color("textOnAccent")
    static let glyphPrimary        = Color("glyphPrimary")
    static let glyphDim            = Color("glyphDim")

    // State
    static let stateConnected         = Color("stateConnected")
    static let stateConnectedGlow     = Color("stateConnectedGlow")
    static let stateTransitioning     = Color("stateTransitioning")
    static let stateTransitioningGlow = Color("stateTransitioningGlow")
    static let stateDormant           = Color("stateDormant")
    static let stateError             = Color("stateError")
    static let stateErrorGlow         = Color("stateErrorGlow")
}
