package ru.vertices.android.core.crypto

import org.bouncycastle.jce.provider.BouncyCastleProvider
import java.security.Security
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Make BouncyCastle available as a JCE provider. We only need it as a fallback —
 * X25519 and HKDF go through BC's lightweight API directly (see [X25519], [Hkdf])
 * and ChaCha20-Poly1305 prefers the platform `Cipher` on API 28+. BC is the
 * provider we explicitly request when the platform is too old to ship that
 * cipher (API 26-27).
 *
 * Idempotent — safe to call from anywhere; the provider is registered once.
 */
object BouncyCastleProviderInit {

    private val installed = AtomicBoolean(false)
    const val PROVIDER_NAME: String = BouncyCastleProvider.PROVIDER_NAME

    fun ensureInstalled() {
        if (installed.compareAndSet(false, true)) {
            // If a stripped-down BC is already present (some Android versions
            // ship a "Bouncy Castle" provider for jacasses), remove it first
            // so getInstance(..., "BC") resolves to our full implementation.
            if (Security.getProvider(PROVIDER_NAME) != null) {
                Security.removeProvider(PROVIDER_NAME)
            }
            // Insert at lower priority so the platform provider stays default
            // for everything BC isn't load-bearing for.
            Security.addProvider(BouncyCastleProvider())
        }
    }
}
