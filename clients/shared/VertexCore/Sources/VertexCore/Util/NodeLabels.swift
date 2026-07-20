import Foundation

/// Vertex/Edge label helpers — graph-theoretic naming per design.
///
/// Vertices (brokers) → `V₀ · NAME`, edges (exit nodes) → `E₀ · NAME`.
/// Subscripts use Unicode code points U+2080…U+2089. The middle dot is U+00B7.
public enum NodeLabels {
    private static let subscripts: [String] = [
        "\u{2080}", "\u{2081}", "\u{2082}", "\u{2083}", "\u{2084}",
        "\u{2085}", "\u{2086}", "\u{2087}", "\u{2088}", "\u{2089}"
    ]

    /// Subscript glyph for index 0…9; falls back to plain digits beyond that.
    private static func subscriptGlyph(_ index: Int) -> String {
        index >= 0 && index < subscripts.count ? subscripts[index] : "\(index)"
    }

    /// E.g. ("mqtt-yc.vertices.ru", 0) → (shortName: "YC", code: "V₀ · YC")
    public static func vertexLabel(host: String, index: Int) -> (shortName: String, code: String) {
        let name = vertexShortName(forHost: host)
        let code = "V\(subscriptGlyph(index)) \u{00B7} \(name)"
        return (name, code)
    }

    /// E.g. ("sto", 0, "Stockholm, Sweden") → (display: "Stockholm, Sweden", code: "E₀ · STO").
    ///
    /// `displayOverride` carries the city/country string fetched from a TXT
    /// record on the SRV target host (see `SRVDiscoveryResult.exitDisplayNames`).
    /// When absent, the display falls back to uppercased ID — adding a new
    /// exit only requires SRV + TXT in DNS, never an app update.
    public static func edgeLabel(_ id: String, index: Int, displayOverride: String? = nil) -> (display: String, code: String) {
        let display: String = {
            if let override = displayOverride, !override.isEmpty { return override }
            return id.uppercased()
        }()
        let code = "E\(subscriptGlyph(index)) \u{00B7} \(id.uppercased())"
        return (display, code)
    }

    /// Unique vertex hosts in encounter order.
    /// E.g. ["mqtts://mqtt-yc...:8883", "wss://mqtt-yc...:443", "mqtts://mqtt-sber...:8883"]
    /// → ["mqtt-yc.vertices.ru", "mqtt-sber.vertices.ru"]
    public static func uniqueHosts(in urls: [String]) -> [String] {
        var seen = Set<String>()
        var result: [String] = []
        for url in urls {
            guard let host = host(of: url) else { continue }
            if seen.insert(host).inserted {
                result.append(host)
            }
        }
        return result
    }

    /// Schemes (uppercased: MQTTS, WSS, MQTT) for a given host across the URL list,
    /// preserving encounter order so primary protocol comes first.
    public static func protocols(forHost host: String, in urls: [String]) -> [String] {
        var seen = Set<String>()
        var result: [String] = []
        for url in urls {
            guard let h = self.host(of: url), h == host else { continue }
            guard let scheme = URLComponents(string: url)?.scheme?.uppercased() else { continue }
            if seen.insert(scheme).inserted {
                result.append(scheme)
            }
        }
        return result
    }

    /// Extracts the host component from a URL string, or nil if unparseable.
    public static func host(of url: String) -> String? {
        URLComponents(string: url)?.host
    }

    // MARK: - Internals

    /// Short name derived purely from the hostname — the first DNS label with
    /// any `mqtt-` prefix stripped, uppercased. `mqtt-yc.vertices.ru` → `YC`,
    /// `mqtt-sber.vertices.ru` → `SBER`, `broker.example.com` → `BROKER`.
    ///
    /// No special-case table: every broker the SRV record returns gets a
    /// sensible label without an app update, as long as ops follow the
    /// `mqtt-{shortname}.{zone}` naming convention.
    private static func vertexShortName(forHost host: String) -> String {
        let firstLabel = host.split(separator: ".").first.map(String.init) ?? host
        let stripped = firstLabel.lowercased().hasPrefix("mqtt-")
            ? String(firstLabel.dropFirst("mqtt-".count))
            : firstLabel
        return stripped.uppercased()
    }
}
