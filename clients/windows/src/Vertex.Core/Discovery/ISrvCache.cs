namespace Vertex.Core.Discovery;

/// <summary>
/// Persistent storage for the last good <see cref="SrvDiscoveryResult"/>.
/// On Windows the implementation lives in <c>Vertex.Service.Storage</c>
/// and serializes into <c>%ProgramData%\Vertex\state.json</c>; tests
/// supply an in-memory fake. <see cref="SrvResolver"/> uses the cache for
/// the backup-domain and last-resort fallback paths — never as the
/// happy-path source.
/// </summary>
public interface ISrvCache
{
    Task<SrvDiscoveryResult?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(SrvDiscoveryResult result, CancellationToken ct = default);
}

/// <summary>No-op cache for use when persistence is unavailable (early bootstrap, tests).</summary>
public sealed class NullSrvCache : ISrvCache
{
    public static readonly NullSrvCache Instance = new();
    public Task<SrvDiscoveryResult?> LoadAsync(CancellationToken ct = default) => Task.FromResult<SrvDiscoveryResult?>(null);
    public Task SaveAsync(SrvDiscoveryResult result, CancellationToken ct = default) => Task.CompletedTask;
}
