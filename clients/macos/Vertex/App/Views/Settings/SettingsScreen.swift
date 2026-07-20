import VertexCore
import SwiftUI
import AppKit

/// macOS Settings scene root — a TabView with the four panes Identity /
/// Discovery / Routing / About. The host App embeds this in `Settings { }`
/// so ⌘, opens it as a separate window the way every Mac user expects.
struct SettingsScreen: View {
    @Bindable var viewModel: TunnelViewModel

    var body: some View {
        TabView {
            IdentityTab(viewModel: viewModel)
                .tabItem {
                    Label("Identity", systemImage: "person.crop.circle")
                }
            DiscoveryTab(viewModel: viewModel)
                .tabItem {
                    Label("Discovery", systemImage: "globe")
                }
            RoutingTab()
                .tabItem {
                    Label("Routing", systemImage: "arrow.triangle.branch")
                }
            AboutTab()
                .tabItem {
                    Label("About", systemImage: "info.circle")
                }
        }
        .frame(minWidth: 560, minHeight: 640)
    }
}

// MARK: - Identity tab

private struct IdentityTab: View {
    @Bindable var viewModel: TunnelViewModel
    @State private var password: String = ""
    @State private var showPassword = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: VxSpace.s6) {
                VxSection(
                    header: "Identity",
                    footer: "Username is exit-independent: vtx-client-{name}. Password is stored in the macOS Keychain."
                ) {
                    VxRow {
                        Text("Client name").foregroundStyle(Color.textSecondary)
                        Spacer()
                        TextField("e.g. macbook", text: $viewModel.clientName)
                            .multilineTextAlignment(.trailing)
                            .autocorrectionDisabled()
                            .textFieldStyle(.plain)
                            .foregroundStyle(Color.textPrimary)
                            .frame(minWidth: 160)
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
                        .autocorrectionDisabled()
                        .textFieldStyle(.plain)
                        .foregroundStyle(Color.textPrimary)
                        .frame(minWidth: 160)
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

                IdentityKeyInline()
            }
            .padding(VxSpace.s5)
        }
        .vertexInnerCanvas()
        .onAppear {
            password = (try? KeychainStore.loadPassword()) ?? ""
        }
    }
}

// MARK: - Discovery tab (domain + active configuration)

private struct DiscoveryTab: View {
    @Bindable var viewModel: TunnelViewModel

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: VxSpace.s6) {
                VxSection(header: "Discovery", footer: "Vertices and edges are resolved via DNS SRV records from this domain.") {
                    VxRow {
                        Text("Discovery domain").foregroundStyle(Color.textSecondary)
                        Spacer()
                        TextField("vertices.ru", text: $viewModel.domain)
                            .multilineTextAlignment(.trailing)
                            .autocorrectionDisabled()
                            .textFieldStyle(.plain)
                            .foregroundStyle(Color.textPrimary)
                            .frame(minWidth: 160)
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

                ActiveConfigSection(viewModel: viewModel)
            }
            .padding(VxSpace.s5)
        }
        .vertexInnerCanvas()
    }
}

// MARK: - Routing tab

private struct RoutingTab: View {
    @AppStorage("splitTunnelEnabled", store: UserDefaults(suiteName: RUNetsLoader.appGroupID))
    private var splitTunnelEnabled: Bool = false
    @State private var ruNetsStats: RUNetsLoader.Stats?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: VxSpace.s6) {
                VxSection(
                    header: "Routing",
                    footer: "RU subnets bypass the tunnel and go through your provider directly. Takes effect on next connect."
                ) {
                    VxRow {
                        Text("Split tunnel (RU direct)").foregroundStyle(Color.textPrimary)
                        Spacer()
                        Toggle("", isOn: $splitTunnelEnabled)
                            .labelsHidden()
                            .toggleStyle(.switch)
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
            .padding(VxSpace.s5)
        }
        .vertexInnerCanvas()
        .onAppear {
            ruNetsStats = RUNetsLoader.stats()
        }
    }

    private static let zoneDateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()
}

// MARK: - About tab (wraps existing AboutView)

private struct AboutTab: View {
    var body: some View {
        AboutView()
    }
}

// MARK: - Active configuration (lifted from iOS SettingsScreen.activeConfigSection)

private struct ActiveConfigSection: View {
    let viewModel: TunnelViewModel

    var body: some View {
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
}

// MARK: - Inline Identity-Key block (used inside Identity tab)

private struct IdentityKeyInline: View {
    @State private var pubkeyHex: String = ""
    @State private var isRevealed: Bool = false

    var body: some View {
        VxSection(
            header: "Public Identity",
            footer: "This device's permanent identity in the Vertex graph. Stored in the macOS Keychain — never leaves this device. Right-click the fingerprint to copy the full key for admin reset on the edge."
        ) {
            if pubkeyHex.isEmpty {
                VxRow {
                    Text("No identity key generated yet. The key is created on first connect.")
                        .font(.vxBody)
                        .foregroundStyle(Color.textSecondary)
                        .multilineTextAlignment(.leading)
                    Spacer(minLength: 0)
                }
            } else {
                identityContent
            }
        }
        .task { loadKey() }
    }

    private var identityContent: some View {
        VStack(alignment: .leading, spacing: VxSpace.s3) {
            Text("Fingerprint")
                .font(.vxSubheadline)
                .foregroundStyle(Color.textSecondary)

            HStack(spacing: VxSpace.s3) {
                Text(fingerprint)
                    .font(.identityHex)
                    .foregroundStyle(Color.textPrimary)
                    .contextMenu { copyMenuItem }
                    .textSelection(.enabled)
                Spacer(minLength: VxSpace.s2)
                Button {
                    withAnimation(.easeInOut(duration: 0.2)) {
                        isRevealed.toggle()
                    }
                    Haptics.selection()
                } label: {
                    HStack(spacing: 4) {
                        Text(isRevealed ? "Hide" : "Reveal")
                            .font(.vxBody)
                        Image(systemName: isRevealed ? "chevron.up" : "chevron.right")
                            .font(.caption.weight(.semibold))
                    }
                    .foregroundStyle(Color.accentPrimary)
                }
                .buttonStyle(.plain)
            }

            if isRevealed {
                ScrollView(.horizontal, showsIndicators: false) {
                    Text(pubkeyHex)
                        .font(.identityHex)
                        .foregroundStyle(Color.textPrimary)
                        .padding(VxSpace.s3)
                        .textSelection(.enabled)
                }
                .background(Color.bgSurfaceMuted, in: .rect(cornerRadius: VxRadius.md))
                .contextMenu { copyMenuItem }
            }
        }
        .padding(VxSpace.s4)
    }

    @ViewBuilder
    private var copyMenuItem: some View {
        Button {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(pubkeyHex, forType: .string)
            Haptics.notify(.success)
        } label: {
            Label("Copy for admin reset", systemImage: "doc.on.doc")
        }
    }

    /// First 16 hex chars of the public key, formatted as 4 groups of 4 separated by spaces.
    private var fingerprint: String {
        let prefix = pubkeyHex.prefix(16)
        let chars = Array(prefix)
        var groups: [String] = []
        var idx = 0
        while idx < chars.count {
            let end = min(idx + 4, chars.count)
            groups.append(String(chars[idx..<end]))
            idx += 4
        }
        return groups.joined(separator: " ")
    }

    private func loadKey() {
        guard let raw = try? KeychainStore.loadIdentityKey(),
              let key = try? IdentityKey(rawRepresentation: raw) else {
            pubkeyHex = ""
            return
        }
        pubkeyHex = key.publicKeyHex
    }
}
