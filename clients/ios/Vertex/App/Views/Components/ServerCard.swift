import VertexCore
import SwiftUI

/// Vertex/Edge selector card — see UI-SPEC §6.2.
struct ServerCard: View {
    let brokerURL: String
    let exit: String
    let availableBrokers: [String]
    let availableExits: [String]
    /// Per-exit display strings from TXT records on SRV targets, keyed by
    /// exit ID. Missing entry → fallback to uppercased ID.
    var exitDisplayNames: [String: String] = [:]
    /// When `exit == "auto"` and the extension has resolved it to a
    /// concrete edge (e.g. "sto"), surface the resolved ID so the card
    /// reads "Auto · STO" instead of just "Auto". Nil while disconnected
    /// or while the extension is still resolving.
    var resolvedExit: String? = nil
    /// When `brokerURL == "auto"` and the extension has connected to a
    /// concrete vertex, surface the resolved broker host so the card
    /// reads "Auto · YC". Nil while disconnected.
    var resolvedBrokerHost: String? = nil
    let isDisabled: Bool
    let onBrokerTap: () -> Void
    let onExitTap: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            row(
                glyph: VxAsteriskGlyph(size: 22),
                title: "Vertex",
                value: vertexDisplayName,
                accessory: vertexCode,
                action: onBrokerTap
            )
            Rectangle()
                .fill(Color.borderSubtle)
                .frame(height: 0.5)
                .padding(.leading, 56)
            row(
                glyph: VxEdgeGlyph(size: 22),
                title: "Edge",
                value: edgeDisplayName,
                accessory: edgeCode,
                action: onExitTap
            )
        }
        .background(Color.bgSurface, in: .rect(cornerRadius: VxRadius.lg))
        .overlay(
            RoundedRectangle(cornerRadius: VxRadius.lg)
                .strokeBorder(Color.borderSubtle, lineWidth: 0.5)
        )
        .opacity(isDisabled ? 0.55 : 1.0)
    }

    @ViewBuilder
    private func row<G: View>(glyph: G, title: String, value: String, accessory: String?, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 14) {
                glyph
                    .frame(width: 28)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.vxSubheadline)
                        .foregroundStyle(Color.textSecondary)
                    Text(value)
                        .font(.vxBody.weight(.medium))
                        .foregroundStyle(Color.textPrimary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                Spacer()
                if let accessory {
                    Text(accessory)
                        .font(.vxCaptionMono)
                        .foregroundStyle(Color.textTertiary)
                }
                Image(systemName: "chevron.right")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(Color.textTertiary)
            }
            .padding(.horizontal, VxSpace.s4)
            .padding(.vertical, 14)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .disabled(isDisabled)
    }

    private var brokerHost: String {
        guard let c = URLComponents(string: brokerURL), let host = c.host else { return brokerURL }
        return host
    }

    private var isAutoBroker: Bool { brokerURL == "auto" }

    private var vertexDisplayName: String {
        if isAutoBroker {
            if let resolved = resolvedBrokerHost, !resolved.isEmpty {
                return "Auto \u{00B7} \(NodeLabels.vertexLabel(host: resolved, index: 0).shortName)"
            }
            return "Auto"
        }
        return brokerHost
    }

    private var vertexCode: String? {
        if isAutoBroker { return nil }
        let unique = NodeLabels.uniqueHosts(in: availableBrokers)
        let index = unique.firstIndex(of: brokerHost) ?? 0
        return NodeLabels.vertexLabel(host: brokerHost, index: index).code
    }

    private var edgeDisplayName: String {
        if exit == "auto" {
            if let resolved = resolvedExit, !resolved.isEmpty, resolved != "auto" {
                return "Auto \u{00B7} \(resolved.uppercased())"
            }
            return "Auto"
        }
        return NodeLabels.edgeLabel(exit, index: 0, displayOverride: exitDisplayNames[exit]).display
    }

    private var edgeCode: String? {
        if exit == "auto" { return nil }
        let index = availableExits.firstIndex(of: exit) ?? 0
        return NodeLabels.edgeLabel(exit, index: index, displayOverride: exitDisplayNames[exit]).code
    }
}
