import SwiftUI

/// Single ascending edge glyph for "Edge" rows — see UI-SPEC §8.1.
///
/// One stroke from (4, 18) → (20, 6) in a 24×24 box, stroke width 2, line cap round.
/// One filled circle at (20, 6) radius 2.4 marks the destination "node".
struct VxEdgeGlyph: View {
    var size: CGFloat = 20
    var color: Color = .accentPrimary

    var body: some View {
        Canvas { ctx, canvasSize in
            let s = canvasSize.width / 24.0

            var path = Path()
            path.move(to: CGPoint(x: 4 * s, y: 18 * s))
            path.addLine(to: CGPoint(x: 20 * s, y: 6 * s))
            ctx.stroke(
                path,
                with: .color(color),
                style: StrokeStyle(lineWidth: 2 * s, lineCap: .round, lineJoin: .round)
            )

            // Destination dot (radius 2.4)
            let r: CGFloat = 2.4
            let cx: CGFloat = 20
            let cy: CGFloat = 6
            let rect = CGRect(
                x: (cx - r) * s, y: (cy - r) * s,
                width: 2 * r * s, height: 2 * r * s
            )
            ctx.fill(Path(ellipseIn: rect), with: .color(color))
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

#Preview {
    HStack(spacing: 16) {
        VxEdgeGlyph()
        VxEdgeGlyph(size: 32)
        VxEdgeGlyph(size: 48)
    }
    .padding()
    .background(Color.bgCanvas)
}
