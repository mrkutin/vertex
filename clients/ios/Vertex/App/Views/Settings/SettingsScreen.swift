import VertexCore
import SwiftUI

struct SettingsScreen: View {
    @Bindable var viewModel: TunnelViewModel
    @State private var password: String = ""
    @State private var showPassword = false
    /// Stored in the App Group so the extension reads the same flag at startVPNTunnel.
    @AppStorage("splitTunnelEnabled", store: UserDefaults(suiteName: RUNetsLoader.appGroupID))
    private var splitTunnelEnabled: Bool = false
    @State private var ruNetsStats: RUNetsLoader.Stats?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: VxSpace.s8) {
                identitySection
                discoverySection
                routingSection
                activeConfigSection
                navSection
            }
            .padding(.horizontal, VxSpace.s5)
            .padding(.top, VxSpace.s4)
            .padding(.bottom, VxSpace.s8)
        }
        .vertexInnerCanvas()
        .navigationTitle("Settings")
        .navigationBarTitleDisplayMode(.inline)
        .onAppear {
            password = (try? KeychainStore.loadPassword()) ?? ""
            ruNetsStats = RUNetsLoader.stats()
        }
    }

    private var identitySection: some View {
        VxSection(header: "Identity") {
            VxRow {
                Text("Client name").foregroundStyle(Color.textSecondary)
                Spacer()
                TextField("e.g. iphone", text: $viewModel.clientName)
                    .multilineTextAlignment(.trailing)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .foregroundStyle(Color.textPrimary)
            }
            VxDivider()
            VxRow {
                Text("Password").foregroundStyle(Color.textSecondary)
                Spacer()
                Group {
                    if showPassword {
                        TextField("password", text: $password)
                    } else {
                        SecureField("password", text: $password)
                    }
                }
                .multilineTextAlignment(.trailing)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .foregroundStyle(Color.textPrimary)
                .frame(minWidth: 120)
                Button {
                    showPassword.toggle()
                } label: {
                    Image(systemName: showPassword ? "eye.slash" : "eye")
                        .foregroundStyle(Color.textTertiary)
                }
                .buttonStyle(.plain)
                .onChange(of: password) { _, v in
                    try? KeychainStore.savePassword(v)
                }
            }
        }
    }

    private var discoverySection: some View {
        VxSection(header: "Discovery", footer: "Vertices and edges are resolved via DNS SRV records from this domain.") {
            VxRow {
                Text("Discovery domain").foregroundStyle(Color.textSecondary)
                Spacer()
                TextField("vertices.ru", text: $viewModel.domain)
                    .multilineTextAlignment(.trailing)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .keyboardType(.URL)
                    .foregroundStyle(Color.textPrimary)
            }
            VxDivider()
            Button {
                Task { await viewModel.resolveSRV() }
            } label: {
                HStack(spacing: VxSpace.s2) {
                    Image(systemName: "arrow.clockwise")
                    Text("Refresh")
                }
                .font(.vxBody)
                .foregroundStyle(Color.accentPrimary)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, VxSpace.s3)
                .padding(.horizontal, VxSpace.s4)
            }
            .buttonStyle(.plain)
        }
    }

    private var routingSection: some View {
        VxSection(
            header: "Routing",
            footer: "RU subnets bypass the tunnel and go through your provider directly. Takes effect on next connect."
        ) {
            VxRow {
                Text("Split tunnel (RU direct)").foregroundStyle(Color.textPrimary)
                Spacer()
                Toggle("", isOn: $splitTunnelEnabled)
                    .labelsHidden()
                    .tint(Color.accentPrimary)
            }
            if let stats = ruNetsStats {
                VxDivider()
                VxRow {
                    Text("Networks").foregroundStyle(Color.textSecondary)
                    Spacer()
                    Text("\(stats.cidrCount)")
                        .font(.vxCaptionMono)
                        .foregroundStyle(Color.textPrimary)
                }
                VxDivider()
                VxRow {
                    Text("Updated").foregroundStyle(Color.textSecondary)
                    Spacer()
                    Text(Self.zoneDateFormatter.string(from: stats.modifiedAt))
                        .font(.vxCaptionMono)
                        .foregroundStyle(Color.textPrimary)
                }
            }
        }
    }

    private static let zoneDateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()

    private var activeConfigSection: some View {
        let hosts = NodeLabels.uniqueHosts(in: viewModel.availableBrokers)
        let exits = viewModel.availableExits

        return VxSection(header: "Active Configuration") {
            if hosts.isEmpty && exits.isEmpty {
                VxRow {
                    Text("Resolving discovery…")
                        .font(.vxBody)
                        .foregroundStyle(Color.textSecondary)
                    Spacer()
                }
            } else {
                if !hosts.isEmpty {
                    miniHeader("Vertices")
                    ForEach(Array(hosts.enumerated()), id: \.element) { idx, host in
                        if idx > 0 { VxDivider(leadingInset: 56) }
                        vertexRow(host: host, index: idx)
                    }
                }
                if !hosts.isEmpty && !exits.isEmpty {
                    // Full-width hairline keeps the bgSurface plate continuous;
                    // a Spacer would punch a visible surface-colored hole here.
                    VxDivider(leadingInset: 0)
                        .padding(.top, VxSpace.s2)
                }
                if !exits.isEmpty {
                    miniHeader("Edges")
                    ForEach(Array(exits.enumerated()), id: \.element) { idx, exit in
                        if idx > 0 { VxDivider(leadingInset: 56) }
                        edgeRow(id: exit, index: idx)
                    }
                }
            }
        }
    }

    private func miniHeader(_ text: String) -> some View {
        Text(text)
            .font(.vxCaption)
            .tracking(0.8)
            .textCase(.uppercase)
            .foregroundStyle(Color.textTertiary)
            .padding(.leading, VxSpace.s4)
            .padding(.top, VxSpace.s3)
            .padding(.bottom, VxSpace.s1)
    }

    private func vertexRow(host: String, index: Int) -> some View {
        let label = NodeLabels.vertexLabel(host: host, index: index)
        let shortHost = host.split(separator: ".").first.map(String.init) ?? host
        let schemes = NodeLabels.protocols(forHost: host, in: viewModel.availableBrokers)

        return VxRow {
            VxAsteriskGlyph(size: 18)
                .frame(width: 28)
            Text(label.code)
                .font(.vxBody)
                .foregroundStyle(Color.textPrimary)
            Text(shortHost)
                .font(.vxCaptionMono)
                .foregroundStyle(Color.textTertiary)
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer()
            HStack(spacing: 4) {
                ForEach(Array(schemes.enumerated()), id: \.element) { i, scheme in
                    VxProtocolBadge(label: scheme, isPrimary: i == 0)
                }
            }
        }
    }

    private func edgeRow(id: String, index: Int) -> some View {
        let label = NodeLabels.edgeLabel(id, index: index, displayOverride: viewModel.exitDisplayNames[id])
        let city = label.display.split(separator: ",").first.map { String($0).trimmingCharacters(in: .whitespaces) } ?? label.display

        return VxRow {
            VxEdgeGlyph(size: 18)
                .frame(width: 28)
            Text(label.code)
                .font(.vxBody)
                .foregroundStyle(Color.textPrimary)
            Text(city)
                .font(.vxCaptionMono)
                .foregroundStyle(Color.textTertiary)
            Spacer()
        }
    }

    private var navSection: some View {
        VxSection {
            NavigationLink {
                IdentityKeyView()
            } label: {
                navRow(systemImage: "key.fill", title: "Identity Key")
            }
            .buttonStyle(.plain)
            VxDivider()
            NavigationLink {
                DiagnosticsView()
            } label: {
                navRow(systemImage: "waveform.path.ecg", title: "Diagnostics")
            }
            .buttonStyle(.plain)
            VxDivider()
            NavigationLink {
                AboutView()
            } label: {
                navRow(systemImage: "info.circle", title: "About")
            }
            .buttonStyle(.plain)
        }
    }

    private func navRow(systemImage: String, title: String) -> some View {
        HStack(spacing: VxSpace.s3) {
            Image(systemName: systemImage)
                .foregroundStyle(Color.accentPrimary)
                .frame(width: 28)
            Text(title)
                .foregroundStyle(Color.textPrimary)
            Spacer()
            Image(systemName: "chevron.right")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(Color.textTertiary)
        }
        .padding(.vertical, VxSpace.s3)
        .padding(.horizontal, VxSpace.s4)
        .contentShape(.rect)
    }
}

// VxSection / VxRow / VxDivider primitives live in
// App/Views/Components/VxSection.swift — used across all inner screens.
