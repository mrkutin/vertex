import VertexCore
@preconcurrency import NetworkExtension
import os

/// Manages NETunnelProviderManager — the bridge between app UI and the tunnel extension.
@MainActor
final class TunnelManager {
    private let log = Logger(subsystem: "ru.vertices", category: "tunnel-mgr")
    private var manager: NETunnelProviderManager?

    var currentStatus: NEVPNStatus {
        manager?.connection.status ?? .invalid
    }

    /// Force-reload preferences so `connection.status` reflects the live state of the
    /// extension process (which may have outlived the host app while it was suspended).
    func reload() async -> NEVPNStatus {
        guard let mgr = manager else {
            return (try? await loadOrCreate()) ?? .invalid
        }
        do {
            try await mgr.loadFromPreferences()
        } catch {
            log.warning("reload: loadFromPreferences failed: \(error.localizedDescription)")
        }
        return mgr.connection.status
    }

    func loadOrCreate() async throws -> NEVPNStatus {
        log.info("loadOrCreate: loading preferences...")
        let managers = try await NETunnelProviderManager.loadAllFromPreferences()
        log.info("loadOrCreate: found \(managers.count) existing VPN configurations")

        let mgr: NETunnelProviderManager
        if let existing = managers.first {
            mgr = existing
            log.info("loadOrCreate: using existing config, enabled=\(mgr.isEnabled), status=\(String(describing: mgr.connection.status.rawValue))")
            if let proto = mgr.protocolConfiguration as? NETunnelProviderProtocol {
                log.info("loadOrCreate: providerBundleID=\(proto.providerBundleIdentifier ?? "nil"), server=\(proto.serverAddress ?? "nil")")
            }
        } else {
            mgr = NETunnelProviderManager()
            mgr.localizedDescription = "Vertex"
            let proto = NETunnelProviderProtocol()
            proto.providerBundleIdentifier = "ru.vertices.tunnel"
            proto.serverAddress = "vertex"
            proto.disconnectOnSleep = false
            mgr.protocolConfiguration = proto
            log.info("loadOrCreate: created new VPN configuration")
        }

        self.manager = mgr
        return mgr.connection.status
    }

    func loadSavedConfig() -> [String: Any]? {
        guard let proto = manager?.protocolConfiguration as? NETunnelProviderProtocol else {
            return nil
        }
        return proto.providerConfiguration
    }

    func configure(config: TunnelConfig, password: String) async throws {
        if manager == nil {
            _ = try await loadOrCreate()
        }

        guard let mgr = manager else {
            log.error("configure: no manager")
            return
        }

        let proto = mgr.protocolConfiguration as? NETunnelProviderProtocol ?? NETunnelProviderProtocol()
        proto.providerBundleIdentifier = "ru.vertices.tunnel"
        proto.serverAddress = config.brokerURLs.first?.host ?? "vertex"
        proto.disconnectOnSleep = false

        var providerConfig = config.toProviderConfiguration()
        providerConfig["password"] = password
        proto.providerConfiguration = providerConfig

        mgr.protocolConfiguration = proto
        mgr.isEnabled = true

        // On-demand rule definition is persisted alongside the protocol
        // (cheap, idempotent), but `isOnDemandEnabled` is toggled in
        // start()/stop() so a user-initiated Disconnect doesn't get
        // immediately reversed by the rule.
        let connectRule = NEOnDemandRuleConnect()
        connectRule.interfaceTypeMatch = .any
        mgr.onDemandRules = [connectRule]

        log.info("configure: saving preferences (server=\(proto.serverAddress ?? "nil"), brokers=\(config.brokerURLs.count))...")
        try await mgr.saveToPreferences()
        log.info("configure: save done, reloading...")
        try await mgr.loadFromPreferences()
        log.info("configure: reload done, enabled=\(mgr.isEnabled), status=\(String(describing: mgr.connection.status.rawValue))")
    }

    func start() async throws {
        guard let mgr = manager else {
            log.error("start: no manager")
            throw TunnelManagerError.notConfigured
        }

        // Enable on-demand BEFORE starting so iOS will auto-restart the
        // extension after a PINGRESP-timeout-driven cancelTunnelWithError.
        // Without this, a wifi-off scenario leaves the tunnel wedged.
        if !mgr.isOnDemandEnabled {
            mgr.isOnDemandEnabled = true
            log.info("start: enabling on-demand and saving")
            try await mgr.saveToPreferences()
            try await mgr.loadFromPreferences()
        }

        log.info("start: calling startVPNTunnel (enabled=\(mgr.isEnabled), onDemand=\(mgr.isOnDemandEnabled), status=\(String(describing: mgr.connection.status.rawValue)))...")
        do {
            try mgr.connection.startVPNTunnel()
            log.info("start: startVPNTunnel returned OK")
        } catch {
            log.error("start: startVPNTunnel failed: \(error.localizedDescription)")
            throw error
        }
    }

    func stop() async {
        guard let mgr = manager else {
            log.info("stop: no manager")
            return
        }

        // Disable on-demand BEFORE stopping — otherwise iOS would treat
        // the disconnect as transient and immediately bring the tunnel
        // back up via the on-demand rule.
        if mgr.isOnDemandEnabled {
            mgr.isOnDemandEnabled = false
            log.info("stop: disabling on-demand and saving")
            do {
                try await mgr.saveToPreferences()
                try await mgr.loadFromPreferences()
            } catch {
                log.warning("stop: saveToPreferences failed: \(error.localizedDescription)")
            }
        }

        log.info("stop: calling stopVPNTunnel")
        mgr.connection.stopVPNTunnel()
    }

    func sendMessage(_ message: AppMessage) async -> Data? {
        guard let session = manager?.connection as? NETunnelProviderSession else {
            return nil
        }

        return await withCheckedContinuation { continuation in
            do {
                try session.sendProviderMessage(Data([message.rawValue])) { response in
                    continuation.resume(returning: response)
                }
            } catch {
                continuation.resume(returning: nil)
            }
        }
    }
}

enum TunnelManagerError: LocalizedError {
    case notConfigured

    var errorDescription: String? {
        switch self {
        case .notConfigured: "Vertex is not configured. Open Settings and set the discovery domain and client name first."
        }
    }
}
