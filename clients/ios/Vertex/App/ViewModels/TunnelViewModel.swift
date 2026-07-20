import VertexCore
@preconcurrency import NetworkExtension
import Network
import os
import SwiftUI

@Observable
@MainActor
final class TunnelViewModel {
    private let log = Logger(subsystem: "ru.vertices", category: "viewmodel")

    // Connection
    var vpnStatus: NEVPNStatus = .disconnected
    var connectionStatus: ConnectionStatus?
    var stats: TunnelStats?

    /// Rolling history of byte samples for instantaneous rate calculation.
    /// We keep ~5s of samples, compute rate over the last 3s — smooth enough
    /// to avoid jitter, responsive enough to feel real-time. (Best practice
    /// for network-rate displays in tools like Activity Monitor.)
    private struct StatsSample {
        let time: Date
        let bytesUp: UInt64
        let bytesDown: UInt64
    }
    private var statsHistory: [StatsSample] = []
    private static let rateWindow: TimeInterval = 3.0
    private static let historyHorizon: TimeInterval = 5.0

    /// Bytes-per-second over the rolling rate window. Returns 0 when not
    /// enough samples or no traffic.
    var uploadRate: Double { instantRate(\.bytesUp) }
    var downloadRate: Double { instantRate(\.bytesDown) }

    /// End-to-end RTT through the active tunnel, in milliseconds.
    /// Measured by TCP-connecting to 1.1.1.1:443 (Cloudflare anycast — never blocked,
    /// fastest possible upstream); time from .start() to .ready ≈ one round-trip.
    /// `nil` until the first successful measurement; cleared only on
    /// disconnect — transient probe failures keep the last value visible.
    var pingMs: Int?

    private func instantRate(_ keyPath: KeyPath<StatsSample, UInt64>) -> Double {
        guard let newest = statsHistory.last else { return 0 }
        let cutoff = newest.time.addingTimeInterval(-Self.rateWindow)
        guard let oldest = statsHistory.first(where: { $0.time >= cutoff }),
              oldest.time < newest.time
        else { return 0 }
        let elapsed = newest.time.timeIntervalSince(oldest.time)
        guard elapsed > 0 else { return 0 }
        let delta = Int64(newest[keyPath: keyPath]) - Int64(oldest[keyPath: keyPath])
        guard delta > 0 else { return 0 }
        return Double(delta) / elapsed
    }

    // Discovery
    var domain: String = "vertices.ru" {
        didSet { UserDefaults.standard.set(domain, forKey: "discoveryDomain") }
    }
    var availableBrokers: [String] = []
    var availableExits: [String] = []
    /// Per-exit display string from `SRVDiscoveryResult.exitDisplayNames`
    /// (TXT records on the SRV target). Missing key → UI falls back to
    /// uppercased exit ID via `NodeLabels.edgeLabel`.
    var exitDisplayNames: [String: String] = [:]
    var selectedBroker: String = "auto" {
        didSet { UserDefaults.standard.set(selectedBroker, forKey: "selectedBroker") }
    }
    var selectedExit: String = "auto" {
        didSet { UserDefaults.standard.set(selectedExit, forKey: "selectedExit") }
    }

    /// `availableExits` is the SRV-resolved list of concrete edges (`aws`,
    /// `sto`, …); the picker shows the synthetic `"auto"` option as a
    /// header element. Kept out of `availableExits` itself so the
    /// `NodeLabels.edgeLabel` subscript indices don't shift when "auto"
    /// is added to the UI.
    var presentedExits: [String] { ["auto"] + availableExits }

    /// Same synthetic-head trick for brokers — `availableBrokers` stays
    /// the SRV-resolved truth list (broker URLs); the picker shows
    /// `"auto"` first as a sentinel meaning "let the extension pick by
    /// TCP-connect RTT". Indices into `availableBrokers` (used for
    /// `NodeLabels.vertexLabel`) are unaffected.
    var presentedBrokers: [String] { ["auto"] + availableBrokers }

    // User settings
    var clientName: String = "iphone"

    // UI
    var errorMessage: String?
    var permissionDenied: Bool = false

