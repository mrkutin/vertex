import VertexCore
import SwiftUI

struct StatsSheet: View {
    @Bindable var viewModel: TunnelViewModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            List {
                Section {
                    VStack(spacing: VxSpace.s3) {
                        VertexHero(status: viewModel.vpnStatus,
                                   contentSize: 96,
                                   haloPadding: 12)
                        Text(viewModel.statusText)
                            .font(.system(size: 18, weight: .semibold, design: .rounded))
                            .foregroundStyle(viewModel.statusColor)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, VxSpace.s3)
                    .listRowBackground(Color.clear)
                    .listRowSeparator(.hidden)
                    .listRowInsets(EdgeInsets())
                }

                if let status = viewModel.connectionStatus {
                    Section {
                        if let ip = status.assignedIP {
                            LabeledContent {
                                Text(ip).font(.statValue).foregroundStyle(Color.textPrimary)
                            } label: {
                                Text("Assigned IP").foregroundStyle(Color.textSecondary)
                            }
                        }
                        if let broker = status.currentBroker {
                            LabeledContent {
                                Text(brokerHost(broker))
                                    .foregroundStyle(Color.textPrimary)
                            } label: {
                                Text("Vertex").foregroundStyle(Color.textSecondary)
                            }
                        }
                        LabeledContent {
                            // Prefer the extension-reported `currentExit`
                            // (the resolved value when user picked "auto");
                            // fall back to the user's pick if not reported.
                            Text(edgeText)
                                .foregroundStyle(Color.textPrimary)
                        } label: {
                            Text("Edge").foregroundStyle(Color.textSecondary)
                        }
                        if let since = status.connectedSince {
                            LabeledContent {
                                Text(since.formatted(date: .omitted, time: .shortened))
                                    .foregroundStyle(Color.textPrimary)
                                    .monospacedDigit()
                            } label: {
                                Text("Connected").foregroundStyle(Color.textSecondary)
                            }
                        }
                    } header: {
                        Text("Vertex")
                            .font(.vxCaption)
                            .tracking(0.8)
                            .textCase(.uppercase)
                            .foregroundStyle(Color.textTertiary)
                    }
                    .listRowBackground(Color.bgSurface)
                }

                if let stats = viewModel.stats {
                    Section {
                        LabeledContent {
                            Text(format(stats.bytesUp))
                                .font(.statValue)
                                .foregroundStyle(Color.textPrimary)
                        } label: {
                            Text("Sent").foregroundStyle(Color.textSecondary)
                        }
                        LabeledContent {
                            Text(format(stats.bytesDown))
                                .font(.statValue)
                                .foregroundStyle(Color.textPrimary)
                        } label: {
                            Text("Received").foregroundStyle(Color.textSecondary)
                        }
                        LabeledContent {
                            Text("\(stats.packetsUp)")
                                .font(.statValue)
                                .foregroundStyle(Color.textPrimary)
                        } label: {
                            Text("Packets up").foregroundStyle(Color.textSecondary)
                        }
                        LabeledContent {
                            Text("\(stats.packetsDown)")
                                .font(.statValue)
                                .foregroundStyle(Color.textPrimary)
                        } label: {
                            Text("Packets down").foregroundStyle(Color.textSecondary)
                        }
                    } header: {
                        Text("Traffic")
                            .font(.vxCaption)
                            .tracking(0.8)
                            .textCase(.uppercase)
                            .foregroundStyle(Color.textTertiary)
                    }
                    .listRowBackground(Color.bgSurface)
                }
            }
            .scrollContentBackground(.hidden)
            .navigationTitle("Connection")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Done") { dismiss() }
                        .foregroundStyle(Color.accentPrimary)
                }
            }
        }
        .preferredColorScheme(.dark)
    }

    private func brokerHost(_ url: String) -> String {
        URLComponents(string: url)?.host ?? url
    }

    private var edgeText: String {
        if let resolved = viewModel.connectionStatus?.currentExit, !resolved.isEmpty {
            return resolved.uppercased()
        }
        return viewModel.selectedExit.uppercased()
    }

    private func format(_ bytes: UInt64) -> String {
        let bcf = ByteCountFormatter()
        bcf.allowedUnits = [.useKB, .useMB, .useGB]
        bcf.countStyle = .binary
        return bcf.string(fromByteCount: Int64(bytes))
    }
}
