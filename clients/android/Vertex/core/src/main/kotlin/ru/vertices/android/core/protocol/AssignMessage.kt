package ru.vertices.android.core.protocol

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * IP assignment response from the exit. Received on `vpn/{exit}/{name}/control`.
 *
 * Field names match Go `cmd/exit` and Swift `VertexCore.AssignMessage`.
 */
@Serializable
data class AssignMessage(
    /** Assigned client TUN IP, e.g. `"10.9.0.5"`. */
    @SerialName("ip")   val ip: String,
    /** Optional dotted-quad subnet mask, e.g. `"255.255.255.0"`. Present in newer exits. */
    @SerialName("mask") val mask: String? = null,
    /** Gateway TUN IP on the exit side, e.g. `"10.9.0.1"`. */
    @SerialName("gw")   val gw: String,
    /** Base64 of the exit's static X25519 DH public key (32 bytes raw). */
    @SerialName("dh")   val dh: String? = null,
)