    // Services
    private let tunnelManager = TunnelManager()
    private let srvDiscovery = SRVDiscovery()
    private var pollTimer: Timer?
    private var pingTimer: Timer?
    /// Single-flight handle for an in-flight SRV resolve. A rapid double-tap
    /// on Connect (cold install, no cache, user impatient) used to launch
    /// two parallel `resolveSRV()` calls — both DoH round-trips,
    /// last-writer-wins on `availableBrokers` (potentially with stale
    /// data). Coalesce: the second tap awaits the first.
    private var resolveTask: Task<Void, Never>?
    /// Wifi watcher in the host app process. Unlike the extension's
    /// monitor (which can be path-scoped while the extension is bound to
    /// cellular), the app-process monitor is unscoped and reliably fires
    /// when wifi associates. On wifi unsat→sat we IPC the extension via
    /// `notifyWifiAvailable`, which restarts it via cancelTunnelWithError
    /// + on-demand. Limitation: only fires while the app is alive
    /// (foreground or near-foreground in background).
    private var wifiPathMonitor: NWPathMonitor?
    private let wifiPathMonitorQueue = DispatchQueue(label: "ru.vertices.app.wifi-monitor")
    private var wifiPathSatisfied: Bool = false
    private static let pingHost = "1.1.1.1"
    private static let pingPort: UInt16 = 443
    private static let pingInterval: TimeInterval = 60.0
    private static let pingTimeout: TimeInterval = 2.5

    /// Default exit selection for a fresh install — auto-resolve picks the
    /// best edge per broker RTT and load. Existing users with a saved value
    /// (`UserDefaults["selectedExit"]`) keep their explicit pick; only new
    /// installs land on auto.
    private static let defaultExitID = "auto"

    /// Default broker selection — auto-probe picks by TCP-connect RTT.
    /// Existing users with a saved URL keep their explicit pick.
    private static let defaultBrokerID = "auto"

    /// Shared App Group container ID — for reading TunnelErrorReports
    /// the extension drops on fatal connect failure (auth/identity/etc.).
    private static let appGroupID = "group.ru.vertices"

    /// Surface a fatal extension error after disconnect. Reads the most
    /// recent `TunnelErrorReport` from the App Group container, sets
    /// `errorMessage` to its localized text, and stops the tunnel fully
    /// so iOS doesn't loop the on-demand restart against the same
    /// broken creds/exit/etc. — the user must hit Connect again after
    /// fixing Settings.
    private func handleDisconnectError() async {
        guard let report = TunnelErrorReport.read(appGroupID: Self.appGroupID) else { return }
        let age = Date().timeIntervalSince(report.timestamp)
        guard age < 60 else {
            TunnelErrorReport.clear(appGroupID: Self.appGroupID)
            return
        }
        log.warning("Surfacing tunnel error: \(report.kind.rawValue) — \(report.detail)")
        errorMessage = report.userMessage
        TunnelErrorReport.clear(appGroupID: Self.appGroupID)
        await tunnelManager.stop()
    }

    var isConnected: Bool {
        vpnStatus == .connected
    }

    var isTransitioning: Bool {
        vpnStatus == .connecting || vpnStatus == .disconnecting || vpnStatus == .reasserting
    }

    var statusText: String {
        switch vpnStatus {
        case .disconnected: "Not connected"
        case .connecting: "Connecting…"
        case .connected: "Connected"
        case .disconnecting: "Disconnecting…"
        case .reasserting: "Reconnecting…"
        case .invalid: "Not configured"
        @unknown default: "Unknown"
        }
    }

    var statusColor: Color {
        switch vpnStatus {
        case .connected: .stateConnected
        case .connecting, .disconnecting, .reasserting: .stateTransitioning
        case .disconnected: .stateDormant
        case .invalid: .stateError
        @unknown default: .stateDormant
        }
    }

    // MARK: - Lifecycle

