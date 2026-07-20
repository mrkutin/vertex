using Vertex.Core.Discovery;

namespace Vertex.Service.Storage;

/// <summary>
/// <see cref="ISrvCache"/> backed by <see cref="StateStore"/>'s
/// <c>state.json</c>. Keeps the SRV result alongside the rest of the
/// service state so admins can inspect discovery state with one file
/// open. Reads/writes are serialised by <see cref="StateStore"/>'s
/// internal lock — no extra synchronisation needed here.
/// </summary>
public sealed class StateStoreSrvCache : ISrvCache
{
    private readonly StateStore _store;

    public StateStoreSrvCache(StateStore store) => _store = store;

    public Task<SrvDiscoveryResult?> LoadAsync(CancellationToken ct = default)
    {
        var state = _store.Load<ServiceState>();
        return Task.FromResult(state?.LastSrv);
    }

    public Task SaveAsync(SrvDiscoveryResult result, CancellationToken ct = default)
    {
        var state = _store.Load<ServiceState>() ?? new ServiceState();
        var next = state with
        {
            LastSrv = result,
            SrvCacheRefreshedTicks = DateTime.UtcNow.Ticks,
        };
        _store.Save(next);
        return Task.CompletedTask;
    }
}
