using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vertex.Service.Storage;

/// <summary>
/// DPAPI-protected MQTT broker password. Stored under
/// <c>%ProgramData%\Vertex\password.bin</c> as
/// <see cref="ProtectedData.Protect"/>(<c>UTF-8(password)</c>) with
/// <see cref="DataProtectionScope.LocalMachine"/>.
///
/// Plaintext is held only momentarily on the LocalSystem service account's
/// stack. The host App passes the password in via the Service IPC
/// (<c>setPassword</c>); the App is expected to wipe its in-process copy
/// the moment <see cref="Set"/> returns — though Phase 1.10 will likely
/// deliver it as a <see cref="string"/> through the named pipe (string
/// interning leak — reviewer note from Phase 1.4).
/// </summary>
public sealed class PasswordStore
{
    public static readonly string DefaultPath =
        Path.Combine(IdentityStore.DefaultDirectory, "password.bin");

    private static readonly byte[] DpapiEntropy = Encoding.ASCII.GetBytes("vertex-password-v1");

    private readonly string _path;
    private readonly ILogger _log;

    public PasswordStore(string? path = null, ILogger? log = null)
    {
        _path = path ?? DefaultPath;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Read the persisted password. Returns <c>null</c> if absent or unsealable.</summary>
    public string? Get()
    {
        IdentityStore.DeleteTempIfPresent(_path);
        if (!File.Exists(_path)) return null;

        try
        {
            byte[] sealedBytes = File.ReadAllBytes(_path);
            byte[] plain = ProtectedData.Unprotect(sealedBytes, DpapiEntropy, DataProtectionScope.LocalMachine);
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Password file unreadable — returning null");
            return null;
        }
    }

    /// <summary>Persist <paramref name="password"/> atomically. Overwrites any existing file.</summary>
    public void Set(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? IdentityStore.DefaultDirectory);

        byte[] plain = Encoding.UTF8.GetBytes(password);
        try
        {
            byte[] sealedBytes = ProtectedData.Protect(plain, DpapiEntropy, DataProtectionScope.LocalMachine);
            string tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, sealedBytes);
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Delete the persisted password.</summary>
    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) { _log.LogWarning(ex, "Password clear failed"); }
    }
}