    func loadState() async {
        log.info("loadState: starting")
        do {
            let status = try await tunnelManager.loadOrCreate()
            log.info("loadState: initial VPN status = \(String(describing: status.rawValue))")

            if status == .connecting || status == .reasserting {
                log.warning("loadState: stale connecting state, stopping tunnel")
                await tunnelManager.stop()
                vpnStatus = .disconnected
            } else {
                vpnStatus = status
            }

            NotificationCenter.default.addObserver(
                forName: .NEVPNStatusDidChange,
                object: nil,
                queue: .main
            ) { [weak self] _ in
                guard let self else { return }
                let newStatus = self.tunnelManager.currentStatus
                self.log.info("VPN status changed: \(String(describing: newStatus.rawValue))")
                self.vpnStatus = newStatus
                switch newStatus {
                case .connected:
                    self.startPolling()
                    self.startWifiPathMonitor()
                case .disconnected:
                    self.stopPolling()
                    self.stopWifiPathMonitor()
                    self.connectionStatus = nil
                    self.stats = nil
                    self.statsHistory.removeAll()
                    self.pingMs = nil
                    Task { await self.handleDisconnectError() }
                default:
                    break
                }
            }

            domain = UserDefaults.standard.string(forKey: "discoveryDomain") ?? "vertices.ru"

            if let cached = SRVDiscovery.loadCache() {
                availableBrokers = cached.brokerURLs
                availableExits = cached.exitIDs
                log.info("loadState: cached \(cached.brokerURLs.count) brokers, \(cached.exitIDs.count) exits")
            }

            if let savedBroker = UserDefaults.standard.string(forKey: "selectedBroker") {
                selectedBroker = savedBroker
            }
            if let savedExit = UserDefaults.standard.string(forKey: "selectedExit") {
                selectedExit = savedExit
            }

            if let config = tunnelManager.loadSavedConfig() {
                if let name = config["name"] as? String {
                    clientName = name
                }
            }

            // Seed from cache so the picker isn't empty before SRV resolves;
            // brokers/exits live in DNS, no hardcoded fallback list.
            if let cached = SRVDiscovery.loadCache() {
                availableBrokers = cached.brokerURLs
                availableExits = cached.exitIDs
                exitDisplayNames = cached.exitDisplayNames
            }
            validateSelections()

            await resolveSRV()

            if status == .connected {
                log.info("loadState: already connected, starting poll")
                startPolling()
                startWifiPathMonitor()
            }
        } catch {
            log.error("loadState error: \(error.localizedDescription)")
            errorMessage = error.localizedDescription
        }
    }

    // MARK: - Scene Phase

    func handleScenePhase(_ phase: ScenePhase) {
        switch phase {
        case .active:
            Task { await refreshLiveStatus() }
        case .background, .inactive:
            stopPolling()
        @unknown default:
            break
        }
    }

    /// Re-syncs `vpnStatus` with the live extension process. The extension keeps running
    /// while the host app is suspended in the background, but `NEVPNStatusDidChange`
    /// notifications are not delivered to a suspended app — so the cached status can be
    /// stale by the time we come back to foreground. Force-reload preferences and start
    /// polling unconditionally if the live status is connected.
    private func refreshLiveStatus() async {
        let live = await tunnelManager.reload()
        log.info("refreshLiveStatus: live=\(String(describing: live.rawValue))")
        vpnStatus = live
        if live == .connected {
            startPolling()
        }
    }

    // MARK: - SRV Discovery

    func resolveSRV() async {
        guard !domain.isEmpty else { return }
        // Coalesce concurrent callers (cold-start init + a rapid Connect tap)
        // onto a single DoH resolve. Both arrive on @MainActor, so reading +
        // assigning resolveTask serializes naturally without an explicit lock.
        if let existing = resolveTask {
            await existing.value
            return
        }
        let domain = self.domain
        let task: Task<Void, Never> = Task { [weak self] in
            await self?.performResolveSRV(domain: domain)
        }
        resolveTask = task
        await task.value
        resolveTask = nil
    }

