package ru.vertices.android.core.crypto

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test
import ru.vertices.android.core.util.hexToBytes

/**
 * Cross-language test vectors. The same fixed inputs must produce the same
 * outputs in Go (`pkg/crypto`), Swift (`VertexCore.SessionCrypto`), and Kotlin
 * (this module). When an exit talks to one client across all three languages,
 * any divergence here means the tunnel won't decrypt.
 *
 * Vectors here are RFC test vectors (X25519 RFC 7748 §6.1) plus simple
 * derivable identities. Add more once Phase 1 wire-tests pass against
 * production exits.
 */
class CryptoVectorsTest {

    /** RFC 7748 §6.1 — X25519 ECDH between Alice and Bob. */
    @Test fun x25519_rfc7748_alice_bob_shared_secret() {
        val alicePriv = "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a".hexToBytes()
        val bobPub    = "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f".hexToBytes()
        val expected  = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742".hexToBytes()
        assertArrayEquals(expected, X25519.ecdh(alicePriv, bobPub))
    }

    /** RFC 7748 §6.1 — Bob's side derives the same shared secret. */
    @Test fun x25519_rfc7748_bob_alice_shared_secret_same() {
        val bobPriv  = "5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb".hexToBytes()
        val alicePub = "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a".hexToBytes()
        val expected = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742".hexToBytes()
        assertArrayEquals(expected, X25519.ecdh(bobPriv, alicePub))
    }

    /** Deriving public from private is deterministic. */
    @Test fun x25519_pubkey_derivation_is_deterministic() {
        val priv = "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a".hexToBytes()
        val expectedPub = "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a".hexToBytes()
        val kp = X25519.fromPrivateBytes(priv)
        assertArrayEquals(expectedPub, kp.publicKey)
    }

    /** HKDF-SHA256 known-vector (RFC 5869 test case 1). */
    @Test fun hkdf_rfc5869_test_case_1() {
        val ikm  = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b".hexToBytes()
        val salt = "000102030405060708090a0b0c".hexToBytes()
        val info = "f0f1f2f3f4f5f6f7f8f9".hexToBytes()
        val expected = ("3cb25f25faacd57a90434f64d0362f2a" +
                        "2d2d0a90cf1a5a4c5db02d56ecc4c5bf" +
                        "34007208d5b887185865").hexToBytes()
        val out = Hkdf.deriveSha256(ikm, salt, info, expected.size)
        assertArrayEquals(expected, out)
    }

    /** SessionCrypto round-trip — encrypt then decrypt yields the original plaintext. */
    @Test fun session_crypto_seal_open_round_trip() {
        // Random 32-byte key for the test (we test the API, not the KDF here).
        val key = ByteArray(32) { it.toByte() }
        val s = SessionCrypto.fromKey(key)
        val pt = "Vertex says: hello, world.".toByteArray()
        val ct = s.seal(pt)
        // Each seal produces fresh random nonce, so output != input and length grows by 28 (12 + 16).
        assertEquals(pt.size + ChaChaPoly.OVERHEAD, ct.size)
        assertArrayEquals(pt, s.open(ct))
    }

    /** SessionCrypto.fromDH — both parties derive the same key from their respective sides. */
    @Test fun session_crypto_from_dh_is_symmetric() {
        val client = X25519.generate()
        val exit   = X25519.generate()

        val clientSide = SessionCrypto.fromDH(
            myPrivateKey   = client.privateKey,
            theirPublicKey = exit.publicKey,
            clientPublicKey = client.publicKey,
            exitPublicKey   = exit.publicKey,
        )
        val exitSide = SessionCrypto.fromDH(
            myPrivateKey   = exit.privateKey,
            theirPublicKey = client.publicKey,
            clientPublicKey = client.publicKey,
            exitPublicKey   = exit.publicKey,
        )
        // What the exit seals, the client opens — and vice versa.
        val pt = "ping".toByteArray()
        assertArrayEquals(pt, clientSide.open(exitSide.seal(pt)))
        assertArrayEquals(pt, exitSide.open(clientSide.seal(pt)))
    }

    /** Identity proof — same input → same HMAC. Asymmetric: exit derives the same MAC
     *  using its own private key against the client's identity public key. */
    @Test fun identity_proof_matches_on_both_sides() {
        val identity = X25519.generate()
        val exit     = X25519.generate()
        val name     = "android-test"

        val client = IdentityKey(identity)
        val proof  = client.proof(exit.publicKey, name)

        // Verify on the exit side: HMAC(ECDH(exitPriv, identityPub), label+name)
        val expected = run {
            val shared = X25519.ecdh(exit.privateKey, identity.publicKey)
            val msg = (IdentityKey.LABEL + name).toByteArray()
            val mac = javax.crypto.Mac.getInstance("HmacSHA256")
            mac.init(javax.crypto.spec.SecretKeySpec(shared, "HmacSHA256"))
            mac.doFinal(msg)
        }
        assertArrayEquals(expected, proof)
    }
}
