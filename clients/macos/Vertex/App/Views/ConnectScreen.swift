import SwiftUI

struct ConnectScreen: View {
    @Bindable var viewModel: TunnelViewModel
    @State private var showStats = false
    @State private var navigateToBrokers = false
    @State private var navigateToExits = false

    var body: some View {
        VStack(spacing: VxSpace.s7) {
            wordmark
            hero
            serverCard
            connectButton
            statsCard
            errorBanner
        }
        .padding(.horizontal, VxSpace.s5)
        .padding(.top, VxSpace.s2)
        .padding(.bottom, VxSpace.s8)
        .frame(maxWidth: 480)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .vertexCanvas()
        .navigationDestination(isPresented: $navigateToBrokers) {
            BrokerListView(viewModel: viewModel)
        }
        .navigationDestination(isPresented: $navigateToExits) {
            ExitListView(viewModel: viewModel)
        }
        .sheet(isPresented: $showStats) {
            StatsSheet(viewModel: viewModel)
                .frame(minWidth: 460, minHeight: 520)
                .preferredColorScheme(.dark)
        }
        .onChange(of: viewModel.vpnStatus) { _, newStatus in
            switch newStatus {
            case .connected: Haptics.notify(.success)
            case .disconnected:
                if viewModel.errorMessage != nil { Haptics.notify(.error) }
            default: break
            }
        }
    }

    /// VERTEX wordmark in content (not in toolbar) — macOS Tahoe wraps
    /// every ToolbarItem in a Liquid Glass capsule with no clean opt-out,
    /// so the wordmark lives at the top of the scroll content instead.
    private var wordmark: some View {
        Text("VERTEX")
            .font(.brandWordmark)
            .tracking(1.5)
            .foregroundStyle(Color.textPrimary)
            .accessibilityAddTraits(.isHeader)
    }

    private var hero: some View {
        VStack(spacing: VxSpace.s4) {
            VertexHero(
                status: viewModel.vpnStatus,
                uploadRateBps: viewModel.uploadRate,
                downloadRateBps: viewModel.downloadRate
            )
                .padding(.top, VxSpace.s2)
            Text(viewModel.statusText)
                .font(.heroStatus)
                .tracking(-0.4)
                .foregroundStyle(viewModel.statusColor)
                .contentTransition(.opacity)
                .dynamicTypeSize(...DynamicTypeSize.accessibility2)
                .padding(.top, -VxSpace.s2)
            StatusPillView(
                status: viewModel.vpnStatus,
                assignedIP: viewModel.connectionStatus?.assignedIP,
                connectedSince: viewModel.connectionStatus?.connectedSince
            )
            if viewModel.isConnected {
                SpeedPillView(
                    uploadRate: viewModel.uploadRate,
                    downloadRate: viewModel.downloadRate,
                    pingMs: viewModel.pingMs
                )
                .transition(.opacity.combined(with: .scale(scale: 0.96)))
            }
        }
        .animation(VxMotion.statsSpring, value: viewModel.isConnected)
    }

    private var serverCard: some View {
        ServerCard(
            brokerURL: viewModel.selectedBroker,
            exit: viewModel.selectedExit,
            availableBrokers: viewModel.availableBrokers,
            availableExits: viewModel.availableExits,
            exitDisplayNames: viewModel.exitDisplayNames,
            resolvedExit: viewModel.connectionStatus?.currentExit,
            // `currentBroker` is already a bare host (set from
            // MQTTTransport.currentBroker, which reads `brokers[i].host`)
            // — no URL-parse needed.
            resolvedBrokerHost: viewModel.connectionStatus?.currentBroker,
            isDisabled: viewModel.isConnected || viewModel.isTransitioning,
            onBrokerTap: { navigateToBrokers = true },
            onExitTap: { navigateToExits = true }
        )
    }

    private var connectButton: some View {
        BigConnectButton(
            isConnected: viewModel.isConnected,
            isTransitioning: viewModel.isTransitioning
        ) {
            Task { await viewModel.toggleConnection() }
        }
    }

    @ViewBuilder
    private var statsCard: some View {
        if viewModel.isConnected, let stats = viewModel.stats {
            Button {
                showStats = true
            } label: {
                StatRowView(stats: stats)
            }
            .buttonStyle(.plain)
            .transition(.opacity.combined(with: .move(edge: .bottom)))
        }
    }

    @ViewBuilder
    private var errorBanner: some View {
        if let error = viewModel.errorMessage {
            HStack(alignment: .top, spacing: 10) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(Color.stateError)
                Text(error)
                    .font(.vxFootnote)
                    .foregroundStyle(Color.textSecondary)
                    .multilineTextAlignment(.leading)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(VxSpace.s3)
            .background(Color.stateError.opacity(0.12), in: .rect(cornerRadius: VxRadius.lg))
            .overlay(
                RoundedRectangle(cornerRadius: VxRadius.lg)
                    .strokeBorder(Color.stateError.opacity(0.35), lineWidth: 0.5)
            )
        }
    }

}
