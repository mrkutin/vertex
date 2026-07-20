package ru.vertices.android.vpn.routing

import android.net.IpPrefix
import java.net.InetAddress

/**
 * Operator-owned RU services that must ALWAYS bypass the tunnel — excluded from
 * routing regardless of the split-tunnel toggle and independent of the
 * [RUNetsLoader] 1500-route cap.
 *
 * Why this exists: [RUNetsLoader] caps the ipdeny RU zone at 1500 largest
 * blocks and drops the long /20–/24 tail. The mutter home server
 * (203.0.113.10) sits inside 176.106.144.0/20, which ranks past that cap, so
 * with split-tunnel on it gets tunnelled abroad anyway; with split-tunnel off
 * the whole `0.0.0.0/0` is tunnelled. Either way a RU-hosted service on the
 * operator's own infra is routed through a foreign exit and back to Russia —
 * pointless, and it fails when the home firewall does not accept the foreign
 * exit IP. The symptom is "the app can't reach its server while the VPN is on".
 *
 * These routes are excluded unconditionally so the operator's own RU services
 * stay directly reachable, VPN on or off, split on or off.
 *
 * Keep this list tiny and specific (prefer /32). Mirror of the iOS
 * `PriorityBypass` list in VertexCore — keep the two in sync.
 *
 * Note: `VpnService.Builder.excludeRoute` requires API 33+ (Tiramisu), same as
 * the RU split-tunnel exclusions; on older devices the bypass is a no-op and
 * the full tunnel applies.
 */
internal object PriorityBypassNets {
    // host -> prefix length
    private val ENTRIES = listOf(
        // api.mutter-app.ru / mutter home server. Tracks the home server's
        // public IP — update here if it ever changes.
        "203.0.113.10" to 32,
    )

    val cidrs: List<IpPrefix> by lazy {
        ENTRIES.mapNotNull { (host, len) ->
            runCatching { IpPrefix(InetAddress.getByName(host), len) }.getOrNull()
        }
    }
}
