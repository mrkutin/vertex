import VertexCore
import SwiftUI

struct ExitListView: View {
    @Bindable var viewModel: TunnelViewModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: VxSpace.s8) {
                VxSection(
                    header: "Available Edges",
                    footer: "The edge is the network shoulder where your traffic exits to the public internet."
                ) {
                    ForEach(Array(viewModel.presentedExits.enumerated()), id: \.element) { presentedIdx, exit in
                        if presentedIdx > 0 { VxDivider(leadingInset: 56) }
                        Button {
                            viewModel.selectedExit = exit
                            Haptics.selection()
                            dismiss()
                        } label: {
                            if exit == "auto" {
                                autoRow(selected: viewModel.selectedExit == "auto")
                            } else {
                                // Subscript index uses position in the real
                                // SRV list, NOT in `presentedExits` — keeps
                                // E₀/E₁ stable across UI changes.
                                let idx = viewModel.availableExits.firstIndex(of: exit) ?? 0
                                row(exit: exit, index: idx, selected: exit == viewModel.selectedExit)
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
        .navigationTitle("Edges")
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
                .accessibilityLabel("Refresh edges")
            }
        }
    }

    private func row(exit: String, index: Int, selected: Bool) -> some View {
        let label = NodeLabels.edgeLabel(exit, index: index, displayOverride: viewModel.exitDisplayNames[exit])
        return HStack(spacing: VxSpace.s3) {
            VxEdgeGlyph(size: 22)
                .frame(width: 28)
            VStack(alignment: .leading, spacing: 2) {
                Text(label.display)
                    .font(selected ? .vxBodyEmphasized : .vxBody)
                    .foregroundStyle(Color.textPrimary)
                Text(label.code)
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

    /// "Auto" pseudo-row — the extension resolves this to a concrete edge
    /// after MQTT connect using broker-RTT and load score. Subtitle shows
    /// the live resolved edge while connected so users see what auto picked.
    private func autoRow(selected: Bool) -> some View {
        let resolved = viewModel.connectionStatus?.currentExit
        let subtitle: String = {
            if let resolved, viewModel.isConnected, resolved != "auto" {
                let display = viewModel.exitDisplayNames[resolved] ?? resolved.uppercased()
                return "Now: \(display)"
            }
            return "Best edge per latency"
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
