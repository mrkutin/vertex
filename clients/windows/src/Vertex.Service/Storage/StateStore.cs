using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Core.Discovery;

namespace Vertex.Service.Storage;

/// <summary>
/// Non-secret service state persisted as JSON under
/// <c>%ProgramData%\Vertex\state.json</c>. Holds:
/// last successful exit (sticky preference for next start), user
/// settings (split tunnel toggle, broker / exit overrides), cached
/// SRV records.
///
/// Confidentiality is NOT a goal — anything sensitive (identity,
/// password) lives in <see cref="IdentityStore"/> /
/// <see cref="PasswordStore"/>. This file's threat model is "service
/// crashes mid-write" — we use atomic temp+rename, never partial-write
/// the live file.
/// </summary>
public sealed class StateStore
{
    public static readonly string DefaultPath =
        Path.Combine(IdentityStore.DefaultDirectory, "state.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger _log;

    public StateStore(string? path = null, ILogger? log = null)
    {
        _path = path ?? DefaultPath;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Serialises Load + Save. Phase 1.8 review MAJOR-7 — TunnelEngine and PipeServer handlers can both touch state, lost-update protection.</summary>
    private readonly object _lock = new();

    /// <summary>Load state from disk; return <c>null</c> if file missing or unparseable.</summary>
    public T? Load<T>() where T : class
    {
        lock (_lock)
        {
            IdentityStore.DeleteTempIfPresent(_path);
            if (!File.Exists(_path)) return null;

            try
            {
                string raw = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<T>(raw, Json);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "State file unparseable — discarding");
                return null;
            }
        }
    }

    /// <summary>Persist atomically (temp + rename). Pretty-printed for readability when admins inspect by hand.</summary>
    public void Save<T>(T state) where T : class
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? IdentityStore.DefaultDirectory);

            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}

/// <summary>
/// Top-level service state shape. Add new fields here — JSON will
/// silently round-trip unknown fields through (default-ignore-when-null
/// keeps optional bits compact).
/// </summary>
public sealed record ServiceState
{
    /// <summary>Last broker we successfully connected to — used as Phase 5 sticky reconnect hint across service restarts.</summary>
    public string? LastGoodBroker { get; init; }

    /// <summary>Last exit we successfully connected to.</summary>
    public string? LastGoodExit { get; init; }

    /// <summary>User-selected exit override (<c>"auto"</c> = let DiscoveryTracker pick).</summary>
    public string SelectedExit { get; init; } = "auto";

    /// <summary>User-selected broker override (<c>"auto"</c> = RTT-probe driven).</summary>
    public string SelectedBroker { get; init; } = "auto";

    /// <summary>RU-bypass split tunnel toggle (Phase 3).</summary>
    public bool SplitTunnelEnabled { get; init; } = false;

    /// <summary>SRV discovery domain — defaults to the production zone; UI override lands in Phase 2.8.</summary>
    public string DiscoveryDomain { get; init; } = "vertices.ru";

    /// <summary>
    /// Device-local client name (== Mosquitto user suffix). Defaults to
    /// "windows" so first-run installs map to the per-platform identity
    /// already provisioned via vtx-admin (paritет с iOS "iphone" /
    /// macOS "mac" / Android "android"). UI lets users with multiple
    /// Windows boxes override this — e.g. "windows-laptop", "windows-desktop".
    /// </summary>
    public string ClientName { get; init; } = "windows";

    /// <summary>Last time we successfully refreshed SRV-based discovery (UTC ticks).</summary>
    public long? SrvCacheRefreshedTicks { get; init; }

    /// <summary>
    /// Last good SRV resolution. Used as the backup-domain source and
    /// stale-cache fallback by <see cref="SrvResolver"/> when DoH is
    /// unreachable. Wire-compat with Swift / Kotlin so a copied-over
    /// state.json from another platform still bootstraps Windows.
    /// </summary>
    public SrvDiscoveryResult? LastSrv { get; init; }
}
