import SwiftUI

/// Selected-row picker mark — a brand-coherent V-asterisk with a soft halo.
/// See UI-SPEC §8.1.
struct VxSelectionGlyph: View {
    var size: CGFloat = 20

    var body: some View {
        ZStack {
            // Halo: 28pt frame, blurred accent fill at 35%.
            Circle()
                .fill(Color.accentPrimary)
                .frame(width: size + 12, height: size + 12)
                .blur(radius: 6)
                .opacity(0.35)
            // Solid V-asterisk in accent.
            VxAsteriskGlyph(size: size, color: .accentPrimary)
        }
        .frame(width: size + 14, height: size + 14)
        .accessibilityHidden(true)
    }
}

#Preview {
    HStack(spacing: 16) {
        VxSelectionGlyph()
        VxSelectionGlyph(size: 28)
    }
    .padding()
    .background(Color.bgCanvas)
}
