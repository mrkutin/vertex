@preconcurrency import NetworkExtension
import SwiftUI

/// The marquee hero for ConnectScreen — see UI-SPEC §5 + VertexHero v2.
///
/// Renders the V-asterisk geometry from the app icon with state-keyed glow,
/// node activation during connecting, gentle "breath" while connected, edge
/// shimmer driven by upload/download rates, and a shake on error. Single
/// Canvas + TimelineView keeps allocations low.
struct VertexHero: View {
    let status: NEVPNStatus

    /// Bytes/sec uploaded — drives endpoint A glow & edge A shimmer.
    var uploadRateBps: Double = 0

    /// Bytes/sec downloaded — drives endpoint B glow & edge B shimmer.
    var downloadRateBps: Double = 0

    /// Optional content size in points (the inner V canvas, before halo padding).
    var contentSize: CGFloat = 220

    /// Optional padding around the canvas to allow blur to spill out.
    var haloPadding: CGFloat = 24

    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    @State private var shakeOffset: CGFloat = 0
    @State private var lastErrorTrigger: UUID = UUID()

    /// Smoothed (animated) rate scalars in [0,1] driven by onChange handlers.
    @State private var rUpDisplayed: Double = 0
    @State private var rDnDisplayed: Double = 0

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30.0, paused: shouldPause)) { ctx in
            Canvas(opaque: false) { gc, size in
                draw(
                    into: &gc,
                    size: size,
                    time: ctx.date.timeIntervalSinceReferenceDate,
                    rUp: rUpDisplayed,
                    rDn: rDnDisplayed
                )
            }
            .frame(width: contentSize, height: contentSize)
            .padding(haloPadding)
            .offset(x: shakeOffset)
        }
        .accessibilityElement()
        .accessibilityLabel("Vertex status")
        .accessibilityValue(a11yValue)
        .accessibilityAddTraits(isAnimating ? .updatesFrequently : [])
        .onChange(of: status) { _, newStatus in
            if newStatus == .invalid {
                triggerErrorShake()
            }
        }
        .onChange(of: uploadRateBps) { _, newValue in
            updateUploadRate(newValue)
        }
        .onChange(of: downloadRateBps) { _, newValue in
            updateDownloadRate(newValue)
        }
        .onAppear {
            // Initialize displayed rates without animation on first appearance.
            rUpDisplayed = quantizedIntensity(forBps: uploadRateBps)
            rDnDisplayed = quantizedIntensity(forBps: downloadRateBps)
        }
    }

    // MARK: - Pause logic

    private var shouldPause: Bool {
        if reduceMotion { return true }
        switch status {
        case .disconnected, .invalid: return true
        case .connected: return false
        default: return false
        }
    }

    private var isAnimating: Bool {
        status == .connecting || status == .reasserting
    }

    private var a11yValue: String {
        switch status {
        case .connected: "Connected. Network is active."
        case .connecting: "Connecting."
        case .reasserting: "Reconnecting."
        case .disconnecting: "Disconnecting."
        case .disconnected: "Not connected."
        case .invalid: "Configuration error."
        @unknown default: "Unknown."
        }
    }

    // MARK: - Rate updates

    private func updateUploadRate(_ bps: Double) {
        let target = quantizedIntensity(forBps: bps)
        // Reduce Motion: brightness cross-fade allowed at 600ms (no motion).
        // Default: 400ms ease-in-out smoothing.
        let duration: Double = reduceMotion ? 0.6 : 0.4
        withAnimation(.easeInOut(duration: duration)) {
            rUpDisplayed = target
        }
    }

    private func updateDownloadRate(_ bps: Double) {
        let target = quantizedIntensity(forBps: bps)
        let duration: Double = reduceMotion ? 0.6 : 0.4
        withAnimation(.easeInOut(duration: duration)) {
            rDnDisplayed = target
        }
    }

    /// Returns the rate scalar — continuous when reduceMotion is off, otherwise
    /// a 3-step ladder per the Reduce Motion fallback (§5).
    private func quantizedIntensity(forBps bps: Double) -> Double {
        if reduceMotion {
            // Step ladder maps to scalars that produce the spec's intensity/glow.
            // Step 0 (idle): bps < 50 Kbps → r = 0 → endpoint intensity 0.55, glow 30.1
            // Step 1 (low/mid): 50 Kbps ≤ bps < 5 Mbps → r ≈ 0.5111 → intensity 0.78, glow 37.5
            // Step 2 (high): bps ≥ 5 Mbps → r = 1 → intensity 1.00, glow 45.0
            if bps < 50_000 {
                return 0
            } else if bps < 5_000_000 {
                return 0.5111   // (0.78 - 0.55) / 0.45
            } else {
                return 1.0
            }
        }
        return RateMap.intensity(forBps: bps)
    }

    // MARK: - Error shake

    private func triggerErrorShake() {
        guard !reduceMotion else { return }
        let trigger = UUID()
        lastErrorTrigger = trigger
        Task { @MainActor in
            for cycle in 0..<3 {
                let dx: CGFloat = (cycle % 2 == 0) ? 6 : -6
                withAnimation(.spring(response: 0.32, dampingFraction: 0.55)) {
                    shakeOffset = dx
                }
                try? await Task.sleep(nanoseconds: 90_000_000)
                if lastErrorTrigger != trigger { return }
            }
            withAnimation(.spring(response: 0.32, dampingFraction: 0.55)) {
                shakeOffset = 0
            }
        }
    }

    // MARK: - Rate mapping

    private struct RateMap {
        static let floorBps: Double =     50_000   //   50 Kbps
        static let ceilBps:  Double = 50_000_000   //   50 Mbps
        static let exponent: Double = 0.85

        /// Returns 0..1. 0 means "below threshold, render as idle".
        static func intensity(forBps rawBps: Double) -> Double {
            let bps = max(0, rawBps)
            guard bps > floorBps else { return 0 }
            let clamped = min(bps, ceilBps)
            let num = log10(1 + clamped / floorBps)
            let den = log10(ceilBps / floorBps)   // ≈ 3.0
            let normalized = num / den
            return pow(min(max(normalized, 0), 1), exponent)
        }
    }

    // MARK: - Geometry (220×220 reference)

    private struct Geom {
        // Nodes
        static let endpointA = CGPoint(x:  60.2, y:  65.3)
        static let endpointB = CGPoint(x: 159.8, y:  65.3)
        static let vertex    = CGPoint(x: 110.0, y: 154.7)

        // Line A: top tail end → bottom tail end (passes through endpointA, vertex)
        static let lineAStart = CGPoint(x:  51.8, y:  50.3)
        static let lineAEnd   = CGPoint(x: 122.5, y: 177.2)

        // Line B: top tail end → bottom tail end (passes through endpointB, vertex)
        static let lineBStart = CGPoint(x: 168.2, y:  50.3)
        static let lineBEnd   = CGPoint(x:  97.5, y: 177.2)

        // Strokes & dots
        static let edgeStrokeFull: CGFloat = 12.0
        static let edgeStrokeIdle: CGFloat =  9.0
        static let endpointRadius: CGFloat = 10.3
        static let vertexRadius:   CGFloat = 15.5

        // Glows
        static let endpointGlowRadius: CGFloat = 30.1
        static let vertexGlowRadius:   CGFloat = 51.6

        // Active-traffic glow expansion ceiling (saturated)
        static let endpointGlowRadiusActive: CGFloat = 45.0
        static let vertexGlowRadiusBreath:   CGFloat = 53.7   // 1.04 × base

        // Active-edge segment (endpoint → vertex sub-path of each line) — used
        // for the rate-driven segment-halo bloom under-stroke.
        static let segAStart = CGPoint(x:  60.2, y:  65.3)   // = endpointA
        static let segAEnd   = CGPoint(x: 110.0, y: 154.7)   // = vertex
        static let segBStart = CGPoint(x: 159.8, y:  65.3)   // = endpointB
        static let segBEnd   = CGPoint(x: 110.0, y: 154.7)   // = vertex
    }

    // MARK: - Drawing

    @MainActor
    private func draw(
        into gc: inout GraphicsContext,
        size: CGSize,
        time: TimeInterval,
        rUp: Double,
        rDn: Double
    ) {
        let scale = size.width / 220.0
        let A = scale * Geom.endpointA
        let B = scale * Geom.endpointB
        let V = scale * Geom.vertex

        let lineAStart = scale * Geom.lineAStart
        let lineAEnd   = scale * Geom.lineAEnd
        let lineBStart = scale * Geom.lineBStart
        let lineBEnd   = scale * Geom.lineBEnd

        let endpointR = Geom.endpointRadius * scale
        let vertexR   = Geom.vertexRadius * scale

        // Per-state targets.
        let targets = stateTargets(time: time, rUp: rUp, rDn: rDn)

        // Layer 1 — ambient halo behind everything.
        if targets.haloIntensity > 0.001 {
            let center = CGPoint(x: size.width / 2, y: size.height / 2)
            let r = size.width * 0.5 * targets.haloScale
            let halo = Path(ellipseIn: CGRect(
                x: center.x - r, y: center.y - r, width: 2 * r, height: 2 * r
            ))
            let grad = Gradient(stops: [
                .init(color: targets.haloColor.opacity(targets.haloIntensity), location: 0),
                .init(color: targets.haloColor.opacity(0), location: 1)
            ])
            gc.fill(
                halo,
                with: .radialGradient(
                    grad,
                    center: center,
                    startRadius: 0,
                    endRadius: r
                )
            )
        }

        // Layers 2 & 3 — endpoint glows (rate-driven radius/alpha cap on connected).
        drawNodeGlow(
            into: &gc, center: A,
            radius: targets.nodeAGlowRadius * scale,
            color: targets.nodeAColor,
            innerAlphaCap: targets.nodeAGlowAlphaCap,
            intensity: targets.nodeAIntensity
        )
        drawNodeGlow(
            into: &gc, center: B,
            radius: targets.nodeBGlowRadius * scale,
            color: targets.nodeBColor,
            innerAlphaCap: targets.nodeBGlowAlphaCap,
            intensity: targets.nodeBIntensity
        )

        // Layer 4 — vertex glow (largest).
        drawNodeGlow(
            into: &gc, center: V,
            radius: targets.vertexGlowRadius * scale,
            color: targets.vertexColor,
            innerAlphaCap: 0.65,
            intensity: targets.vertexIntensity
        )

        // Layers 5 & 6 — edges as full extended through-dot lines.
        let edgeStroke = targets.edgeStroke * scale
        drawEdge(
            into: &gc,
            from: lineAStart, to: lineAEnd,
            color: targets.edgeColor, opacity: targets.edgeOpacity,
            stroke: edgeStroke,
            sweepProgress: targets.edgeSweepA
        )
        drawEdge(
            into: &gc,
            from: lineBStart, to: lineBEnd,
            color: targets.edgeColor, opacity: targets.edgeOpacity,
            stroke: edgeStroke,
            sweepProgress: targets.edgeSweepB
        )

        // Layers 6a & 6b — active-segment halo bloom (endpoint→vertex only).
        // Wide translucent stroke under the moving shimmer; rate-driven.
        drawSegmentHalo(
            into: &gc,
            from: scale * Geom.segAStart, to: scale * Geom.segAEnd,
            rate: rUp,
            baseStroke: edgeStroke,
            color: targets.edgeColor
        )
        drawSegmentHalo(
            into: &gc,
            from: scale * Geom.segBStart, to: scale * Geom.segBEnd,
            rate: rDn,
            baseStroke: edgeStroke,
            color: targets.edgeColor
        )

        // Layers 7 & 8 — edge shimmer (active-traffic only, connected state).
        if let shimmerSpeedA = targets.shimmerASpeed, !reduceMotion {
            drawShimmer(
                into: &gc,
                from: lineAStart, to: lineAEnd,
                rate: rUp,
                speed: shimmerSpeedA,
                time: time,
                stroke: edgeStroke,
                color: targets.edgeColor
            )
        }
        if let shimmerSpeedB = targets.shimmerBSpeed, !reduceMotion {
            // Shimmer B travels bottom-to-top — swap from/to.
            drawShimmer(
                into: &gc,
                from: lineBEnd, to: lineBStart,
                rate: rDn,
                speed: shimmerSpeedB,
                time: time,
                stroke: edgeStroke,
                color: targets.edgeColor
            )
        }

        // Layer 9 — endpoint cores (cover lines at endpoints, "lines pass through dots").
        drawCore(
            into: &gc, center: A, radius: endpointR,
            color: targets.nodeAColor, intensity: targets.nodeAIntensity
        )
        drawCore(
            into: &gc, center: B, radius: endpointR,
            color: targets.nodeBColor, intensity: targets.nodeBIntensity
        )

        // Layer 10 — vertex core ON TOP.
        drawCore(
            into: &gc, center: V, radius: vertexR,
            color: targets.vertexColor, intensity: targets.vertexIntensity
        )
    }

    private func drawNodeGlow(
        into gc: inout GraphicsContext,
        center: CGPoint,
        radius: CGFloat,
        color: Color,
        innerAlphaCap: Double,
        intensity: Double
    ) {
        guard intensity > 0.001, radius > 0.001 else { return }
        let rect = CGRect(
            x: center.x - radius, y: center.y - radius,
            width: 2 * radius, height: 2 * radius
        )
        let grad = Gradient(stops: [
            .init(color: color.opacity(intensity * innerAlphaCap), location: 0),
            .init(color: color.opacity(0), location: 1)
        ])
        gc.fill(
            Path(ellipseIn: rect),
            with: .radialGradient(
                grad,
                center: center,
                startRadius: 0,
                endRadius: radius
            )
        )
    }

    private func drawCore(
        into gc: inout GraphicsContext,
        center: CGPoint,
        radius: CGFloat,
        color: Color,
        intensity: Double
    ) {
        let rect = CGRect(
            x: center.x - radius, y: center.y - radius,
            width: 2 * radius, height: 2 * radius
        )
        gc.fill(
            Path(ellipseIn: rect),
            with: .color(color.opacity(intensity))
        )
    }

    private func drawEdge(
        into gc: inout GraphicsContext,
        from p0: CGPoint, to p1: CGPoint,
        color: Color, opacity: Double,
        stroke: CGFloat,
        sweepProgress: Double?
    ) {
        var path = Path()
        path.move(to: p0)
        path.addLine(to: p1)

        // Base edge stroked as a single straight path; endpoint cores are drawn
        // afterward and naturally cover the line at A/B/V — so the lines read
        // as passing through the dots without manual segmentation.
        gc.stroke(
            path,
            with: .color(color.opacity(opacity)),
            style: StrokeStyle(lineWidth: stroke, lineCap: .round, lineJoin: .round)
        )

        // Optional bright sweep over the FULL extended line during connecting.
        if let t = sweepProgress, t > 0 {
            let trimmed = path.trimmedPath(from: 0, to: CGFloat(min(max(t, 0), 1)))
            gc.stroke(
                trimmed,
                with: .color(Color.white.opacity(0.85 * opacity)),
                style: StrokeStyle(lineWidth: stroke * 0.7, lineCap: .round, lineJoin: .round)
            )
        }
    }

    /// Single moving tapered band per edge (active-traffic shimmer).
    /// Wide translucent under-stroke on the endpoint→vertex segment. The
    /// "bloom tier" that pairs with the moving shimmer ("motion tier") to
    /// emphasize the active edge. Static — only the rate scalar varies it,
    /// and rate updates are already smoothed at the @State level.
    private func drawSegmentHalo(
        into gc: inout GraphicsContext,
        from p0: CGPoint, to p1: CGPoint,
        rate r: Double,
        baseStroke: CGFloat,
        color: Color
    ) {
        guard r > 0.001 else { return }
        var path = Path()
        path.move(to: p0)
        path.addLine(to: p1)
        let width = baseStroke * (1.7 + 0.6 * CGFloat(r))
        let opacity = 0.10 + 0.28 * r
        gc.stroke(
            path,
            with: .color(color.opacity(opacity)),
            style: StrokeStyle(lineWidth: width, lineCap: .round)
        )
    }

    private func drawShimmer(
        into gc: inout GraphicsContext,
        from p0: CGPoint, to p1: CGPoint,
        rate r: Double,
        speed: Double,
        time: TimeInterval,
        stroke: CGFloat,
        color: Color
    ) {
        guard r > 0.001 else { return }
        let u = (time * speed).truncatingRemainder(dividingBy: 1.0)
        let half = 0.09
        let lo = max(0, u - half)
        let hi = min(1, u + half)
        if hi <= lo { return }
        var path = Path()
        path.move(to: p0)
        path.addLine(to: p1)
        let band = path.trimmedPath(from: CGFloat(lo), to: CGFloat(hi))
        gc.stroke(
            band,
            with: .color(color.opacity(0.20 + 0.35 * r)),
            style: StrokeStyle(lineWidth: stroke * 0.55, lineCap: .round)
        )
    }

    // MARK: - State targets

    private struct StateTargets {
        var nodeAColor: Color
        var nodeBColor: Color
        var vertexColor: Color
        var edgeColor: Color

        var nodeAIntensity: Double
        var nodeBIntensity: Double
        var vertexIntensity: Double
        var edgeOpacity: Double

        // Edge stroke width (pre-scaling, pt at 220).
        var edgeStroke: CGFloat

        // Endpoint glow params (rate-driven on connected).
        var nodeAGlowRadius: CGFloat
        var nodeBGlowRadius: CGFloat
        var nodeAGlowAlphaCap: Double
        var nodeBGlowAlphaCap: Double

        // Vertex glow radius (breath on connected).
        var vertexGlowRadius: CGFloat

        // Optional 0..1 progress for the bright sweep on each FULL extended line.
        var edgeSweepA: Double?
        var edgeSweepB: Double?

        // Optional shimmer speeds (Hz traversals/sec) — connected w/ traffic only.
        var shimmerASpeed: Double?
        var shimmerBSpeed: Double?

        var haloColor: Color
        var haloIntensity: Double
        var haloScale: Double
    }

    private func stateTargets(time: TimeInterval, rUp: Double, rDn: Double) -> StateTargets {
        let twoPi = 2.0 * Double.pi
        switch status {
        case .connected:
            // Halo (the round ambient glow under the V) breathes gently — alpha
            // ±0.15 around 0.55, scale ±0.04 around 1.0, period 2.4s. The breath
            // is intentionally limited to the halo: nodes, edges, and vertex
            // glow all stay static so traffic activity remains the only signal
            // that pulses on those elements.
            let phase = sin(time * (twoPi / VxMotion.heroBreathPeriod))
            let breathAlpha = reduceMotion ? 0.55 : 0.55 + 0.15 * phase
            let breathScale = reduceMotion ? 1.0  : 1.0  + 0.04 * phase

            // Endpoint intensity & glow are rate-driven only. Upload drives A
            // (left), download drives B (right). Direction-specific.
            let nodeAIntensity = 0.55 + 0.45 * rUp
            let nodeBIntensity = 0.55 + 0.45 * rDn
            let nodeAGlowRadius = Geom.endpointGlowRadius + 14.9 * CGFloat(rUp)
            let nodeBGlowRadius = Geom.endpointGlowRadius + 14.9 * CGFloat(rDn)
            let nodeAGlowAlphaCap = 0.45 + 0.30 * rUp
            let nodeBGlowAlphaCap = 0.45 + 0.30 * rDn

            // Vertex glow — static, slightly smaller than the spec default for a
            // more restrained center.
            let vertexGlowR: CGFloat = 38.0

            // Shimmer is direction-specific and only appears when there is real
            // traffic on that side. Left line shimmers on upload; right on download.
            let shimmerA: Double? = (rUp > 0.001 && !reduceMotion) ? (0.6 + 1.4 * rUp) : nil
            let shimmerB: Double? = (rDn > 0.001 && !reduceMotion) ? (0.6 + 1.4 * rDn) : nil

            return StateTargets(
                nodeAColor: .white,
                nodeBColor: .white,
                vertexColor: .white,
                edgeColor: .white,
                nodeAIntensity: nodeAIntensity,
                nodeBIntensity: nodeBIntensity,
                vertexIntensity: 1.0,
                edgeOpacity: 1.0,
                edgeStroke: Geom.edgeStrokeFull,
                nodeAGlowRadius: nodeAGlowRadius,
                nodeBGlowRadius: nodeBGlowRadius,
                nodeAGlowAlphaCap: nodeAGlowAlphaCap,
                nodeBGlowAlphaCap: nodeBGlowAlphaCap,
                vertexGlowRadius: vertexGlowR,
                edgeSweepA: nil,
                edgeSweepB: nil,
                shimmerASpeed: shimmerA,
                shimmerBSpeed: shimmerB,
                haloColor: .stateConnectedGlow,
                haloIntensity: 0.6 * breathAlpha,
                haloScale: breathScale
            )

        case .connecting:
            // Three-node sequence: A → B → V over 0.9s/node.
            let cycle = VxMotion.heroPulsePeriod * 3.0
            let cyclePos = time.truncatingRemainder(dividingBy: cycle) / VxMotion.heroPulsePeriod
            let active = Int(cyclePos.rounded(.down)) % 3
            let pulse = 0.55 + 0.45 * sin(time * (twoPi / VxMotion.heroPulsePeriod))

            // In Reduce Motion: replace cycle with all-three-at-0.7.
            let aInt = reduceMotion ? 0.7 : (active == 0 ? pulse : 0.45)
            let bInt = reduceMotion ? 0.7 : (active == 1 ? pulse : 0.45)
            let vInt = reduceMotion ? 0.7 : (active == 2 ? pulse : 0.55)

            // Sweep traverses the FULL extended line of currently active edge.
            let sweepFrac = cyclePos.truncatingRemainder(dividingBy: 1.0)
            let sweepA: Double? = (active == 0 && !reduceMotion) ? sweepFrac : nil
            let sweepB: Double? = (active == 1 && !reduceMotion) ? sweepFrac : nil

            // Connecting halo breath = 0.7 + 0.3·sin(t·2π/heroPulsePeriod)
            let connectingBreath = reduceMotion
                ? 0.85
                : 0.7 + 0.3 * sin(time * (twoPi / VxMotion.heroPulsePeriod))

            return StateTargets(
                nodeAColor: .stateTransitioning,
                nodeBColor: .stateTransitioning,
                vertexColor: .stateTransitioning,
                edgeColor: .stateTransitioning,
                nodeAIntensity: aInt,
                nodeBIntensity: bInt,
                vertexIntensity: vInt,
                edgeOpacity: 0.65,
                edgeStroke: Geom.edgeStrokeFull,
                nodeAGlowRadius: Geom.endpointGlowRadius,
                nodeBGlowRadius: Geom.endpointGlowRadius,
                nodeAGlowAlphaCap: 0.55,
                nodeBGlowAlphaCap: 0.55,
                vertexGlowRadius: Geom.vertexGlowRadius,
                edgeSweepA: sweepA,
                edgeSweepB: sweepB,
                shimmerASpeed: nil,
                shimmerBSpeed: nil,
                haloColor: .stateTransitioningGlow,
                haloIntensity: 0.4 * connectingBreath,
                haloScale: 1.0
            )

        case .reasserting:
            // Slower, less anxious cycle; no sweep.
            let cycle = VxMotion.heroReassertPeriod * 3.0
            let cyclePos = time.truncatingRemainder(dividingBy: cycle) / VxMotion.heroReassertPeriod
            let active = Int(cyclePos.rounded(.down)) % 3
            let pulse = 0.55 + 0.35 * sin(time * (twoPi / VxMotion.heroReassertPeriod))
            let aInt = reduceMotion ? 0.7 : (active == 0 ? pulse : 0.45)
            let bInt = reduceMotion ? 0.7 : (active == 1 ? pulse : 0.45)
            let vInt = reduceMotion ? 0.7 : (active == 2 ? pulse : 0.55)
            return StateTargets(
                nodeAColor: .stateTransitioning,
                nodeBColor: .stateTransitioning,
                vertexColor: .stateTransitioning,
                edgeColor: .stateTransitioning,
                nodeAIntensity: aInt,
                nodeBIntensity: bInt,
                vertexIntensity: vInt,
                edgeOpacity: 0.6,
                edgeStroke: Geom.edgeStrokeFull,
                nodeAGlowRadius: Geom.endpointGlowRadius,
                nodeBGlowRadius: Geom.endpointGlowRadius,
                nodeAGlowAlphaCap: 0.55,
                nodeBGlowAlphaCap: 0.55,
                vertexGlowRadius: Geom.vertexGlowRadius,
                edgeSweepA: nil,
                edgeSweepB: nil,
                shimmerASpeed: nil,
                shimmerBSpeed: nil,
                haloColor: .stateTransitioningGlow,
                haloIntensity: 0.3,
                haloScale: 1.0
            )

        case .disconnecting:
            // One-shot fade-down: drive intensity by a slow decay (existing 1.2s sin).
            let pulse = 0.7 + 0.1 * sin(time * (twoPi / 1.2))
            return StateTargets(
                nodeAColor: .white,
                nodeBColor: .white,
                vertexColor: .white,
                edgeColor: .white,
                nodeAIntensity: 0.6 * pulse,
                nodeBIntensity: 0.6 * pulse,
                vertexIntensity: 0.7 * pulse,
                edgeOpacity: 0.55,
                edgeStroke: Geom.edgeStrokeFull,
                nodeAGlowRadius: Geom.endpointGlowRadius,
                nodeBGlowRadius: Geom.endpointGlowRadius,
                nodeAGlowAlphaCap: 0.55,
                nodeBGlowAlphaCap: 0.55,
                vertexGlowRadius: Geom.vertexGlowRadius,
                edgeSweepA: nil,
                edgeSweepB: nil,
                shimmerASpeed: nil,
                shimmerBSpeed: nil,
                haloColor: .stateConnectedGlow,
                haloIntensity: 0.25,
                haloScale: 1.0
            )

        case .disconnected:
            return StateTargets(
                nodeAColor: .glyphDim,
                nodeBColor: .glyphDim,
                vertexColor: .glyphDim,
                edgeColor: .glyphDim,
                nodeAIntensity: 1.0,
                nodeBIntensity: 1.0,
                vertexIntensity: 1.0,
                edgeOpacity: 0.85,
                edgeStroke: Geom.edgeStrokeIdle,
                nodeAGlowRadius: Geom.endpointGlowRadius,
                nodeBGlowRadius: Geom.endpointGlowRadius,
                nodeAGlowAlphaCap: 0.55,
                nodeBGlowAlphaCap: 0.55,
                vertexGlowRadius: Geom.vertexGlowRadius,
                edgeSweepA: nil,
                edgeSweepB: nil,
                shimmerASpeed: nil,
                shimmerBSpeed: nil,
                haloColor: .clear,
                haloIntensity: 0,
                haloScale: 1.0
            )

        case .invalid:
            return StateTargets(
                nodeAColor: .stateError,
                nodeBColor: .stateError,
                vertexColor: .stateError,
                edgeColor: .stateError,
                nodeAIntensity: 1.0,
                nodeBIntensity: 1.0,
                vertexIntensity: 1.0,
                edgeOpacity: 0.95,
                edgeStroke: Geom.edgeStrokeFull,
                nodeAGlowRadius: Geom.endpointGlowRadius,
                nodeBGlowRadius: Geom.endpointGlowRadius,
                nodeAGlowAlphaCap: 0.55,
                nodeBGlowAlphaCap: 0.55,
                vertexGlowRadius: Geom.vertexGlowRadius,
                edgeSweepA: nil,
                edgeSweepB: nil,
                shimmerASpeed: nil,
                shimmerBSpeed: nil,
                haloColor: .stateErrorGlow,
                haloIntensity: 0.55,
                haloScale: 1.0
            )

        @unknown default:
            return stateTargets(for: .disconnected, time: time, rUp: rUp, rDn: rDn)
        }
    }

    private func stateTargets(for status: NEVPNStatus, time: TimeInterval, rUp: Double, rDn: Double) -> StateTargets {
        let copy = VertexHero(
            status: status,
            uploadRateBps: 0,
            downloadRateBps: 0,
            contentSize: contentSize,
            haloPadding: haloPadding
        )
        return copy.stateTargets(time: time, rUp: rUp, rDn: rDn)
    }
}

private func * (lhs: CGFloat, rhs: CGPoint) -> CGPoint {
    CGPoint(x: lhs * rhs.x, y: lhs * rhs.y)
}

#Preview {
    VStack(spacing: 24) {
        VertexHero(status: .connected, uploadRateBps: 2_000_000, downloadRateBps: 20_000_000)
        VertexHero(status: .connecting)
        VertexHero(status: .disconnected)
    }
    .padding()
    .background(Color.bgCanvas.ignoresSafeArea())
}
