import SwiftUI

/// Primary action button — see UI-SPEC §6.1.
struct BigConnectButton: View {
    let isConnected: Bool
    let isTransitioning: Bool
    let action: () -> Void

    @State private var pressed = false
    @State private var glowPhase: Double = 0
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Button {
            Haptics.impact(.medium)
            action()
        } label: {
            HStack(spacing: 10) {
                if isTransitioning {
                    ProgressView()
                        .controlSize(.small)
                        .tint(Color.accentPrimary)
                }
                Text(title)
                    .font(.system(size: 18, weight: .semibold, design: .rounded))
                    .foregroundStyle(textColor)
            }
            .frame(maxWidth: .infinity, minHeight: 56)
            .padding(.horizontal, VxSpace.s6)
            .background {
                ZStack {
                    if showsGlow {
                        Capsule()
                            .fill(Color.accentPrimary)
                            .blur(radius: 24)
                            .opacity(glowOpacity)
                            .padding(-8)
                    }
                    Capsule().fill(fillColor)
                }
            }
            .clipShape(.capsule)
            .contentShape(.capsule)
        }
        .buttonStyle(.plain)
        .scaleEffect(pressed ? 0.97 : 1.0)
        .animation(VxMotion.buttonPressSpring, value: pressed)
        .onLongPressGesture(
            minimumDuration: 0,
            maximumDistance: .infinity,
            pressing: { pressed = $0 },
            perform: {}
        )
        .onAppear {
            guard !reduceMotion else { return }
            withAnimation(VxMotion.buttonGlow) {
                glowPhase = 1
            }
        }
    }

    private var title: String {
        if isTransitioning { return "Cancel" }
        return isConnected ? "Disconnect" : "Connect"
    }

    private var fillColor: Color {
        if isTransitioning { return .accentPrimaryMuted }
        if isConnected { return .bgSurfaceMuted }
        return .accentPrimary
    }

    private var textColor: Color {
        if isTransitioning { return .textPrimary }
        if isConnected { return .stateError }
        return .textOnAccent
    }

    private var showsGlow: Bool {
        // Spec: glow only when idle (Connect state).
        !isConnected && !isTransitioning
    }

    private var glowOpacity: Double {
        guard showsGlow else { return 0 }
        if reduceMotion { return 0.8 }
        // Pulse 60% ↔ 100%.
        return 0.6 + 0.4 * glowPhase
    }
}
