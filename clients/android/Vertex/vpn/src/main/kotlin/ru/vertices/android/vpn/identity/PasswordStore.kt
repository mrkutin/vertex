package ru.vertices.android.vpn.identity

import android.content.Context
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Reads the MQTT broker password directly from the shared
 * [EncryptedSharedPreferences] file written by the app's `PasswordRepository`.
 *
 * Lives in `:vpn` so [VertexVpnService] can read the password without going
 * through `Intent` extras — passing secrets via Intent leaks them into
 * `ActivityManagerService` logs on debug builds and into bundles consumed by
 * system always-on-VPN restart hooks.
 *
 * The [PREFS_NAME] / [KEY_PASSWORD] / [MASTER_KEY_ALIAS] constants must stay
 * in lockstep with `ru.vertices.android.repository.PasswordRepository` in the
 * `:app` module — they intentionally point at the same encrypted prefs file.
 */
@Singleton
class PasswordStore @Inject constructor(
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

    fun get(): String = prefs.getString(KEY_PASSWORD, "") ?: ""

    companion object {
        // Must match ru.vertices.android.repository.PasswordRepository.
        private const val PREFS_NAME = "vtx_secret_prefs"
        private const val MASTER_KEY_ALIAS = "vtx-prefs-master"
        private const val KEY_PASSWORD = "mqtt_password"
    }
}
