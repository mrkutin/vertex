package ru.vertices.android.core.ipc

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * Error categories the tunnel surfaces to the UI on a fatal disconnect.
 * Mirror of Swift `TunnelErrorKind`.
 */
@Serializable
enum class TunnelErrorKind {
    @SerialName("authentication")      AUTHENTICATION,
    @SerialName("identity_rejected")   IDENTITY_REJECTED,
    @SerialName("discovery_timeout")   DISCOVERY_TIMEOUT,
    @SerialName("join_timeout")        JOIN_TIMEOUT,
    @SerialName("configuration")       CONFIGURATION,
    @SerialName("unknown")              UNKNOWN,
}

/**
 * Last fatal error from the tunnel. Persisted to disk by VpnService and read
 * by the host app when status flips back to DISCONNECTED.
 */
@Serializable
data class TunnelErrorReport(
    val kind: TunnelErrorKind,
    val detail: String = "",
    val timestampEpochMs: Long = System.currentTimeMillis(),
) {
    /** Localized user-facing message. Equivalent to Swift `userMessage`. */
    val userMessage: String
        get() = when (kind) {
            TunnelErrorKind.AUTHENTICATION ->
                "Authentication failed. Check Client name and Password in Settings → Identity. ($detail)"
            TunnelErrorKind.IDENTITY_REJECTED ->
                "The exit rejected this device's identity ($detail). Ask admin to reset TOFU for this device on the exit, then reconnect."
            TunnelErrorKind.DISCOVERY_TIMEOUT ->
                "Exit \"$detail\" is unreachable. Check Edge selection in Settings or try a different exit."
            TunnelErrorKind.JOIN_TIMEOUT ->
                "Exit \"$detail\" didn't respond to join. The exit may be down, or your Client name is not authorized."
            TunnelErrorKind.CONFIGURATION ->
                "Configuration error: $detail"
            TunnelErrorKind.UNKNOWN ->
                if (detail.isEmpty()) "Connection failed." else detail
        }
}