    private func performResolveSRV(domain: String) async {
        log.info("resolveSRV: resolving \(domain)")
        if let result = await srvDiscovery.resolveWithFallback(domain: domain) {
            availableBrokers = result.brokerURLs
            availableExits = result.exitIDs
            exitDisplayNames = result.exitDisplayNames
            log.info("resolveSRV: \(result.brokerURLs.count) brokers, \(result.exitIDs.count) exits via \(result.domain)")
        } else {
            // No hardcoded fallback list — brokers/exits live in DNS. On a
            // clean install with no internet the lists stay empty until
            // SRV resolves; the Connect button surfaces "Discovery hasn't
            // resolved yet" instead of dispatching a doomed connect.
            log.warning("resolveSRV: failed, lists stay empty until next attempt")
        }

        validateSelections()
    }

    private func validateSelections() {
        // Same logic for both pickers: "auto" is always valid; an
        // explicit pick is overwritten only if the SRV list is populated
        // AND the saved value is no longer in it. Empty-list guard
        // protects a saved value during cold start before SRV resolves.
        if selectedBroker != "auto", !availableBrokers.isEmpty,
           !availableBrokers.contains(selectedBroker) {
            selectedBroker = Self.defaultBrokerID
        }
        if selectedExit != "auto", !availableExits.isEmpty,
           !availableExits.contains(selectedExit) {
            selectedExit = Self.defaultExitID
        }
    }

    // MARK: - Actions

    func toggleConnection() async {
        log.info("toggleConnection: vpnStatus=\(String(describing: self.vpnStatus.rawValue))")
        if isConnected || vpnStatus == .connecting {
            await disconnect()
        } else {
            await connect()
        }
    }

    func connect() async {
        log.info("connect: broker=\(self.selectedBroker), exit=\(self.selectedExit), name=\(self.clientName)")
        errorMessage = nil
        permissionDenied = false
        // Drop previous error so the banner doesn't outlive a fresh attempt.
        TunnelErrorReport.clear(appGroupID: Self.appGroupID)

        // Refuse early when SRV hasn't populated the broker list yet.
        // Without DEFAULT_BROKERS to fall back on, the parsed list would be
        // empty, the extension would start, fail config validation, and
        // surface a useless "configuration failed" error. Better to tell
        // the user discovery is still in flight and force a fresh resolve.
        if availableBrokers.isEmpty {
            errorMessage = "Discovery hasn't resolved yet — try again in a moment."
            await resolveSRV()
            return
        }
        do {
            // Pin the user's broker pick to the front of the list when
            // an explicit broker is chosen — extension skips the probe
            // and connects to it. When the user picked "auto", leave the
            // SRV order as-is; extension will probe and reorder. When the
            // saved URL is no longer in the SRV result (DNS removed or
            // renamed), the pin step no-ops and the extension uses SRV
            // order without probe — log so a "I picked YC but it didn't
            // connect to YC" report has a breadcrumb in device logs.
            var orderedURLs = availableBrokers
            if selectedBroker != "auto" {
                if let idx = orderedURLs.firstIndex(of: selectedBroker) {
                    orderedURLs.remove(at: idx)
                    orderedURLs.insert(selectedBroker, at: 0)
                } else {
                    log.warning("Saved broker \(self.selectedBroker, privacy: .public) not in SRV list — using SRV order")
                }
            }

            let password = (try? KeychainStore.loadPassword()) ?? ""
            log.info("connect: password \(password.isEmpty ? "EMPTY" : "present (\(password.count) chars)")")

            let parsedBrokers = orderedURLs.compactMap { BrokerURL(string: $0) }
            log.info("connect: parsed \(parsedBrokers.count) broker URLs")

            let defaults = UserDefaults(suiteName: RUNetsLoader.appGroupID) ?? .standard
            let splitOn = defaults.bool(forKey: "splitTunnelEnabled")
            let ruPath = RUNetsLoader.containerURL()?.path

            let config = TunnelConfig(
                brokerURLs: parsedBrokers,
                clientName: clientName,
                selectedExit: selectedExit,
                selectedBroker: selectedBroker,
                splitTunnelEnabled: splitOn,
                ruNetsPath: ruPath
            )

            try await tunnelManager.configure(config: config, password: password)
            try await tunnelManager.start()

            startPolling()
        } catch {
            log.error("connect error: \(error.localizedDescription)")
            let nsError = error as NSError
            // NEVPNError codes: 1 = configurationInvalid, 5 = configurationReadWriteFailed, 6 = configurationStale
            // Permission denial typically surfaces as configurationReadWriteFailed or .permissionDenied (not exposed publicly).
            if nsError.domain == NEVPNErrorDomain {
                permissionDenied = true
            }
            errorMessage = error.localizedDescription
        }
    }

