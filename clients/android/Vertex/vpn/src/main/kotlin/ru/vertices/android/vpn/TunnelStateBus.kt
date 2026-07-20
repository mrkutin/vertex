package ru.vertices.android.vpn

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import ru.vertices.android.core.ipc.ConnectionStatus
import ru.vertices.android.core.ipc.TunnelStats

/**
 * Singleton bridge between VertexVpnService and the UI ViewModels. Phase 1 has
 * the VpnService running in the same OS process as the UI, so a process-local
 * StateFlow is sufficient. Phase 2 moves VpnService to `:vpn` process and this
 * bus gets replaced with a Messenger-backed implementation behind the same
 * read API.
 *
 * Marked `@Volatile`-style by virtue of `MutableStateFlow` being thread-safe.
 */
object TunnelStateBus {

    private val _status = MutableStateFlow(ConnectionStatus.DISCONNECTED)
    val status: StateFlow<ConnectionStatus> = _status.asStateFlow()

    private val _stats = MutableStateFlow(TunnelStats.ZERO)
    val stats: StateFlow<TunnelStats> = _stats.asStateFlow()

    internal fun publishStatus(s: ConnectionStatus) {
        _status.value = s
    }

    internal fun publishStats(s: TunnelStats) {
        _stats.value = s
    }

    /**
     * Drop a stale `lastError` carried over from a previous failed connect so
     * the StatusPill stops showing the old message once the user attempts a
     * new connection. Shape-preserves the rest of the status (state, ip,
     * broker, exit) — the new connect attempt will overwrite those itself.
     */
    fun clearLastError() {
        val cur = _status.value
        if (cur.lastError != null) _status.value = cur.copy(lastError = null)
    }

    /**
     * Surface a UI-side error before any service has been started — e.g.
     * the user tapped Connect while the SRV broker list was still resolving,
     * so the StatusPill should explain the no-op. Shape-preserves the rest
     * of the status; only [ConnectionStatus.lastError] changes.
     */
    fun setLastError(message: String) {
        val cur = _status.value
        if (cur.lastError != message) _status.value = cur.copy(lastError = message)
    }
}
