import VertexCore
import SwiftUI

struct BrokerListView: View {
    @Bindable var viewModel: TunnelViewModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: VxSpace.s8) {
                VxSection(
                    header: "Available Vertices",
                    footer: "Vertices are tried in SRV-priority order. The selected vertex leads; the rest serve as failover."
                ) {
                    ForEach(Array(viewModel.presentedBrokers.enumerated()), id: \.element) { presentedIdx, item in
                        if presentedIdx > 0 { VxDivider(leadingInset: 56) }
                        Button {
                            viewModel.selectedBroker = item
                            Haptics.selection()
                            dismiss()
                        } label: {
                            if item == "auto" {
                                autoRow(selected: viewModel.selectedBroker == "auto")
                            } else {
                                row(url: item, selected: item == viewModel.selectedBroker)
                            }
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
            .padding(.horizontal, VxSpace.s5)
            .padding(.top, VxSpace.s4)
            .padding(.bottom, VxSpace.s8)
        }
        .vertexInnerCanvas()
        .navigationTitle("Vertices")
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await viewModel.resolveSRV() }
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button {
                    Task { await viewModel.resolveSRV() }
                } label: {
                    Image(systemName: "arrow.clockwise")
                        .foregroundStyle(Color.accentPrimary)
                }
                .accessibilityLabel("Refresh vertices")
            }
        }
    }

    private func row(url: String, selected: Bool) -> some View {
        let urlHost = NodeLabels.host(of: url) ?? url
        let uniqueIndex = NodeLabels.uniqueHosts(in: viewModel.availableBrokers)
            .firstIndex(of: urlHost) ?? 0
        let chip = NodeLabels.vertexLabel(host: urlHost, index: uniqueIndex).code

        return HStack(spacing: VxSpace.s3) {
            VxAsteriskGlyph(size: 22)
                .frame(width: 28)
            VStack(alignment: .leading, spacing: 2) {
                Text(urlHost)
                    .font(selected ? .vxBodyEmphasized : .vxBody)
                    .foregroundStyle(Color.textPrimary)
                Text(scheme(url))
                    .font(.vxCaptionMono)
                    .foregroundStyle(Color.textTertiary)
            }
            Spacer()
            Text(chip)
                .font(.vxCaptionMono)
                .foregroundStyle(Color.textTertiary)
            if selected {
                VxSelectionGlyph(size: 18)
            }
        }
        .padding(.vertical, VxSpace.s3)
        .padding(.horizontal, VxSpace.s4)
        .frame(minHeight: 56)
        .contentShape(.rect)
    }

    private func scheme(_ url: String) -> String {
        guard let c = URLComponents(string: url), let s = c.scheme else { return "" }
        if let port = c.port { return "\(s.uppercased()) · \(port)" }
        return s.uppercased()
    }

    /// "Auto" pseudo-row — extension probes TCP-connect RTT to every
    /// broker URL and connects to the fastest. Subtitle shows the live
    /// resolved broker short-name while connected so users see what
    /// auto picked.
    private func autoRow(selected: Bool) -> some View {
        // `currentBroker` is already a bare host — see ConnectScreen for
        // rationale.
        let resolved: String? = viewModel.connectionStatus?.currentBroker
        let subtitle: String = {
            if let resolved, viewModel.isConnected {
                let short = NodeLabels.vertexLabel(host: resolved, index: 0).shortName
                return "Now: \(short)"
            }
            return "Lowest TCP RTT"
        }()
        return HStack(spacing: VxSpace.s3) {
            VxAsteriskGlyph(size: 22)
                .frame(width: 28)
            VStack(alignment: .leading, spacing: 2) {
                Text("Auto")
                    .font(selected ? .vxBodyEmphasized : .vxBody)
                    .foregroundStyle(Color.textPrimary)
                Text(subtitle)
                    .font(.vxCaptionMono)
                    .foregroundStyle(Color.textTertiary)
            }
            Spacer()
            if selected {
                VxSelectionGlyph(size: 18)
            }
        }
        .padding(.vertical, VxSpace.s3)
        .padding(.horizontal, VxSpace.s4)
        .frame(minHeight: 56)
        .contentShape(.rect)
    }
}
