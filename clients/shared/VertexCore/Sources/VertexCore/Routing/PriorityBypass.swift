import Foundation

/// Operator-owned RU services that must ALWAYS bypass the tunnel — added to
/// `NEIPv4Settings.excludedRoutes` regardless of the split-tunnel toggle.
///
/// Why this exists: with split-tunnel off the whole `0.0.0.0/0` is tunnelled,
/// so a RU-hosted service on the operator's own infra is routed through a
/// foreign exit and back to Russia — pointless, and it fails when the home
/// firewall does not accept the foreign exit IP. The symptom is "the app can't
/// reach its server while the VPN is on". Excluding these routes
/// unconditionally keeps the operator's own RU services directly reachable,
/// VPN on or off, split on or off.
///
/// Keep this list tiny and specific (prefer /32). Mirror of the Android
/// `PriorityBypassNets` object — keep the two in sync.
public enum PriorityBypass {
    /// `(address, subnetMask)` pairs ready for `NEIPv4Route`.
    public static let routes: [(address: String, mask: String)] = [
        // api.mutter-app.ru / mutter home server. Tracks the home server's
        // public IP — update here if it ever changes.
        ("203.0.113.10", "255.255.255.255"),
    ]
}
