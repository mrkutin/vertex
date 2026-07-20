package ru.vertices.android.repository

import android.content.Context
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Stores the MQTT broker password in [EncryptedSharedPreferences] (master key
 * via Android Keystore). Read/write is synchronous — keep prefs surface tiny;
 * it's a single string.
 *
 * Exposes a [StateFlow] mirror of the persisted value so any ViewModel that
 * displays the password observes mutations without keeping its own copy:
 * a previous design held a private MutableStateFlow inside SettingsViewModel
 * and only refreshed it from this repo on first construction, which made the
 * repo and the VM diverge if anything else (an instrumentation test, a
 * future "import config" flow, etc.) called [set] from outside.
 */
@Singleton
class PasswordRepository @Inject constructor(
    @ApplicationContext private val context: Context,
) {
    private val prefs by lazy {
        val key = MasterKey.Builder(context, MASTER_KEY_ALIAS)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()
        EncryptedSharedPreferences.create(
            context,
            PREFS_NAME,
            key,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
        )
    }

    private val _flow = MutableStateFlow("")
    val flow: StateFlow<String> = _flow.asStateFlow()

    /** Lazily seeds [flow] on first read. */
    fun get(): String {
        if (!seeded) {
            _flow.value = prefs.getString(KEY_PASSWORD, "") ?: ""
            seeded = true
        }
        return _flow.value
    }

    fun set(password: String) {
        prefs.edit().putString(KEY_PASSWORD, password).apply()
        _flow.value = password
        seeded = true
    }

    @Volatile private var seeded: Boolean = false

    companion object {
        private const val PREFS_NAME = "vtx_secret_prefs"
        private const val MASTER_KEY_ALIAS = "vtx-prefs-master"
        private const val KEY_PASSWORD = "mqtt_password"
    }
}