    func disconnect() async {
        log.info("disconnect: stopping")
        stopPolling()
        await tunnelManager.stop()
        connectionStatus = nil
        stats = nil
        statsHistory.removeAll()
        pingMs = nil
    }

    // MARK: - IPC Polling

    private func startPolling() {
        stopPolling()
        pollTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                await self?.pollStatus()
            }
        }
        pingTimer = Timer.scheduledTimer(withTimeInterval: Self.pingInterval, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                await self?.measurePing()
            }
        }
        // Immediate first measurement so the user doesn't wait a full minute.
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(2))
            await self?.measurePing()
        }
    }

    private func stopPolling() {
        pollTimer?.invalidate()
        pollTimer = nil
        pingTimer?.invalidate()
        pingTimer = nil
        // pingMs intentionally NOT cleared here — stopPolling is called
        // on every scenePhase background transition, and clearing would
        // hide the last successful measurement until the next 60s
        // pingTimer fires after foregrounding. Disconnect/.disconnected
        // paths reset pingMs explicitly.
    }

    private func pollStatus() async {
        await poll(.requestStatus)
        await poll(.requestStats)
    }

    private func poll(_ message: AppMessage) async {
        guard let response = await tunnelManager.sendMessage(message) else { return }

        if let decoded = try? JSONDecoder().decode(ExtensionResponse.self, from: response) {
            switch decoded {
            case .status(let status):
                connectionStatus = status
            case .stats(let tunnelStats):
                stats = tunnelStats
                let sample = StatsSample(time: Date(), bytesUp: tunnelStats.bytesUp, bytesDown: tunnelStats.bytesDown)
                statsHistory.append(sample)
                let cutoff = sample.time.addingTimeInterval(-Self.historyHorizon)
                statsHistory.removeAll { $0.time < cutoff }
            case .error(let msg):
                errorMessage = msg
            }
        }
    }

    // MARK: - WiFi-prefer (host-app side)

    private func startWifiPathMonitor() {
        guard wifiPathMonitor == nil else { return }
        log.info("Starting host-app wifi path monitor")
        let monitor = NWPathMonitor(requiredInterfaceType: .wifi)
        wifiPathSatisfied = false
        monitor.pathUpdateHandler = { [weak self] path in
            guard let self else { return }
            let nowSatisfied = (path.status == .satisfied)
            Task { @MainActor [weak self] in
                guard let self else { return }
                let wasSatisfied = self.wifiPathSatisfied
                self.wifiPathSatisfied = nowSatisfied
                self.log.info("Host-app wifi path: status=\(String(describing: path.status), privacy: .public) wasSatisfied=\(wasSatisfied)")
                guard !wasSatisfied && nowSatisfied else { return }
                self.log.info("Host-app wifi path: unsatisfied → satisfied; notifying extension")
                _ = await self.tunnelManager.sendMessage(.notifyWifiAvailable)
            }
        }
        monitor.start(queue: wifiPathMonitorQueue)
        wifiPathMonitor = monitor
    }

    private func stopWifiPathMonitor() {
        guard let monitor = wifiPathMonitor else { return }
        log.info("Stopping host-app wifi path monitor")
        monitor.cancel()
        wifiPathMonitor = nil
        wifiPathSatisfied = false
    }

    // MARK: - Ping

    private var isPingInFlight = false

    private func measurePing() async {
        guard isConnected, !isPingInFlight else { return }
        isPingInFlight = true
        defer { isPingInFlight = false }
        let result = await TCPRTT.measure(host: Self.pingHost, port: Self.pingPort, timeout: Self.pingTimeout)
        switch result {
        case .success(let ms):
            pingMs = ms
        case .failure:
            // Keep the last successful pingMs visible — the value is
            // replaced when the next probe succeeds, or cleared on
            // disconnect.
            break
        }
    }
}
