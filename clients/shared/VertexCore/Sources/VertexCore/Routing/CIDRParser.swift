import Foundation

/// Parses CIDR notation (e.g. "37.230.192.0/22") into (address, dotted-quad mask).
public enum CIDRParser {
    public struct Route: Sendable, Equatable {
        public let address: String
        public let mask: String
        public init(address: String, mask: String) {
            self.address = address
            self.mask = mask
        }
    }

    /// Parses one CIDR line. Returns nil for blanks, comments, or malformed input.
    public static func parse(_ line: String) -> Route? {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, !trimmed.hasPrefix("#") else { return nil }
        let parts = trimmed.split(separator: "/")
        guard parts.count == 2,
              let prefix = Int(parts[1]),
              prefix >= 0, prefix <= 32 else { return nil }
        let bits: UInt32 = prefix == 0 ? 0 : (UInt32.max << (32 - prefix)) & UInt32.max
        let mask = "\((bits >> 24) & 0xff).\((bits >> 16) & 0xff).\((bits >> 8) & 0xff).\(bits & 0xff)"
        return Route(address: String(parts[0]), mask: mask)
    }

    /// Parses a multi-line CIDR file (one entry per line).
    public static func parseAll(_ text: String) -> [Route] {
        var out: [Route] = []
        out.reserveCapacity(text.count / 16)
        for line in text.split(separator: "\n", omittingEmptySubsequences: true) {
            if let r = parse(String(line)) { out.append(r) }
        }
        return out
    }
}
