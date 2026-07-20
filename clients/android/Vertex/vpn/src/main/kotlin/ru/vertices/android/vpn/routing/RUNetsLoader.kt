package ru.vertices.android.vpn.routing

import android.content.Context
import android.net.IpPrefix
import timber.log.Timber
import java.io.File
import java.io.InputStream
import java.net.Inet4Address
import java.net.InetAddress

/**
 * Loads the RU aggregated CIDR list and parses it into [IpPrefix] entries
 * suitable for `VpnService.Builder.excludeRoute()`.
 *
 * Source resolution order (matches what `RuNetsRepository` in `:app` writes):
 *  1. `filesDir/ru-aggregated.zone` — present after the user has refreshed
 *     from ipdeny.com via the Settings screen. Fresh zones take effect on
 *     the next connect without rebuilding the app.
 *  2. APK asset `ru-aggregated.zone` — bundled fallback, frozen at build
 *     time. Used on first install and whenever the refreshed copy has been
 *     deleted (e.g. via Clear Storage).
 *
 * IPv4 only — the upstream zone is v4. Lines that fail to parse are skipped
 * silently; an empty list is returned on I/O errors so the caller can fall
 * back to full-tunnel routing without crashing.
 */
internal object RUNetsLoader {
    private const val TAG = "vtx-runets"
    private const val ASSET_NAME = "ru-aggregated.zone"

    /**
     * Hard cap on how many CIDRs we hand to `VpnService.Builder.excludeRoute`.
     *
     * The full ipdeny RU zone is ~8.5k entries. Each `IpPrefix` parcels to
     * roughly 140 bytes inside the [LinkProperties] that Android marshals
     * over Binder from `system_server` to the NetworkStack process for
     * validation; with the full set we hit `TransactionTooLargeException`
     * (1.2 MB > Binder's ~1 MB hard limit), NetworkMonitor never receives
     * `notifyNetworkConnected`, the VPN never gets the VALIDATED capability,
     * and the user sees the no-internet cross next to Wi-Fi.
     *
     * Sing-box-style clients (Hiddify) avoid this by doing split-tunnel
     * entirely in userspace inside their Go netstack — Builder gets a
     * single `0.0.0.0/0` route, the RU bypass logic runs against incoming
     * packets and is invisible to ConnectivityService. We do kernel-level
     * exclude routes for now and cap the count at a safe parcel budget;
     * sorting by prefix length keeps the largest aggregated blocks (most
     * IP coverage per route) and drops the long /22-/24 tail. Coverage
     * loss in practice is < 1% of RU IP space.
     */
    // Empirical: each IpPrefix marshals to ≈140 bytes inside the
    // LinkProperties Binder transaction. We saw `TransactionTooLargeException`
    // even at 3000 entries (≈420 KB) on Sony Xperia 5 V — Sony's
    // INetworkMonitor proxy enforces a tighter cap than the AOSP 1 MB.
    // 1500 keeps us under ≈210 KB which fits comfortably; coverage loss is
    // limited to the long /24 tail of the RU zone (the largest blocks
    // dominate IP-space-wise after sorting by prefix length).
    private const val MAX_EXCLUDE_ROUTES: Int = 1500

    fun load(context: Context): List<IpPrefix> {
        val out = ArrayList<IpPrefix>(8600)
        val (stream, sourceLabel) = openSource(context) ?: run {
            Timber.tag(TAG).w("no RU CIDR source available — split-tunnel will be no-op")
            return emptyList()
        }
        try {
            stream.bufferedReader().use { reader ->
                reader.lineSequence().forEach { raw ->
                    val line = raw.trim()
                    if (line.isEmpty() || line.startsWith("#")) return@forEach
                    val slash = line.indexOf('/')
                    if (slash <= 0) return@forEach
                    val host = line.substring(0, slash)
                    val prefix = line.substring(slash + 1).toIntOrNull() ?: return@forEach
                    if (prefix !in 0..32) return@forEach
                    runCatching {
                        val addr = InetAddress.getByName(host)
                        if (addr is Inet4Address) out.add(IpPrefix(addr, prefix))
                    }
                }
            }
            Timber.tag(TAG).i("parsed %d RU CIDRs from %s", out.size, sourceLabel)
        } catch (t: Throwable) {
            Timber.tag(TAG).w(t, "failed to load %s — split-tunnel will be no-op", sourceLabel)
            return emptyList()
        }
        if (out.size <= MAX_EXCLUDE_ROUTES) return out
        // Largest aggregated blocks first. /8 covers 16M addresses,
        // /24 covers 256 — keep the heavy hitters, drop the tail.
        out.sortBy { it.prefixLength }
        val capped = out.subList(0, MAX_EXCLUDE_ROUTES).toList()
        Timber.tag(TAG).i(
            "capped to %d largest CIDRs to keep LinkProperties under Binder transaction limit (was %d)",
            capped.size, out.size,
        )
        return capped
    }

    /**
     * Open the active CIDR source. Returns the stream and a human-readable
     * label for logs (`"filesDir/…"` vs `"asset:…"`) so split-tunnel logs make
     * it obvious which copy the engine is using.
     */
    private fun openSource(context: Context): Pair<InputStream, String>? {
        val refreshed = File(context.filesDir, ASSET_NAME)
        if (refreshed.exists() && refreshed.length() > 0) {
            return runCatching { refreshed.inputStream() to "filesDir/$ASSET_NAME" }
                .getOrNull() ?: openAsset(context)
        }
        return openAsset(context)
    }

    private fun openAsset(context: Context): Pair<InputStream, String>? = runCatching {
        context.assets.open(ASSET_NAME) to "asset:$ASSET_NAME"
    }.getOrNull()
}
