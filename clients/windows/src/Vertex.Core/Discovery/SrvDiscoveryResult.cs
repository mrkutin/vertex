using System.Text.Json.Serialization;

namespace Vertex.Core.Discovery;

/// <summary>
/// Cached snapshot of one successful SRV resolution. JSON wire-compatible
/// with Swift <c>SRVDiscoveryResult</c> and Kotlin <c>SrvDiscoveryResult</c>
/// so a state cache copied between platforms is interchangeable.
/// </summary>
public sealed record SrvDiscoveryResult(
    [property: JsonPropertyName("domain")]                                                                                string                       Domain,
    [property: JsonPropertyName("backupDomain"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]             string?                      BackupDomain,
    [property: JsonPropertyName("brokers")]                                                                               IReadOnlyList<SrvRecord>     Brokers,
    [property: JsonPropertyName("exits")]                                                                                 IReadOnlyList<SrvRecord>     Exits,
    /// <summary>
    /// Per-exit display name read from a TXT record on the SRV target host
    /// (e.g. <c>aws.exit.vertices.ru. IN TXT "Toronto, Canada"</c>). Key =
    /// exit ID (first label of the SRV target). Missing entry means no
    /// city/country available; the UI should fall back to the uppercased
    /// ID via <c>NodeLabels.EdgeLabel</c>. Defaulted for backward-compat
    /// with cached results from older builds.
    /// </summary>
    [property: JsonPropertyName("exitDisplayNames")]                                                                      IReadOnlyDictionary<string, string> ExitDisplayNames,
    [property: JsonPropertyName("updatedAtEpochMs"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]       long                         UpdatedAtEpochMs)
{
    /// <summary>
    /// Broker URLs sorted by SRV priority. Port convention:
    /// 8883→mqtts, 443→wss, 1883→mqtt; everything else falls back to mqtt
    /// (lets the user point SRV at a non-standard port without breaking).
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> BrokerUrls => Brokers
        .Select(r => $"{SchemeForPort(r.Port)}://{r.Target}:{r.Port}")
        .ToList();

    /// <summary>
    /// Exit IDs extracted from SRV targets. Convention:
    /// <c>{id}.exit.{domain}</c> — the first dot-label is the ID.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> ExitIds => Exits
        .Select(r => FirstLabelOrTarget(r.Target))
        .ToList();

    /// <summary>
    /// True while cache age is within <see cref="CacheTtlMs"/>. Callers
    /// that want a "skip resolve, use cache" shortcut consult this; the
    /// resolver itself does not honor freshness — fallback always tries
    /// the cache regardless of age.
    /// </summary>
    public bool IsFresh(long? nowEpochMs = null)
    {
        var now = nowEpochMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var age = now - UpdatedAtEpochMs;
        return age >= 0 && age <= CacheTtlMs;
    }

    /// <summary>
    /// In-memory freshness window for SRV / exit lookups. Sized for SRV
    /// TTLs of a few minutes amortized over background sessions: a fresh
    /// app launch within this window skips the DoH round-trip and uses
    /// the cached answer; older than this and we re-resolve. Aligns with
    /// Kotlin's 6h tightening — iOS uses no explicit TTL but is
    /// overwritten on every successful resolve, so the de-facto window
    /// is similar.
    /// </summary>
    public const long CacheTtlMs = 6 * 60 * 60 * 1000L;

    private static string SchemeForPort(int port) => port switch
    {
        8883 => "mqtts",
        443  => "wss",
        1883 => "mqtt",
        _    => "mqtt",
    };

    private static string FirstLabelOrTarget(string target)
    {
        var idx = target.IndexOf('.');
        if (idx <= 0) return target;
        return target[..idx];
    }
}
