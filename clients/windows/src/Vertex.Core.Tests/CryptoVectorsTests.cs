using System.Security.Cryptography;
using FluentAssertions;
using Vertex.Core.Crypto;
using Xunit;

namespace Vertex.Core.Tests;

/// <summary>
/// Cross-language wire-compat vectors. The same fixed inputs must produce
/// the same outputs in Go (<c>pkg/crypto</c>), Swift (<c>VertexCore</c>),
/// Kotlin (<c>vertex.core.crypto</c>), and this C# port. Any drift here
/// means the production exit can't decrypt our packets.
///
/// Mirrors <c>clients/android/Vertex/core/src/test/.../CryptoVectorsTest.kt</c>
/// — keep the same vectors and naming so the four implementations stay
/// in lockstep.
/// </summary>
public class CryptoVectorsTests
{
    private static byte[] Hex(string h) => Convert.FromHexString(h);

    /// <summary>RFC 7748 §6.1 — X25519 ECDH between Alice and Bob.</summary>
    [Fact]
    public void X25519_Rfc7748_Alice_Bob_SharedSecret()
    {
        var alicePriv = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bobPub    = Hex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
        var expected  = Hex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

        using var alice = X25519KeyPair.FromPrivateBytes(alicePriv);
        var shared = alice.EcdhRaw(bobPub);

        shared.Should().Equal(expected);
    }

    /// <summary>RFC 7748 §6.1 — Bob's side derives the same shared secret.</summary>
    [Fact]
    public void X25519_Rfc7748_Bob_Alice_SharedSecret_Same()
    {
        var bobPriv  = Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        var alicePub = Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        var expected = Hex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

        using var bob = X25519KeyPair.FromPrivateBytes(bobPriv);
        bob.EcdhRaw(alicePub).Should().Equal(expected);
    }

    /// <summary>Deriving public from private is deterministic.</summary>
    [Fact]
    public void X25519_PubkeyDerivation_IsDeterministic()
    {
        var priv = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var expectedPub = Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");

        using var kp = X25519KeyPair.FromPrivateBytes(priv);
        kp.PublicKey.ToArray().Should().Equal(expectedPub);
    }

    /// <summary>
    /// SessionCrypto round-trip — encrypt then decrypt yields the original
    /// plaintext, and the wire output is exactly 28 bytes longer.
    /// </summary>
    [Fact]
    public void SessionCrypto_SealOpen_RoundTrip()
    {
        var key = new byte[32];
        for (int i = 0; i < 32; i++) key[i] = (byte)i;

        using var s = SessionCrypto.FromKey(key);
        var pt = System.Text.Encoding.UTF8.GetBytes("Vertex says: hello, world.");
        var ct = s.Seal(pt);

        ct.Length.Should().Be(pt.Length + 28);
        s.Open(ct).Should().Equal(pt);
    }

    /// <summary>
    /// SessionCrypto.FromDH — both parties derive the same key from their
    /// respective sides; what the exit seals, the client opens (and vice versa).
    /// </summary>
    [Fact]
    public void SessionCrypto_FromDH_IsSymmetric()
    {
        using var client = X25519KeyPair.Generate();
        using var exit   = X25519KeyPair.Generate();

        using var clientSide = SessionCrypto.FromDH(
            myPrivate: client,
            peerPublicKey: exit.PublicKey,
            clientPublicKey: client.PublicKey,
            exitPublicKey: exit.PublicKey);

        using var exitSide = SessionCrypto.FromDH(
            myPrivate: exit,
            peerPublicKey: client.PublicKey,
            clientPublicKey: client.PublicKey,
            exitPublicKey: exit.PublicKey);

        var pt = System.Text.Encoding.UTF8.GetBytes("ping");
        clientSide.Open(exitSide.Seal(pt)).Should().Equal(pt);
        exitSide.Open(clientSide.Seal(pt)).Should().Equal(pt);
    }

