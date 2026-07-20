package ru.vertices.android.vpn.identity

import android.content.Context
import androidx.security.crypto.EncryptedFile
import androidx.security.crypto.MasterKey
import ru.vertices.android.core.crypto.IdentityKey
import ru.vertices.android.core.identity.IdentityKeyStore
import timber.log.Timber
import java.io.File

/**
 * Persists the device's X25519 identity private key to disk, encrypted with a
 * master key bound to the Android Keystore. Same strategy as WireGuard Android:
 * works on every API ≥ 26, gives us hardware-backed master key on devices that
 * support it (StrongBox / TEE) and a SW fallback elsewhere.
 *
 * Filename: `vtx-identity.bin` in the app's private files dir. Once read on
 * first call, the [IdentityKey] is cached in memory; subsequent calls reuse it.
 */
class KeystoreIdentityKeyStore(context: Context) : IdentityKeyStore {

    private val appContext = context.applicationContext
    private val file = File(appContext.filesDir, FILE_NAME)
    private val masterKey: MasterKey by lazy {
        MasterKey.Builder(appContext, MASTER_KEY_ALIAS)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            // setRequestStrongBoxBacked is best-effort — falls back transparently
            // to TEE / SW on devices without StrongBox.
            .setRequestStrongBoxBacked(true)
            .build()
    }

    @Volatile private var cached: IdentityKey? = null

    @Synchronized
    override fun loadOrCreate(): IdentityKey {
        cached?.let { return it }

        if (file.exists()) {
            try {
                val bytes = openEncrypted().openFileInput().use { it.readBytes() }
                if (bytes.size == 32) {
                    val key = IdentityKey.fromPrivateBytes(bytes)
                    cached = key
                    Timber.tag(TAG).i("loaded identity (pubkey ${key.publicKeyHex.take(16)}…)")
                    return key
                } else {
                    Timber.tag(TAG).w("identity file has unexpected length ${bytes.size}; regenerating")
                    file.delete()
                }
            } catch (t: Throwable) {
                Timber.tag(TAG).e(t, "failed to read identity file; regenerating")
                file.delete()
            }
        }

        val fresh = IdentityKey.generate()
        try {
            // EncryptedFile rejects writing to an existing path — already deleted above
            // on the bad-bytes / read-error fallback paths.
            openEncrypted().openFileOutput().use { it.write(fresh.privateKeyBytes) }
            cached = fresh
            Timber.tag(TAG).i("generated identity (pubkey ${fresh.publicKeyHex.take(16)}…)")
        } catch (t: Throwable) {
            Timber.tag(TAG).e(t, "failed to persist identity")
            // Continue with the fresh key in memory — better than failing the join,
            // even though it won't survive a process restart. User can retry connect.
            cached = fresh
        }
        return fresh
    }

    @Synchronized
    override fun reset() {
        cached = null
        if (file.exists()) {
            if (!file.delete()) Timber.tag(TAG).w("failed to delete identity file")
        }
    }

    private fun openEncrypted(): EncryptedFile = EncryptedFile.Builder(
        appContext,
        file,
        masterKey,
        EncryptedFile.FileEncryptionScheme.AES256_GCM_HKDF_4KB,
    ).build()

    companion object {
        private const val TAG = "vtx-id"
        private const val FILE_NAME = "vtx-identity.bin"
        private const val MASTER_KEY_ALIAS = "vtx-identity-master"
    }
}
