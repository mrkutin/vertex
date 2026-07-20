import SwiftUI

/// Compact protocol chip used in Settings → Active Configuration.
/// Caller must pass the label already uppercased (e.g. "MQTTS", "WSS").
struct VxProtocolBadge: View {
    let label: String
    let isPrimary: Bool

    var body: some View {
        Text(label)
            .font(.vxCaptionMono)
            .foregroundStyle(isPrimary ? Color.accentPrimary : Color.textTertiary)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(Color.bgSurfaceMuted, in: .rect(cornerRadius: 4))
    }
}
