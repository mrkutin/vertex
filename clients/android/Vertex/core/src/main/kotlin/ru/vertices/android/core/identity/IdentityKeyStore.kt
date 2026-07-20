package ru.vertices.android.core.identity

import ru.vertices.android.core.crypto.IdentityKey

/**
 * Persistent storage for the device's identity X25519 keypair.
 *
 * Pure Kotlin interface — concrete impls live in `:vpn` (`KeystoreIdentityKeyStore`,
 * which uses `EncryptedFile` + Android Keystore master key) and in tests
 * (an in-memory variant for unit tests).
 *
 * Single-instance contract: the store generates a key on first read if none
 * exists, then returns the same one across the lifetime of the app install.
 */
interface IdentityKeyStore {
    /** Load — or create on first call — this device's identity key. */
    fun loadOrCreate(): IdentityKey

    /** Drop the stored key. The next [loadOrCreate] generates a fresh keypair. */
    fun reset()
}
