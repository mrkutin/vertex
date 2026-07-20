package ru.vertices.android.core.config

/**
 * VPN configuration handed from the host app to the VpnService. Mirror of Swift
 * `TunnelConfig` — but on Android we don't go through `NETunnelProviderProtocol`,
 * we serialize this through `Intent` extras when starting the service.
 *
 * `selectedExit = "auto"` means auto-select via discovery scoring.
 * `selectedBroker = "auto"` means probe TCP-RTT to every broker and reorder
 * by ascending latency before connecting; an explicit URL pins that broker
 * to the head of the failover list (probe is skipped, user choice honoured).
 */
data class TunnelConfig(
    val brokerUrls: List<BrokerUrl>,
    val clientName: String,
    val selectedExit: String,
    /** "auto" (probe + reorder) or one of the URLs from [brokerUrls]. */
    val selectedBroker: String = "auto",
    /** Sticky reconnect hint — index of last successful broker in [brokerUrls]. */
    val lastGoodBrokerIndex: Int? = null,
    val splitTunnelEnabled: Boolean = false,
    val ruNetsPath: String? = null,
) {
    init {
        require(brokerUrls.isNotEmpty()) { "at least one broker URL required" }
        require(clientName.isNotBlank()) { "client name must not be blank" }
        require(selectedExit.isNotBlank()) { "selected exit must not be blank (use \"auto\")" }
        require(selectedBroker.isNotBlank()) { "selected broker must not be blank (use \"auto\")" }
    }
}
