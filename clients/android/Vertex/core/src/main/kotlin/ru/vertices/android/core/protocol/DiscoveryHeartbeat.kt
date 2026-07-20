package ru.vertices.android.core.protocol

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * Exit discovery heartbeat, published as RETAINED on `discovery/exits/{id}`.
 *
 * Field names match Go `cmd/exit` and Swift `VertexCore.DiscoveryHeartbeat`. The
 * snake_case fields (`max_clients`, `broker_rtt_ms`, `dh_pubkey`) are intentional
 * — they're the wire identifiers shared across implementations.
 */
@Serializable
data class DiscoveryHeartbeat(
    @SerialName("id")            val id: String,
    @SerialName("country")       val country: String? = null,
    @SerialName("clients")       val clients: Int? = null,
    @SerialName("max_clients")   val maxClients: Int? = null,
    @SerialName("broker_rtt_ms") val brokerRttMs: Map<String, Int>? = null,
    @SerialName("uptime")        val uptime: Long? = null,
    @SerialName("ts")            val ts: Long? = null,
    @SerialName("dh_pubkey")     val dhPubkey: String? = null,
)
