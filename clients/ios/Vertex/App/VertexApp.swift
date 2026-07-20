import SwiftUI
import VertexCore
import os

@main
struct VertexApp: App {
    @State private var viewModel = TunnelViewModel()
    /// Subscribes to MetricKit on first launch. The host app must be open at
    /// least briefly each day for the OS to deliver the daily payload — this
    /// is fine because the user does open the app to check status.
    private let metricsCollector = MetricsCollector()
    @Environment(\.scenePhase) private var scenePhase

    private static let logger = Logger(subsystem: "ru.vertices", category: "app")

    var body: some Scene {
        WindowGroup {
            RootView(viewModel: viewModel)
                .task {
                    await viewModel.loadState()
                    Self.bootstrapRUNets()
                    Task.detached(priority: .background) { await Self.refreshRUNets() }
                }
                .onChange(of: scenePhase) { _, phase in
                    viewModel.handleScenePhase(phase)
                }
        }
    }

    /// Copy bundled fallback into App Group on first launch — guarantees the
    /// extension finds the file even before the first network refresh.
    private static func bootstrapRUNets() {
        do {
            try RUNetsLoader.bootstrap(bundle: .main)
        } catch {
            logger.error("RU CIDR bootstrap failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    /// Background refresh from ipdeny on every launch. Failure is silent — the
    /// extension still has the previous (or bundled) copy.
    private static func refreshRUNets() async {
        do {
            try await RUNetsLoader.refresh()
            logger.info("RU CIDR refreshed from ipdeny.com")
        } catch {
            logger.notice("RU CIDR refresh skipped: \(error.localizedDescription, privacy: .public)")
        }
    }
}
