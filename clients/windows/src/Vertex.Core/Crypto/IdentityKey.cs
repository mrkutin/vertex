using System.Security.Cryptography;
using System.Text;

namespace Vertex.Core.Crypto;

/// <summary>
/// Persistent X25519 keypair for device identity (TOFU). Mirrors WireGuard-style
/// device-bound auth: the public key is registered on first connection and the
/// client proves ownership on every subsequent connect via an HMAC of an
/// ECDH-derived secret.
///
/// Wire format (parity with Go <c>pkg/identity</c>, Swift <c>IdentityKey</c>,
/// and Kotlin <c>IdentityKey</c>):
/// <code>
///     proof = HMAC-SHA256(ECDH(identity_priv, exit_pub),  "vtx-identity-v1" + name)
/// </code>
/// </summary>
public sealed class IdentityKey : IDisposable
{
    /// <summary>Wire-protocol identity HMAC label. NEVER rename — see MIGRATION.md.</summary>
    public const string Label = "vtx-identity-v1";

    private static readonly byte[] LabelBytes = Encoding.ASCII.GetBytes(Label);

    private readonly X25519KeyPair _keyPair;

    public IdentityKey(X25519KeyPair keyPair) => _keyPair = keyPair;

    /// <summary>Generate a fresh identity (used on first run).</summary>
    public static IdentityKey Generate() => new(X25519KeyPair.Generate());

    /// <summary>Reload from a persisted 32-byte private key.</summary>
    public static IdentityKey FromPrivateBytes(ReadOnlySpan<byte> privateBytes) =>
        new(X25519KeyPair.FromPrivateBytes(privateBytes));

    /// <summary>32-byte public key as a read-only view — what the exit registers in TOFU.</summary>
    public ReadOnlySpan<byte> PublicKey => _keyPair.PublicKey;

    /// <summary>Lowercase hex of the public key.</summary>
    public string PublicKeyHex => Convert.ToHexString(_keyPair.PublicKey).ToLowerInvariant();

    /// <summary>
    /// Raw 32-byte private key — what gets persisted to the keystore.
    /// Caller is responsible for wiping the returned buffer
    /// (<see cref="CryptographicOperations.ZeroMemory(Span{byte})"/>).
    /// </summary>
    public byte[] ExportPrivateBytes() => _keyPair.ExportPrivateBytes();

    /// <summary>
    /// Compute the identity proof for the join handshake.
    /// </summary>
    /// <param name="exitPublicKey">The exit's static X25519 public key (raw 32 bytes).</param>
    /// <param name="name">Our client name as it appears on the broker (e.g. <c>android-pixel</c>).</param>
    public byte[] Proof(ReadOnlySpan<byte> exitPublicKey, string name)
    {
        var shared = _keyPair.EcdhRaw(exitPublicKey);
        try
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var msg = new byte[LabelBytes.Length + nameBytes.Length];
            Buffer.BlockCopy(LabelBytes, 0, msg, 0, LabelBytes.Length);
            Buffer.BlockCopy(nameBytes, 0, msg, LabelBytes.Length, nameBytes.Length);

            // HMACSHA256.HashData(...) materialises a transient HMACSHA256
            // instance whose internal key buffer is zeroed on Dispose by the
            // BCL (verified against the reference source). The Kotlin port
            // notes this same pattern but cannot wipe SecretKeySpec — .NET
            // is slightly better here. Our local 'shared' copy is wiped in
            // the finally below.
            return HMACSHA256.HashData(shared, msg);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    public void Dispose() => _keyPair.Dispose();
}
