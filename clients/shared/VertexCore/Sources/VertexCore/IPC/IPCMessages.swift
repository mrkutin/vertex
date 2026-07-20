import Foundation

/// Messages sent from the app to the Network Extension via sendProviderMessage.
public enum AppMessage: UInt8, Codable, Sendable {
    case requestStatus = 1
    case requestStats = 2
    /// Sent by the host app when its (unscoped) NWPathMonitor sees WiFi
    /// associate. The extension can't rely on its own NWPathMonitor for
    /// this signal because, when scoped to cellular, it does not get
    /// path:satisfied events with wifi as the default interface (Apple
    /// DTS — developer.apple.com/forums/thread/122711). On receiving
    /// this, the extension calls cancelTunnelWithError so the on-demand
    /// rule rebuilds it on the new best path (wifi).
    case notifyWifiAvailable = 3
    /// Sent by the macOS host app when its (unscoped) NWPathMonitor sees
    /// the *default* interface change (Ethernet plug/unplug, Wi-Fi
    /// network switch, Thunderbolt adapter come/go). On Mac both
    /// Ethernet and Wi-Fi are first-class, so a generic "the network
    /// just changed" signal is more useful than the iOS-specific
    /// `notifyWifiAvailable`. The extension responds by forcing a
    /// PINGREQ via `checkLiveness` — if the link is dead, the
    /// PINGRESP-timeout path takes over; if alive, the rebound socket
    /// continues unaffected. iOS does not send this case.
    case notifyPathChanged = 4
}

/// Connection state reported by the extension.
public enum ConnectionState: String, Codable, Sendable {
    case disconnected
    case connecting
    case handshaking
    case connected
    case reconnecting
}

/// Detailed status sent from the extension to the app.
public struct ConnectionStatus: Codable, Sendable {
    public let state: ConnectionState
    public let assignedIP: String?
    public let currentBroker: String?
    public let currentExit: String?
    public let connectedSince: Date?
    public let lastError: String?

    public init(
        state: ConnectionState,
        assignedIP: String? = nil,
        currentBroker: String? = nil,
        currentExit: String? = nil,
        connectedSince: Date? = nil,
        lastError: String? = nil
    ) {
        self.state = state
        self.assignedIP = assignedIP
        self.currentBroker = currentBroker
        self.currentExit = currentExit
        self.connectedSince = connectedSince
        self.lastError = lastError
    }
}

/// Traffic statistics sent from the extension to the app.
public struct TunnelStats: Codable, Sendable {
    public let bytesUp: UInt64
    public let bytesDown: UInt64
    public let packetsUp: UInt64
    public let packetsDown: UInt64

    public init(
        bytesUp: UInt64 = 0,
        bytesDown: UInt64 = 0,
        packetsUp: UInt64 = 0,
        packetsDown: UInt64 = 0
    ) {
        self.bytesUp = bytesUp
        self.bytesDown = bytesDown
        self.packetsUp = packetsUp
        self.packetsDown = packetsDown
    }
}

/// Response wrapper from extension to app.
public enum ExtensionResponse: Codable, Sendable {
    case status(ConnectionStatus)
    case stats(TunnelStats)
    case error(String)
}

// MARK: - User-facing tunnel error reporting

/// Categories of fatal connect failures the extension can detect. The host
/// app reads the most recent report from the App Group on disconnect and
/// surfaces a localized message via `userMessage`.
public enum TunnelErrorKind: String, Codable, Sendable {
    /// MQTT broker rejected our credentials (CONNACK reason 0x86 / 0x87 /
    /// 0x84 / 0x8C / 0x85). Means client name or password is wrong.
    case authentication
    /// Exit's join handshake came back with an `error` payload — typically
    /// "identity proof rejected" when TOFU sees a different pubkey for
    /// the same name. Resolution: admin resets the device on the exit.
    case identityRejected
    /// Discovery heartbeat for the chosen exit never arrived. Either the
    /// exit ID is wrong or that exit is offline.
    case discoveryTimeout
    /// Exit received our join but never replied with assign. Exit may be
    /// down between heartbeats, or admin removed the user from ACL.
    case joinTimeout
    /// `TunnelConfig` failed to materialize from `providerConfiguration`
    /// — bad broker URL, missing client name, etc.
    case configuration
    /// Keychain locked because device hasn't been unlocked since reboot.
    /// Resolution: unlock the iPhone with Face ID / passcode and reconnect.
    /// Hits when on-demand starts the extension before first user unlock.
    case keychainLocked
    /// Anything else surfaced as fatal.
    case unknown
}

/// Persisted last-error report from the extension. Stored in the App Group
/// so the host app can pick it up when the VPN status flips to disconnected.
public struct TunnelErrorReport: Codable, Sendable {
    public let kind: TunnelErrorKind
    /// Free-form context: the failing exit name, broker reasonString, etc.
    public let detail: String
    public let timestamp: Date

    public init(kind: TunnelErrorKind, detail: String = "", timestamp: Date = Date()) {
        self.kind = kind
        self.detail = detail
        self.timestamp = timestamp
    }

    public static let userDefaultsKey = "lastTunnelError"

    /// Write to the App Group container. Called by extension on fatal connect failure.
    public static func write(_ report: TunnelErrorReport, appGroupID: String) {
        guard let defaults = UserDefaults(suiteName: appGroupID),
              let data = try? JSONEncoder().encode(report) else { return }
        defaults.set(data, forKey: userDefaultsKey)
    }

    /// Read most recent report. Returns nil if none persisted.
    public static func read(appGroupID: String) -> TunnelErrorReport? {
        guard let defaults = UserDefaults(suiteName: appGroupID),
              let data = defaults.data(forKey: userDefaultsKey) else { return nil }
        return try? JSONDecoder().decode(TunnelErrorReport.self, from: data)
    }

    /// Drop the persisted report. Called by host app after surfacing the
    /// message and at the start of a fresh connect attempt.
    public static func clear(appGroupID: String) {
        UserDefaults(suiteName: appGroupID)?.removeObject(forKey: userDefaultsKey)
    }

    /// Localized human-readable message for the error banner.
    public var userMessage: String {
        switch kind {
        case .authentication:
            return "Authentication failed. Check Client name and Password in Settings → Identity. (\(detail))"
        case .identityRejected:
            return "The exit rejected this device's identity (\(detail)). Ask admin to reset TOFU for this device on the exit, then reconnect."
        case .discoveryTimeout:
            return "Exit \"\(detail)\" is unreachable. Check Edge selection in Settings or try a different exit."
        case .joinTimeout:
            return "Exit \"\(detail)\" didn't respond to join. The exit may be down, or your Client name is not authorized."
        case .configuration:
            return "Configuration error: \(detail)"
        case .keychainLocked:
            return "iPhone has not been unlocked since reboot — Vertex can't read its identity key. Unlock the device with Face ID or passcode, then tap Connect."
        case .unknown:
            return detail.isEmpty ? "Connection failed." : detail
        }
    }
}
