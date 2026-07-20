package ru.vertices.android.core.protocol

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * Join handshake. Published by the client to `vpn/{exit}/control/join`.
 *
 * Field names match Go `cmd/exit` and Swift `VertexCore.JoinMessage` byte-for-byte
 * — `id_sig` is intentionally snake_case on the wire.
 */
@Serializable
data class JoinMessage(
    @SerialName("name")   val name: String,
    /** Base64 of the client's ephemeral X25519 public key (32 bytes raw). */
    @SerialName("dh")     val dh: String,
    /** Base64 of the client's persistent X25519 identity public key (32 bytes raw). */
    @SerialName("id")     val id: String? = null,
    /** Base64 of the HMAC-SHA256 identity proof. See [ru.vertices.android.core.crypto.IdentityKey.proof]. */
    @SerialName("id_sig") val idSig: String? = null,
)
