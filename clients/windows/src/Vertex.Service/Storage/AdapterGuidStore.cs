namespace Vertex.Service.Storage;

/// <summary>
/// Persists a stable GUID for the WinTUN adapter under
/// <c>%ProgramData%\Vertex\adapter.guid</c>. Reusing the same GUID across
/// service restarts gives a stable adapter LUID — RouteManager and
/// DnsLeakGuard both bind by LUID, and a fresh-GUID-per-session would
/// strand stale routes / NRPT entries on a process crash.
///
/// File ACL is locked down to LocalSystem + Administrators by the
/// installer (Phase 5 WiX). At runtime the Service runs as LocalSystem,
/// so reads are unprivileged from its perspective; in development the
/// file inherits the user profile's ACL, which is fine.
/// </summary>
public static class AdapterGuidStore
{
    public static readonly string DefaultDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Vertex");

    public static readonly string DefaultPath =
        Path.Combine(DefaultDirectory, "adapter.guid");

    /// <summary>
    /// Load an existing GUID from disk, or generate a new one and persist
    /// it. Returns the GUID either way.
    /// </summary>
    public static Guid LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;

        if (File.Exists(path))
        {
            try
            {
                string raw = File.ReadAllText(path).Trim();
                if (Guid.TryParse(raw, out var existing))
                {
                    return existing;
                }
            }
            catch
            {
                // Fall through and rewrite — corrupt file is harmless,
                // it just means we get a new adapter.
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? DefaultDirectory);
        var fresh = Guid.NewGuid();
        File.WriteAllText(path, fresh.ToString("D"));
        return fresh;
    }
}
