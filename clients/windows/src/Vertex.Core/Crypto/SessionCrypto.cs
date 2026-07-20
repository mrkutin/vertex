using System.Security.Cryptography;
using System.Text;

namespace Vertex.Core.Crypto;

/// <summary>
/// Per-session ChaCha20-Poly1305 derived from an X25519 ECDH agreement.
/// Wire format: <c>[12B random nonce][ciphertext][16B Poly1305 tag]</c> —
/// 28 bytes overhead per packet, byte-for-byte identical to Go
/// <c>pkg/crypto</c>, Swift <c>SessionCrypto</c>, and Kotlin
/// <c>SessionCrypto</c>.
/// </summary>
public sealed class SessionCrypto : IDisposable
{
    /// <summary>Wire-protocol HKDF info string. NEVER rename — see MIGRATION.md.</summary>
    public const string HkdfInfoLabel = "broker-tunnel-v1";

    private static readonly byte[] HkdfInfo = Encoding.ASCII.GetBytes(HkdfInfoLabel);

    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    private readonly IAead _aead;

    private SessionCrypto(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException("Session key must be 32 bytes.", nameof(key));
        }

        // .NET 8 System.Security.Cryptography.ChaCha20Poly1305 is unavailable on
        // Windows builds older than 20348 (Server 2022 / Win 11 22H2). Fall back
        // to libsodium via NSec on those — wire format is identical (CT||TAG).
        Span<byte> tmp = stackalloc byte[KeyBytes];
        key.CopyTo(tmp);
        try
        {
            _aead = ChaCha20Poly1305.IsSupported
                ? new NativeAead(tmp)
                : new SodiumAead(tmp);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tmp);
        }
    }

    /// <summary>
    /// Derive a session from an X25519 ECDH shared secret.
    /// </summary>
    /// <param name="myPrivate">Our (client ephemeral) X25519 keypair.</param>
    /// <param name="peerPublicKey">Peer (exit static) X25519 public key, raw 32 bytes.</param>
    /// <param name="clientPublicKey">Raw 32 bytes — client side of the salt.</param>
    /// <param name="exitPublicKey">Raw 32 bytes — exit side of the salt.</param>
    /// <remarks>
    /// Salt is <c>clientPublicKey || exitPublicKey</c> (deterministic ordering —
    /// both sides agree on direction without negotiating).
    /// </remarks>
    public static SessionCrypto FromDH(
        X25519KeyPair myPrivate,
        ReadOnlySpan<byte> peerPublicKey,
        ReadOnlySpan<byte> clientPublicKey,
        ReadOnlySpan<byte> exitPublicKey)
    {
        if (clientPublicKey.Length != 32 || exitPublicKey.Length != 32)
        {
            throw new ArgumentException("Client/exit pubkeys must be 32 bytes for the HKDF salt.");
        }

        Span<byte> salt = stackalloc byte[64];
        clientPublicKey.CopyTo(salt);
        exitPublicKey.CopyTo(salt[32..]);

        var derived = myPrivate.DeriveHkdfSha256(peerPublicKey, salt, HkdfInfo, KeyBytes);
        try
        {
            return new SessionCrypto(derived);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    /// <summary>
    /// Test / diagnostic constructor. Internal so production paths must use
    /// <see cref="FromDH"/> (the wire-protocol entry point). Visible to
    /// <c>Vertex.Core.Tests</c> via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal static SessionCrypto FromKey(ReadOnlySpan<byte> key) => new(key);

    /// <summary>
    /// Encrypt one packet. Output length = <c>plaintext.Length + 28</c>.
    /// </summary>
    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[NonceBytes + plaintext.Length + TagBytes];
        var nonce = output.AsSpan(0, NonceBytes);
        var ciphertext = output.AsSpan(NonceBytes, plaintext.Length);
        var tag = output.AsSpan(NonceBytes + plaintext.Length, TagBytes);

        RandomNumberGenerator.Fill(nonce);
        _aead.Encrypt(nonce, plaintext, ciphertext, tag);
        return output;
    }

    /// <summary>Decrypt one packet from wire format.</summary>
    public byte[] Open(ReadOnlySpan<byte> combined)
    {
        if (combined.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("Ciphertext shorter than 28-byte AEAD overhead.");
        }

        int plaintextLength = combined.Length - NonceBytes - TagBytes;
        var nonce = combined[..NonceBytes];
        var ciphertext = combined.Slice(NonceBytes, plaintextLength);
        var tag = combined.Slice(NonceBytes + plaintextLength, TagBytes);

        var plaintext = new byte[plaintextLength];
        _aead.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public void Dispose() => _aead.Dispose();

    // ──────────────────────────────────────────────────────────────────────
    // AEAD abstraction so we can swap native (BCrypt) for libsodium when
    // the OS doesn't expose ChaCha20-Poly1305 through CNG. Both implement
    // the same contract — separate ciphertext / tag buffers like the
    // System.Security.Cryptography API.
    // ──────────────────────────────────────────────────────────────────────

    private interface IAead : IDisposable
    {
        void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag);
        void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext);
    }

    private sealed class NativeAead : IAead
    {
        private readonly ChaCha20Poly1305 _impl;
        public NativeAead(ReadOnlySpan<byte> key) => _impl = new ChaCha20Poly1305(key);
        public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> pt, Span<byte> ct, Span<byte> tag)
            => _impl.Encrypt(nonce, pt, ct, tag);
        public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ct, ReadOnlySpan<byte> tag, Span<byte> pt)
            => _impl.Decrypt(nonce, ct, tag, pt);
        public void Dispose() => _impl.Dispose();
    }

    /// <summary>
    /// libsodium-backed ChaCha20-Poly1305 (IETF nonce, 12 bytes). Used as
    /// fallback on Windows builds older than 20348 where CNG doesn't expose
    /// the algorithm. Loads <c>libsodium</c> via NSec, which already ships
    /// the native binary as a transitive dependency of Vertex.Core.
    /// </summary>
    private sealed class SodiumAead : IAead
    {
        private readonly NSec.Cryptography.Key _key;
        private static readonly NSec.Cryptography.AeadAlgorithm Algo =
            NSec.Cryptography.AeadAlgorithm.ChaCha20Poly1305;

        public SodiumAead(ReadOnlySpan<byte> key)
        {
            // NSec stores the key in libsodium-locked memory and does not
            // retain a managed reference to the input span.
            _key = NSec.Cryptography.Key.Import(
                Algo,
                key,
                NSec.Cryptography.KeyBlobFormat.RawSymmetricKey);
        }

        public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> pt, Span<byte> ct, Span<byte> tag)
        {
            // NSec writes ciphertext||tag into a single output buffer.
            Span<byte> combined = ct.Length + tag.Length <= 4096
                ? stackalloc byte[ct.Length + tag.Length]
                : new byte[ct.Length + tag.Length];
            Algo.Encrypt(_key, nonce, ReadOnlySpan<byte>.Empty, pt, combined);
            combined[..ct.Length].CopyTo(ct);
            combined.Slice(ct.Length, tag.Length).CopyTo(tag);
        }

        public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ct, ReadOnlySpan<byte> tag, Span<byte> pt)
        {
            Span<byte> combined = ct.Length + tag.Length <= 4096
                ? stackalloc byte[ct.Length + tag.Length]
                : new byte[ct.Length + tag.Length];
            ct.CopyTo(combined);
            tag.CopyTo(combined[ct.Length..]);
            if (!Algo.Decrypt(_key, nonce, ReadOnlySpan<byte>.Empty, combined, pt))
            {
                throw new CryptographicException("ChaCha20-Poly1305 authentication failed.");
            }
        }

        public void Dispose() => _key.Dispose();
    }
}
