package ru.vertices.android.repository

import android.content.Context
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import javax.inject.Inject
import javax.inject.Singleton

private val Context.settingsDataStore by preferencesDataStore("vtx_settings")

/**
 * Persistent UI/connection settings — discovery domain, client name, selected
 * broker, selected exit, and the split-tunnel toggle. Mirror of the iOS
 * `TunnelViewModel` `UserDefaults` slots + the `splitTunnelEnabled` App-Group
 * key.
 *
 * The MQTT password is held in [PasswordRepository] separately (encrypted).
 * The list of available brokers/exits comes from [SrvDiscovery] at runtime
 * — we do not persist that list here; we just remember which one the user
 * picked so we can restore it on cold start.
 */
@Singleton
class SettingsRepository @Inject constructor(
    @ApplicationContext private val context: Context,
) {
    private val store = context.settingsDataStore

    val domain: Flow<String> = store.data.map { it[KEY_DOMAIN] ?: DEFAULT_DOMAIN }
    val clientName: Flow<String> = store.data.map { it[KEY_NAME] ?: DEFAULT_NAME }
    val selectedBroker: Flow<String> = store.data.map { it[KEY_SELECTED_BROKER] ?: DEFAULT_BROKER }
    val selectedExit: Flow<String> = store.data.map { it[KEY_SELECTED_EXIT] ?: DEFAULT_EXIT }
    val splitTunnelEnabled: Flow<Boolean> = store.data.map { it[KEY_SPLIT] ?: false }
    /** Last successfully-connected exit ID. Persisted by the VPN service
     * after each successful connect. Read by the auto-resolve fallback
     * chain on next connect when the discovery tracker is empty. */
    val lastGoodExit: Flow<String?> = store.data.map { it[KEY_LAST_GOOD_EXIT] }

    suspend fun setDomain(value: String) {
        store.edit { it[KEY_DOMAIN] = value.trim().ifEmpty { DEFAULT_DOMAIN } }
    }
    suspend fun setClientName(value: String) {
        store.edit { it[KEY_NAME] = value.trim() }
    }
    suspend fun setSelectedBroker(value: String) {
        store.edit { it[KEY_SELECTED_BROKER] = value.trim().ifEmpty { DEFAULT_BROKER } }
    }
    suspend fun setSelectedExit(value: String) {
        store.edit { it[KEY_SELECTED_EXIT] = value.trim().ifEmpty { DEFAULT_EXIT } }
    }
    suspend fun setSplitTunnelEnabled(value: Boolean) {
        store.edit { it[KEY_SPLIT] = value }
    }
    suspend fun setLastGoodExit(value: String) {
        store.edit { it[KEY_LAST_GOOD_EXIT] = value.trim() }
    }

    /** Atomic snapshot used by [TunnelController.connect]. */
    data class Snapshot(
        val domain: String,
        val clientName: String,
        val selectedBroker: String,
        val selectedExit: String,
        val splitTunnel: Boolean,
        val lastGoodExit: String?,
    )

    suspend fun snapshot(): Snapshot = Snapshot(
        domain = domain.first(),
        clientName = clientName.first(),
        selectedBroker = selectedBroker.first(),
        selectedExit = selectedExit.first(),
        splitTunnel = splitTunnelEnabled.first(),
        lastGoodExit = lastGoodExit.first(),
    )

    companion object {
        private val KEY_DOMAIN = stringPreferencesKey("discovery_domain")
        private val KEY_NAME = stringPreferencesKey("client_name")
        private val KEY_SELECTED_BROKER = stringPreferencesKey("selected_broker")
        private val KEY_SELECTED_EXIT = stringPreferencesKey("selected_exit")
        private val KEY_SPLIT = booleanPreferencesKey("split_tunnel_enabled")
        private val KEY_LAST_GOOD_EXIT = stringPreferencesKey("last_good_exit")

        // The discovery domain is the only seed we hardcode. From it the
        // app pulls broker URLs, exit IDs, and per-exit display strings via
        // SRV/TXT lookups (`SrvDiscovery`); changing brokers or adding an
        // exit is a DNS-only operation, never a binary update.
        const val DEFAULT_DOMAIN: String = "vertices.ru"
        // Default for fresh installs is auto-resolve. Existing users with
        // a saved value (e.g. "aws"/"sto") keep their explicit pick across
        // upgrade — only `selectedExit` flows that miss the key fall back
        // to this constant.
        const val DEFAULT_EXIT: String = "auto"
        // Same auto-by-default rule for the broker pick. Existing users
        // with a saved URL keep their explicit pin; the TCP-RTT probe
        // runs only when the value is "auto".
        const val DEFAULT_BROKER: String = "auto"
        const val DEFAULT_NAME: String = "android"
    }
}
