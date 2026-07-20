import CoreGraphics

/// Vertex corner-radius scale — see design/UI-SPEC.md §3.2.
enum VxRadius {
    static let sm: CGFloat = 8
    static let md: CGFloat = 12
    static let lg: CGFloat = 16
    static let xl: CGFloat = 22
    static let capsule: CGFloat = 999
    /// Notional bounding for hero glow blur (used internally for sizing).
    static let heroOuter: CGFloat = 96
}
