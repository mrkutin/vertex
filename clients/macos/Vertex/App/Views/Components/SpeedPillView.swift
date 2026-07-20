import SwiftUI

/// Compact capsule with current upload/download rate — sibling of `StatusPillView`,
/// rendered just below it on `ConnectScreen` while connected.
///
/// Rates come from `TunnelViewModel.uploadRate / downloadRate` — bytes/sec
/// computed as a rolling average over a 3s window (see TunnelViewModel
/// `instantRate` / `statsHistory`).
struct SpeedPillView: View {
    let uploadRate: Double      // bytes/sec
    let downloadRate: Double    // bytes/sec
    let pingMs: Int?            // end-to-end RTT through tunnel, nil = not measured yet

    var body: some View {
        HStack(spacing: VxSpace.s3) {
            HStack(spacing: 4) {
                Image(systemName: "arrow.up")
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(Color.accentPrimary)
                Text(formatRate(uploadRate))
                    .font(.vxCallout.monospacedDigit())
                    .foregroundStyle(Color.textPrimary)
                    .contentTransition(.numericText())
            }
            Rectangle()
                .fill(Color.borderSubtle)
                .frame(width: 0.5, height: 12)
            HStack(spacing: 4) {
                Image(systemName: "arrow.down")
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(Color.accentPrimary)
                Text(formatRate(downloadRate))
                    .font(.vxCallout.monospacedDigit())
                    .foregroundStyle(Color.textPrimary)
                    .contentTransition(.numericText())
            }
            Rectangle()
                .fill(Color.borderSubtle)
                .frame(width: 0.5, height: 12)
            HStack(spacing: 4) {
                Image(systemName: "timer")
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(pingIconColor)
                Text(formatPing(pingMs))
                    .font(.vxCallout.monospacedDigit())
                    .foregroundStyle(Color.textPrimary)
                    .contentTransition(.numericText())
            }
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

    private var pingIconColor: Color {
        guard let p = pingMs else { return Color.textTertiary }
        switch p {
        case ..<80: return Color.stateConnected
        case ..<200: return Color.accentPrimary
        default: return Color.stateTransitioning
        }
    }

    private func formatPing(_ ms: Int?) -> String {
        guard let ms else { return "—" }
        return "\(ms) ms"
    }

    /// Bytes-per-second → bits-per-second formatted as Kbps/Mbps/Gbps
    /// (decimal SI prefixes — convention for network speeds, matches Speedtest
    /// and ISP advertised throughput). Returns "—" below 1 Kbps to avoid noise.
    private func formatRate(_ bytesPerSec: Double) -> String {
        let bitsPerSec = bytesPerSec * 8
        guard bitsPerSec >= 1_000 else { return "—" }
        if bitsPerSec >= 1_000_000_000 {
            return String(format: "%.1f Gbps", bitsPerSec / 1_000_000_000)
        }
        if bitsPerSec >= 1_000_000 {
            return String(format: "%.1f Mbps", bitsPerSec / 1_000_000)
        }
        return String(format: "%.0f Kbps", bitsPerSec / 1_000)
    }
}
