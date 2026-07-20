import SwiftUI
import VertexCore

struct ConnectScreen: View {
    @Bindable var viewModel: TunnelViewModel
    @State private var showStats = false
    @State private var navigateToBrokers = false
    @State private var navigateToExits = false

    var body: some View {
        ScrollView {
            VStack(spacing: VxSpace.s7) {
                hero
                serverCard
                connectButton
                statsCard
                errorBanner
                debugBadge
            }
            .padding(.horizontal, VxSpace.s5)
            .padding(.top, VxSpace.s4)
            .padding(.bottom, VxSpace.s8)
            .frame(maxWidth: 480)
            .frame(maxWidth: .infinity)
        }
        .scrollContentBackground(.hidden)
        .vertexCanvas()
        .navigationDestination(isPresented: $navigateToBrokers) {
            BrokerListView(viewModel: viewModel)
        }
        .navigationDestination(isPresented: $navigateToExits) {
            ExitListView(viewModel: viewModel)
        }
        .sheet(isPresented: $showStats) {
            StatsSheet(viewModel: viewModel)
                .presentationDetents([.medium, .large])
                .presentationDragIndicator(.visible)
                .presentationBackground(.regularMaterial)
                .presentationCornerRadius(28)
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
        if let error = viewModel.errorMessage, !viewModel.permissionDenied {
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

    @ViewBuilder
    private var debugBadge: some View {
        #if DEBUG
        Text("DEBUG BUILD")
            .font(.system(size: 10, weight: .semibold, design: .monospaced))
            .tracking(1.2)
            .foregroundStyle(Color.stateTransitioning)
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(Color.stateTransitioning.opacity(0.12), in: .capsule)
            .overlay(
                Capsule().strokeBorder(Color.stateTransitioning.opacity(0.35), lineWidth: 0.5)
            )
        #else
        EmptyView()
        #endif
    }
}