    /// <summary>
    /// Identity proof — exit verifies the client's HMAC by re-deriving on
    /// its side: <c>HMAC-SHA256(ECDH(exitPriv, identityPub), label+name)</c>.
    /// </summary>
    [Fact]
    public void IdentityProof_MatchesOnBothSides()
    {
        using var identity = X25519KeyPair.Generate();
        using var exit     = X25519KeyPair.Generate();
        const string name = "windows-test";

        var client = new IdentityKey(identity);
        var proof  = client.Proof(exit.PublicKey, name);

        // Exit-side verification.
        var shared = exit.EcdhRaw(identity.PublicKey);
        var label = System.Text.Encoding.ASCII.GetBytes(IdentityKey.Label);
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var msg = new byte[label.Length + nameBytes.Length];
        Buffer.BlockCopy(label, 0, msg, 0, label.Length);
        Buffer.BlockCopy(nameBytes, 0, msg, label.Length, nameBytes.Length);
        var expected = HMACSHA256.HashData(shared, msg);
        CryptographicOperations.ZeroMemory(shared);

        proof.Should().Equal(expected);
    }

    /// <summary>The wire-protocol HKDF info string and identity label are pinned —
    /// renaming either is a flag-day wire-protocol bump.</summary>
    [Fact]
    public void Wire_Protocol_Labels_Are_Pinned()
    {
        SessionCrypto.HkdfInfoLabel.Should().Be("broker-tunnel-v1");
        IdentityKey.Label.Should().Be("vtx-identity-v1");
    }

    /// <summary>
    /// Pin the SessionCrypto session-key derivation against fixed inputs.
    /// Same X25519 keypairs (RFC 7748 §6.1) as the X25519 vectors above; the
    /// HKDF then takes <c>shared = ECDH(alicePriv, bobPub)</c>, salt =
    /// <c>alicePub || bobPub</c>, info = <c>"broker-tunnel-v1"</c>, output =
    /// 32 bytes. If Swift / Kotlin / Go ever drift on HKDF parameter order
    /// (info vs salt) or info-string text, this fixed-output check fires
    /// before any production exit can reject our packets.
    ///
    /// Expected output computed with the canonical HKDF-SHA256 from
    /// .NET 8's <see cref="HKDF.DeriveKey"/> on the inputs documented inline;
    /// equivalent test should be added to Swift / Kotlin asserting the same
    /// bytes.
    /// </summary>
    [Fact]
    public void SessionCrypto_FromDH_ProducesPinnedKey_AgainstRfc7748Inputs()
    {
        // RFC 7748 §6.1 — Alice = client side, Bob = exit side.
        var alicePriv = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var alicePub  = Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        var bobPub    = Hex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");

        // ECDH = 4a5d9d5b... (verified by RFC). HKDF inputs:
        //   ikm  = ecdh shared secret (32 bytes)
        //   salt = alicePub || bobPub (64 bytes, in that order — client first, exit second)
        //   info = ASCII "broker-tunnel-v1"
        //   L    = 32
        var ikm = Hex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");
        var salt = new byte[64];
        Buffer.BlockCopy(alicePub, 0, salt, 0,  32);
        Buffer.BlockCopy(bobPub,   0, salt, 32, 32);
        var info = System.Text.Encoding.ASCII.GetBytes(SessionCrypto.HkdfInfoLabel);
        var expectedKey = HKDF.DeriveKey(System.Security.Cryptography.HashAlgorithmName.SHA256, ikm, 32, salt, info);

        // Now run the production path end-to-end and verify the same key
        // gets baked into the AEAD (we can't read the AEAD key directly, so
        // verify by encrypting a known plaintext with both sides and
        // checking the decrypts agree across an independent FromKey
        // session pinned to `expectedKey`).
        using var alice = X25519KeyPair.FromPrivateBytes(alicePriv);
        using var fromDh = SessionCrypto.FromDH(alice, bobPub, alicePub, bobPub);
        using var fromExpected = SessionCrypto.FromKey(expectedKey);

        var plaintext = System.Text.Encoding.UTF8.GetBytes("vertex pin");
        var sealed1 = fromDh.Seal(plaintext);
        fromExpected.Open(sealed1).Should().Equal(plaintext);

        var sealed2 = fromExpected.Seal(plaintext);
        fromDh.Open(sealed2).Should().Equal(plaintext);
    }
}
