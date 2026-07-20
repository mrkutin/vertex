import VertexCore
import SwiftUI

/// Bytes-up / bytes-down stat row — see UI-SPEC §6.4.
struct StatRowView: View {
    let stats: TunnelStats

    var body: some View {
        HStack(spacing: VxSpace.s6) {
            stat(icon: "arrow.up", label: "Sent", value: format(stats.bytesUp))
            Rectangle()
                .fill(Color.borderSubtle)
                .frame(width: 0.5, height: 28)
            stat(icon: "arrow.down", label: "Received", value: format(stats.bytesDown))
        }
        .padding(.vertical, 14)
        .padding(.horizontal, VxSpace.s4)
        .frame(maxWidth: .infinity)
        .background(Color.bgSurface, in: .rect(cornerRadius: VxRadius.lg))
        .overlay(
            RoundedRectangle(cornerRadius: VxRadius.lg)
                .strokeBorder(Color.borderSubtle, lineWidth: 0.5)
        )
    }

    private func stat(icon: String, label: String, value: String) -> some View {
        HStack(spacing: VxSpace.s2) {
            ZStack {
                Circle()
                    .fill(Color.accentPrimaryMuted)
                    .frame(width: 28, height: 28)
                Image(systemName: icon)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Color.accentPrimary)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(label)
                    .font(.vxCaption)
                    .foregroundStyle(Color.textSecondary)
                Text(value)
                    .font(.statValue)
                    .foregroundStyle(Color.textPrimary)
                    .contentTransition(.numericText())
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func format(_ bytes: UInt64) -> String {
        let bcf = ByteCountFormatter()
        bcf.allowedUnits = [.useKB, .useMB, .useGB]
        bcf.countStyle = .binary
        return bcf.string(fromByteCount: Int64(bytes))
    }
}
