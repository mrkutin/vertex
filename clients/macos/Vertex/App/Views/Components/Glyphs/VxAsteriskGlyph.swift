import SwiftUI

/// Mini V-asterisk glyph for "Vertex" rows — see UI-SPEC §8.1.
///
/// Two strokes from (6, 8) → (16.5, 22) and (18, 8) → (7.5, 22) in a 24×24 box,
/// stroke width 2, line cap round. Three small filled circles mark endpoints
/// and the meeting vertex. All in `accent.primary`.
struct VxAsteriskGlyph: View {
    var size: CGFloat = 20
    var color: Color = .accentPrimary

    var body: some View {
        Canvas { ctx, canvasSize in
            let s = canvasSize.width / 24.0

            // Strokes
            var pathA = Path()
            pathA.move(to: CGPoint(x: 6 * s, y: 8 * s))
            pathA.addLine(to: CGPoint(x: 16.5 * s, y: 22 * s))

            var pathB = Path()
            pathB.move(to: CGPoint(x: 18 * s, y: 8 * s))
            pathB.addLine(to: CGPoint(x: 7.5 * s, y: 22 * s))

            let stroke = StrokeStyle(lineWidth: 2 * s, lineCap: .round, lineJoin: .round)
            ctx.stroke(pathA, with: .color(color), style: stroke)
            ctx.stroke(pathB, with: .color(color), style: stroke)

            // Endpoint dots (radius 1.6) and vertex dot (radius 2.0)
            // Vertex is the exact intersection of the two strokes — solving
            // line A (6,8)→(16.5,22) with line B (18,8)→(7.5,22) gives (12, 16).
            let endpoints: [(CGFloat, CGFloat, CGFloat)] = [
                (6, 8, 1.6),
                (18, 8, 1.6),
                (12, 16, 2.0)
            ]
            for (cx, cy, r) in endpoints {
                let rect = CGRect(
                    x: (cx - r) * s, y: (cy - r) * s,
                    width: 2 * r * s, height: 2 * r * s
                )
                ctx.fill(Path(ellipseIn: rect), with: .color(color))
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

#Preview {
    HStack(spacing: 16) {
        VxAsteriskGlyph()
        VxAsteriskGlyph(size: 32)
        VxAsteriskGlyph(size: 48)
    }
    .padding()
    .background(Color.bgCanvas)
}
