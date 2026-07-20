using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Core.Crypto;

namespace Vertex.Service.Storage;

/// <summary>
/// Persists the device's X25519 identity keypair to disk under
/// <c>%ProgramData%\Vertex\identity.bin</c>. Confidentiality at rest is
/// provided by DPAPI scoped to <see cref="DataProtectionScope.LocalMachine"/>:
/// only code running on this machine (and, with the right ACL on the
/// file, the LocalSystem service account) can decrypt the blob.
///
/// Mirror of Swift Keychain-backed identity (`group.ru.vertices`) and
/// Android EncryptedFile + master-key. Wire-format identical: 32 raw
/// bytes (X25519 private key) → DPAPI-protected bytes → file.
/// </summary>
public sealed class IdentityStore
{
    public static readonly string DefaultDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Vertex");

    public static readonly string DefaultPath =
        Path.Combine(DefaultDirectory, "identity.bin");

    /// <summary>Optional entropy mixed into DPAPI's protect/unprotect — pin so a stolen blob can't be unsealed by other Vertex installs.</summary>
    private static readonly byte[] DpapiEntropy = System.Text.Encoding.ASCII.GetBytes("vertex-identity-v1");

    private readonly string _path;
    private readonly ILogger _log;

    public IdentityStore(string? path = null, ILogger? log = null)
    {
        _path = path ?? DefaultPath;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Load an existing identity from disk, or generate + persist a fresh
    /// one on first run. The returned key is owned by the caller — its
    /// disposal is the caller's responsibility.
    /// </summary>
    public IdentityKey LoadOrCreate()
    {
        // Clean up any sibling .tmp lingering from a kill-9 between
        // WriteAllBytes and Move (Phase 1.8 review MAJOR-5).
        DeleteTempIfPresent(_path);

        if (File.Exists(_path))
        {
            try
            {
                byte[] sealedBytes = File.ReadAllBytes(_path);
                byte[] privateBytes = ProtectedData.Unprotect(sealedBytes, DpapiEntropy, DataProtectionScope.LocalMachine);
                try
                {
                    return IdentityKey.FromPrivateBytes(privateBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(privateBytes);
                }
            }
            catch (Exception ex)
            {
                // Corrupt or unprotect-failed file (machine reimaged with
                // a different DPAPI master key) — log and regenerate. Old
                // exits will reject our re-TOFU; user must reset us on
                // each exit.
                _log.LogWarning(ex, "Identity file unreadable — regenerating");
            }
        }

        var fresh = IdentityKey.Generate();
        Save(fresh);
        return fresh;
    }

    /// <summary>Persist <paramref name="key"/> atomically to disk. Overwrites any existing file.</summary>
    public void Save(IdentityKey key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? DefaultDirectory);

        byte[] privateBytes = key.ExportPrivateBytes();
        try
        {
            byte[] sealedBytes = ProtectedData.Protect(privateBytes, DpapiEntropy, DataProtectionScope.LocalMachine);
            string tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, sealedBytes);
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBytes);
        }
    }

    /// <summary>Delete the persisted identity (forces re-TOFU on next connect).</summary>
    public void Reset()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) { _log.LogWarning(ex, "Identity reset failed"); }
    }

    internal static void DeleteTempIfPresent(string path)
    {
        string tmp = path + ".tmp";
        try { if (File.Exists(tmp)) File.Delete(tmp); }
        catch { /* best-effort */ }
    }
}
