import Foundation

/// Parsed broker URL supporting mqtt(s):// and ws(s):// schemes.
public struct BrokerURL: Sendable, Equatable, Codable {
    public let scheme: Scheme
    public let host: String
    public let port: Int

    public enum Scheme: String, Sendable, Codable {
        case mqtt
        case mqtts
        case ws
        case wss
    }

    public var isWebSocket: Bool {
        scheme == .wss || scheme == .ws
    }

    public var isTLS: Bool {
        scheme == .mqtts || scheme == .wss
    }

    /// Original URL string (e.g. "mqtts://mqtt-yc.vertices.ru:8883")
    public var urlString: String {
        "\(scheme.rawValue)://\(host):\(port)"
    }

    public init(scheme: Scheme, host: String, port: Int) {
        self.scheme = scheme
        self.host = host
        self.port = port
    }

    /// Parse a broker URL string.
    ///
    /// Supported formats:
    /// - `mqtt://host:port` (default port 1883)
    /// - `mqtts://host:port` (default port 8883)
    /// - `ws://host:port` (default port 80)
    /// - `wss://host:port` (default port 443)
    public init?(string: String) {
        guard let url = URL(string: string),
              let schemeStr = url.scheme,
              let scheme = Scheme(rawValue: schemeStr),
              let host = url.host, !host.isEmpty else {
            return nil
        }

        let defaultPort: Int
        switch scheme {
        case .mqtt: defaultPort = 1883
        case .mqtts: defaultPort = 8883
        case .ws: defaultPort = 80
        case .wss: defaultPort = 443
        }
        self.scheme = scheme
        self.host = host
        self.port = url.port ?? defaultPort
    }

    /// Resolve the broker hostname to IPv4 addresses for route exclusion.
    public func resolveIPs() -> [String] {
        var hints = addrinfo()
        hints.ai_family = AF_INET
        hints.ai_socktype = SOCK_STREAM

        var result: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, nil, &hints, &result) == 0, let first = result else {
            return []
        }
        defer { freeaddrinfo(first) }

        var ips: Set<String> = []
        var current: UnsafeMutablePointer<addrinfo>? = first
        while let entry = current {
            if entry.pointee.ai_family == AF_INET,
               let addr = entry.pointee.ai_addr {
                addr.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { sin in
                    var ip = sin.pointee.sin_addr
                    var buf = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
                    inet_ntop(AF_INET, &ip, &buf, socklen_t(INET_ADDRSTRLEN))
                    let idx = buf.firstIndex(of: 0) ?? buf.endIndex
                    ips.insert(String(decoding: buf[..<idx].map { UInt8(bitPattern: $0) }, as: UTF8.self))
                }
            }
            current = entry.pointee.ai_next
        }
        return Array(ips)
    }
}
