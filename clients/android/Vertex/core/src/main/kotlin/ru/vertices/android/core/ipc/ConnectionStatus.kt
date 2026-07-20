package ru.vertices.android.core.ipc

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/** Coarse connection state. Mirror of Swift `ConnectionState`. */
@Serializable
enum class ConnectionState {
    @SerialName("disconnected") DISCONNECTED,
    @SerialName("connecting")   CONNECTING,
    @SerialName("handshaking")  HANDSHAKING,
    @SerialName("connected")    CONNECTED,
    @SerialName("reconnecting") RECONNECTING,
}

/**
 * Detailed status reported from the VpnService back to the UI. Same shape as
 * Swift `ConnectionStatus`, but on Android we share this through a singleton
 * StateFlow (Phase 1) — it never crosses a process boundary unless we move
 * VpnService to `:vpn` process in Phase 2.
 */
@Serializable
data class ConnectionStatus(
    val state: ConnectionState,
    val assignedIp: String? = null,
    val currentBroker: String? = null,
    val currentExit: String? = null,
    /** Epoch milliseconds when state transitioned to CONNECTED. */
    val connectedSinceEpochMs: Long? = null,
    val lastError: String? = null,
) {
    companion object {
        val DISCONNECTED: ConnectionStatus = ConnectionStatus(ConnectionState.DISCONNECTED)
    }
}
