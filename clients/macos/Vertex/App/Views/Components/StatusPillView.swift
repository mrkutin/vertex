import NetworkExtension
import SwiftUI

/// Status pill — capsule with state-keyed dot, IP, and uptime.
/// See UI-SPEC §6.3.
struct StatusPillView: View {
    let status: NEVPNStatus
    let assignedIP: String?
    let connectedSince: Date?

    var body: some View {
        // TimelineView ticks once per second, paused automatically when the app is
        // backgrounded and resumed when it returns — no Combine timer state to leak.
        TimelineView(.periodic(from: .now, by: 1)) { context in
            HStack(spacing: 8) {
                ZStack {
                    if status == .connected {
                        Circle()
                            .fill(Color.glowPrimary)
                            .frame(width: 14, height: 14)
                            .blur(radius: 4)
                    }
                    Circle()
                        .frame(width: 6, height: 6)
                        .foregroundStyle(dotColor)
                }
                .frame(width: 14, height: 14)
                Text(label(now: context.date))
                    .font(.vxCallout.monospacedDigit())
                    .foregroundStyle(Color.textPrimary)
                    .contentTransition(.opacity)
            }
            .padding(.horizontal, VxSpace.s4 - 2) // 14
            .padding(.vertical, 7)
            .background(.thinMaterial, in: .capsule)
            .overlay(
                Capsule()
                    .strokeBorder(Color.borderSubtle, lineWidth: 0.5)
            )
            .environment(\.colorScheme, .dark)
        }
    }

    private func label(now: Date) -> String {
        switch status {
        case .connected:
            if let ip = assignedIP, let since = connectedSince {
                return "\(ip)  ·  \(uptimeString(from: since, now: now))"
            }
            if let ip = assignedIP { return ip }
            return "Connected"
        case .connecting: return "Connecting…"
        case .reasserting: return "Reconnecting…"
        case .disconnecting: return "Disconnecting…"
        case .disconnected: return "Not connected"
        case .invalid: return "Not configured"
        @unknown default: return "Unknown"
        }
    }

    private var dotColor: Color {
        switch status {
        case .connected: .stateConnected
        case .connecting, .reasserting, .disconnecting: .stateTransitioning
        case .disconnected: .stateDormant
        case .invalid: .stateError
        @unknown default: .stateDormant
        }
    }

    private func uptimeString(from start: Date, now: Date) -> String {
        let total = max(0, Int(now.timeIntervalSince(start)))
        let h = total / 3600
        let m = (total % 3600) / 60
        let s = total % 60
        if h > 0 { return String(format: "%d:%02d:%02d", h, m, s) }
        return String(format: "%d:%02d", m, s)
    }
}
