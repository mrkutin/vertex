using NSec.Cryptography;

namespace Vertex.Core.Crypto;

/// <summary>
/// X25519 key-agreement keypair. Thin wrapper around NSec.Cryptography
/// (libsodium) — .NET 8's built-in cryptography has no X25519 primitive.
///
/// Wire format: 32 raw bytes for both private and public keys, matching
/// Swift CryptoKit <c>Curve25519.KeyAgreement.PrivateKey</c> and
/// Kotlin BouncyCastle <c>X25519</c>.
/// </summary>
public sealed class X25519KeyPair : IDisposable
{
    private static readonly KeyAgreementAlgorithm Algo;
    private static readonly KeyDerivationAlgorithm2 Hkdf;

    static X25519KeyPair()
    {
        // The DllImport resolver MUST be registered before any NSec member
        // is touched. Field initializers run before this body, so static
        // fields are intentionally assigned here, not at declaration.
        LibsodiumLoader.Initialize();
        Algo = KeyAgreementAlgorithm.X25519;
        Hkdf = KeyDerivationAlgorithm2.HkdfSha256;
    }

    /// <summary>32-byte raw public key, exposed as a read-only view to prevent
    /// accidental mutation that would silently break subsequent ECDH derivations.</summary>
    public ReadOnlySpan<byte> PublicKey => _publicKey;

    /// <summary>Backing array for <see cref="PublicKey"/>. Owned by this instance; never returned directly.</summary>
    private readonly byte[] _publicKey;

    private readonly Key _key;

    private X25519KeyPair(Key key)
    {
        _key = key;
        _publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    /// <summary>Generate a fresh ephemeral keypair.</summary>
    public static X25519KeyPair Generate()
    {
        var k = Key.Create(Algo, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        return new X25519KeyPair(k);
    }

    /// <summary>Reload a persistent identity keypair from its 32-byte private seed.</summary>
    public static X25519KeyPair FromPrivateBytes(ReadOnlySpan<byte> privateBytes)
    {
        if (privateBytes.Length != 32)
        {
            throw new ArgumentException("X25519 private key must be 32 bytes.", nameof(privateBytes));
        }

        var k = Key.Import(Algo, privateBytes, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        return new X25519KeyPair(k);
    }

    /// <summary>
    /// Export the 32-byte raw private key. Defensive: caller owns the
    /// returned buffer and is expected to wipe it (CryptographicOperations.ZeroMemory)
    /// when done. Used only by IdentityStore on persistent save.
    /// </summary>
    public byte[] ExportPrivateBytes() => _key.Export(KeyBlobFormat.RawPrivateKey);

    /// <summary>
    /// Compute the raw 32-byte ECDH shared secret with a peer's public key.
    /// Mirrors Swift <c>sharedSecretFromKeyAgreement</c> +
    /// <c>SharedSecret.withUnsafeBytes</c>, and Kotlin <c>X25519.ecdh</c>.
    /// Caller is expected to wipe the returned buffer after use.
    /// </summary>
    public byte[] EcdhRaw(ReadOnlySpan<byte> peerPublicKey)
    {
        if (peerPublicKey.Length != 32)
        {
            throw new ArgumentException("Peer public key must be 32 bytes.", nameof(peerPublicKey));
        }

        var peer = NSec.Cryptography.PublicKey.Import(Algo, peerPublicKey, KeyBlobFormat.RawPublicKey);

        // NSec withholds raw shared-secret export by default; opt in here
        // because the IdentityKey HMAC path needs the raw 32 bytes.
        var creationParams = new SharedSecretCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        };
        using var shared = Algo.Agree(_key, peer, creationParams)
            ?? throw new System.Security.Cryptography.CryptographicException("X25519 agreement returned null (invalid peer key).");

        return shared.Export(SharedSecretBlobFormat.RawSharedSecret);
    }

    /// <summary>
    /// HKDF-SHA256 derive a fixed-length key directly from the X25519
    /// agreement, without materialising the raw 32-byte shared secret.
    /// NSec keeps the secret inside libsodium-locked memory, which is
    /// preferable to round-tripping through a managed byte[] when the
    /// caller doesn't need the raw bytes (i.e. session-key derivation).
    /// </summary>
    public byte[] DeriveHkdfSha256(
        ReadOnlySpan<byte> peerPublicKey,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> info,
        int outputBytes)
    {
        if (peerPublicKey.Length != 32)
        {
            throw new ArgumentException("Peer public key must be 32 bytes.", nameof(peerPublicKey));
        }

        var peer = NSec.Cryptography.PublicKey.Import(Algo, peerPublicKey, KeyBlobFormat.RawPublicKey);
        using var shared = Algo.Agree(_key, peer)
            ?? throw new System.Security.Cryptography.CryptographicException("X25519 agreement returned null (invalid peer key).");

        return Hkdf.DeriveBytes(shared, salt, info, outputBytes);
    }

    public void Dispose() => _key.Dispose();
}
