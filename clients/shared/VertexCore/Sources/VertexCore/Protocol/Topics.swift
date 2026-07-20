import Foundation

/// MQTT topic builders matching the Go implementation.
///
/// Topic structure:
/// - Data:    `vpn/{exit}/{name}/out` (client→exit), `vpn/{exit}/{name}/in` (exit→client)
/// - Control: `vpn/{exit}/control/join` (join request), `vpn/{exit}/{name}/control` (assign response)
/// - Discovery: `discovery/exits/{exit}` (exit heartbeats, retained)
public enum Topics {
    /// Client publishes IP packets upstream.
    /// `vpn/{exit}/{name}/out`
    public static func upload(exit: String, name: String) -> String {
        "vpn/\(exit)/\(name)/out"
    }

    /// Client subscribes to receive IP packets downstream.
    /// `vpn/{exit}/{name}/in`
    public static func download(exit: String, name: String) -> String {
        "vpn/\(exit)/\(name)/in"
    }

    /// Client subscribes to download from any exit (wildcard).
    /// `vpn/+/{name}/in`
    public static func downloadAny(name: String) -> String {
        "vpn/+/\(name)/in"
    }

    /// Client publishes join handshake.
    /// `vpn/{exit}/control/join`
    public static func join(exit: String) -> String {
        "vpn/\(exit)/control/join"
    }

    /// Client subscribes for control responses (assign, etc).
    /// `vpn/{exit}/{name}/control`
    public static func control(exit: String, name: String) -> String {
        "vpn/\(exit)/\(name)/control"
    }

    /// Client subscribes for control from any exit (wildcard).
    /// `vpn/+/{name}/control`
    public static func controlAny(name: String) -> String {
        "vpn/+/\(name)/control"
    }

    /// Exit publishes discovery heartbeats (retained).
    /// `discovery/exits/{exit}`
    public static func discovery(exit: String) -> String {
        "discovery/exits/\(exit)"
    }

    /// Subscribe to all exit discovery heartbeats.
    /// `discovery/exits/+`
    public static let discoveryAll = "discovery/exits/+"
}
