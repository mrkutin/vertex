import Foundation

/// VPN tunnel configuration passed from the app to the Network Extension
/// via NETunnelProviderProtocol.providerConfiguration.
public struct TunnelConfig: Sendable, Codable {
    /// Ordered list of broker URLs (failover order)
    public let brokerURLs: [BrokerURL]

    /// Client name (e.g. "macbook")
    public let clientName: String

    /// Selected exit node ID (e.g. "aws", "sto", "auto")
    public let selectedExit: String

    /// Selected broker URL (e.g. "mqtts://mqtt-yc.vertices.ru:8883",
    /// or "auto" to let the extension pick by client→broker TCP RTT).
    public let selectedBroker: String

    /// Last broker index that worked (for sticky reconnect)
    public var lastGoodBrokerIndex: Int?

    /// Split tunneling: when true, traffic to RU CIDRs bypasses the tunnel.
    /// Requires `ruNetsPath` to point to a readable CIDR list in App Group.
    public let splitTunnelEnabled: Bool

    /// Filesystem path to RU CIDR list (one entry per line). Typically lives in
    /// the App Group container so both the app and the extension can reach it.
    public let ruNetsPath: String?

    public init(
        brokerURLs: [BrokerURL],
        clientName: String,
        selectedExit: String,
        selectedBroker: String = "auto",
        lastGoodBrokerIndex: Int? = nil,
        splitTunnelEnabled: Bool = false,
        ruNetsPath: String? = nil
    ) {
        self.brokerURLs = brokerURLs
        self.clientName = clientName
        self.selectedExit = selectedExit
        self.selectedBroker = selectedBroker
        self.lastGoodBrokerIndex = lastGoodBrokerIndex
        self.splitTunnelEnabled = splitTunnelEnabled
        self.ruNetsPath = ruNetsPath
    }

    /// Create from NETunnelProviderProtocol.providerConfiguration dictionary.
    public init(providerConfiguration: [String: Any]?) throws {
        guard let config = providerConfiguration else {
            throw ConfigError.missingConfiguration
        }

        guard let brokerStrings = config["brokers"] as? [String], !brokerStrings.isEmpty else {
            throw ConfigError.missingBrokers
        }

        let parsed = brokerStrings.compactMap { BrokerURL(string: $0) }
        guard !parsed.isEmpty else {
            throw ConfigError.invalidBrokerURLs
        }

        guard let name = config["name"] as? String, !name.isEmpty else {
            throw ConfigError.missingClientName
        }

        self.brokerURLs = parsed
        self.clientName = name
        self.selectedExit = config["exit"] as? String ?? "auto"
        self.selectedBroker = config["broker"] as? String ?? "auto"
        self.lastGoodBrokerIndex = config["lastGoodBrokerIndex"] as? Int
        self.splitTunnelEnabled = (config["splitTunnelEnabled"] as? Bool) ?? false
        self.ruNetsPath = config["ruNetsPath"] as? String
    }

    /// Convert to providerConfiguration dictionary.
    public func toProviderConfiguration() -> [String: Any] {
        var config: [String: Any] = [
            "brokers": brokerURLs.map(\.urlString),
            "name": clientName,
            "exit": selectedExit,
            "broker": selectedBroker,
        ]
        if let idx = lastGoodBrokerIndex {
            config["lastGoodBrokerIndex"] = idx
        }
        config["splitTunnelEnabled"] = splitTunnelEnabled
        if let path = ruNetsPath {
            config["ruNetsPath"] = path
        }
        return config
    }

    /// All unique broker host IPs for route exclusion.
    public var brokerIPs: [String] {
        let allIPs = brokerURLs.flatMap { $0.resolveIPs() }
        return Array(Set(allIPs))
    }
}

public enum ConfigError: LocalizedError {
    case missingConfiguration
    case missingBrokers
    case invalidBrokerURLs
    case missingClientName

    public var errorDescription: String? {
        switch self {
        case .missingConfiguration: "No tunnel configuration provided"
        case .missingBrokers: "No broker URLs configured"
        case .invalidBrokerURLs: "All broker URLs are invalid"
        case .missingClientName: "Client name is required"
        }
    }
}
