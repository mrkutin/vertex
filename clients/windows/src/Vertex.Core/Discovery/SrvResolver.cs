using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Core.Util;

namespace Vertex.Core.Discovery;

/// <summary>
/// Resolves <c>_mqtt._tcp.{domain}</c> + <c>_vtx-exit._tcp.{domain}</c>
/// (with <c>_vtx-backup._tcp.{domain}</c> fallback) via DNS-over-HTTPS —
/// Cloudflare first, Google second. Mirror of Swift
/// <c>SRVDiscovery</c> and Kotlin <c>SrvDiscovery</c>; semantics MUST stay
/// observationally equivalent because all three platforms read the same
/// SRV+TXT zone and feed the result into the same multi-broker failover
/// logic.
///
/// <para><b>Caching strategy</b> (matches Swift+Kotlin):
/// <list type="number">
///   <item>On success → persist last good answer (incl. backup domain).</item>
///   <item>Primary failure → retry against the cached backup domain.</item>
///   <item>Both fail → return cached answer regardless of age.</item>
///   <item>Everything missing → return null.</item>
/// </list></para>
/// </summary>
public sealed class SrvResolver
{
    public static readonly IReadOnlyList<string> DefaultProviders = new[]
    {
        "https://cloudflare-dns.com/dns-query",
        "https://dns.google/resolve",
    };

    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly ISrvCache _cache;
    private readonly IReadOnlyList<string> _providers;
    private readonly TimeSpan _requestTimeout;
    private readonly ILogger<SrvResolver> _log;

    public SrvResolver(
        HttpClient http,
        ISrvCache cache,
        IReadOnlyList<string>? providers = null,
        TimeSpan? requestTimeout = null,
        ILogger<SrvResolver>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _providers = providers ?? DefaultProviders;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _log = log ?? NullLogger<SrvResolver>.Instance;
    }

    /// <summary>
    /// Try primary domain → cached backup domain → cached results.
    /// Returns null only when every option produces an empty broker list.
    /// </summary>
    public async Task<SrvDiscoveryResult?> ResolveWithFallbackAsync(string domain, CancellationToken ct = default)
    {
        try
        {
            var primary = await ResolveAsync(domain, ct).ConfigureAwait(false);
            await _cache.SaveAsync(primary, ct).ConfigureAwait(false);
            return primary;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Primary SRV domain {Domain} failed", domain);
        }

        var cached = await _cache.LoadAsync(ct).ConfigureAwait(false);
        var backup = cached?.BackupDomain;
        if (!string.IsNullOrWhiteSpace(backup))
        {
            try
            {
                var viaBackup = await ResolveAsync(backup, ct).ConfigureAwait(false);
                await _cache.SaveAsync(viaBackup, ct).ConfigureAwait(false);
                _log.LogInformation("Resolved via backup {Backup}", backup);
                return viaBackup;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Backup SRV domain {Backup} also failed", backup);
            }
        }

        if (cached is { Brokers.Count: > 0 })
        {
            _log.LogInformation("Using cached SRV results for {Domain}", domain);
            return cached;
        }

        _log.LogError("All DNS discovery failed for {Domain}", domain);
        return null;
    }

    /// <summary>
    /// Resolve <paramref name="domain"/> via DoH. Throws when the broker
    /// list comes back empty — caller falls through to backup/cache.
    /// </summary>
    public async Task<SrvDiscoveryResult> ResolveAsync(string domain, CancellationToken ct = default)
    {
        var brokerTask = LookupSrvAsync($"_mqtt._tcp.{domain}", ct);
        var exitTask   = LookupSrvAsync($"_vtx-exit._tcp.{domain}", ct);
        var backupTask = LookupSrvAsync($"_vtx-backup._tcp.{domain}", ct);

        // Await all three together so a primary failure does NOT orphan
        // the secondary tasks. Today none of them throw (provider failover
        // is internal), but joining them defensively guards against
        // future TaskUnobservedException if any sub-lookup grows a throw.
        await Task.WhenAll(brokerTask, exitTask, backupTask).ConfigureAwait(false);

        var brokers = brokerTask.Result;
        var exits   = exitTask.Result;
        var backups = backupTask.Result;

        if (brokers.Count == 0)
        {
            throw new SrvResolveException($"No SRV records for {domain}");
        }

        var sortedBackups = backups.OrderBy(r => r).ToList();
        var backupDomain = sortedBackups.Count > 0
            ? sortedBackups[0].Target.TrimEnd('.', ' ')
            : null;
        if (string.IsNullOrEmpty(backupDomain)) backupDomain = null;

        var sortedBrokers = brokers.OrderBy(r => r).ToList();
        var sortedExits   = exits.OrderBy(r => r).ToList();

        var displayNames = await FetchExitDisplayNamesAsync(sortedExits, ct).ConfigureAwait(false);

        _log.LogInformation(
            "Resolved {Domain}: {Brokers} brokers, {Exits} exits, backup={Backup}",
            domain, sortedBrokers.Count, sortedExits.Count, backupDomain ?? "none");

        return new SrvDiscoveryResult(
            Domain: domain,
            BackupDomain: backupDomain,
            Brokers: sortedBrokers,
            Exits: sortedExits,
            ExitDisplayNames: displayNames,
            UpdatedAtEpochMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private async Task<IReadOnlyDictionary<string, string>> FetchExitDisplayNamesAsync(
        IReadOnlyList<SrvRecord> exits,
        CancellationToken ct)
    {
        if (exits.Count == 0) return new Dictionary<string, string>();

        // Each TXT lookup runs in parallel; failures fall through to no
        // entry, which makes NodeLabels.EdgeLabel render the uppercased
        // ID instead. Keying by exit ID (first label of the SRV target)
        // matches the Swift/Kotlin convention.
        var pairs = await Task.WhenAll(exits.Select(async record =>
        {
            var target = record.Target.TrimEnd('.', ' ');
            var dot = target.IndexOf('.');
            var id = dot > 0 ? target[..dot] : target;
            if (string.IsNullOrEmpty(id)) return (Id: string.Empty, Txt: (string?)null);

            var txt = await LookupTxtSafeAsync(target, ct).ConfigureAwait(false);
            return (Id: id, Txt: txt);
        })).ConfigureAwait(false);

        var result = new Dictionary<string, string>(pairs.Length);
        foreach (var (id, txt) in pairs)
        {
            if (string.IsNullOrEmpty(id)) continue;
            if (string.IsNullOrWhiteSpace(txt)) continue;
            result[id] = txt!;
        }
        return result;
    }

    // -------- DoH SRV --------

    /// <summary>
    /// Run the SRV query against each DoH provider in turn until one
    /// returns a non-empty answer. Provider-level exceptions are logged
    /// and swallowed; the caller — <see cref="ResolveAsync"/> — surfaces
    /// the empty broker list as a single <see cref="SrvResolveException"/>
    /// so failure semantics for required vs optional records collapse to
    /// one place.
    /// </summary>
    private async Task<IReadOnlyList<SrvRecord>> LookupSrvAsync(string name, CancellationToken ct)
    {
        foreach (var provider in _providers)
        {
            try
            {
                var records = await QuerySrvAsync(provider, name, ct).ConfigureAwait(false);
                if (records.Count > 0) return records;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "DoH {Provider} for {Name} failed", provider, name);
            }
        }
        return Array.Empty<SrvRecord>();
    }

    private async Task<IReadOnlyList<SrvRecord>> QuerySrvAsync(string provider, string name, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_requestTimeout);

        var url = $"{provider}?name={Uri.EscapeDataString(name)}&type=SRV";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/dns-json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var doh = await response.Content.ReadFromJsonAsync<DohResponse>(cts.Token).ConfigureAwait(false);
        if (doh is null || doh.Status != 0 || doh.Answer is null) return Array.Empty<SrvRecord>();

        var records = new List<SrvRecord>(doh.Answer.Count);
        foreach (var answer in doh.Answer)
        {
            if (answer.Type != 33) continue; // RFC 2782
            var parts = answer.Data.Split(' ');
            if (parts.Length != 4) continue;
            if (!int.TryParse(parts[0], out var priority)) continue;
            if (!int.TryParse(parts[1], out var weight))   continue;
            if (!int.TryParse(parts[2], out var port))     continue;
            var target = parts[3].TrimEnd('.', ' ');
            if (string.IsNullOrEmpty(target)) continue;
            records.Add(new SrvRecord(priority, weight, port, target));
        }
        return records;
    }

    // -------- DoH TXT --------

    /// <summary>
    /// TXT record lookup with provider failover, identical strategy to
    /// SRV but for type 16. Returns the first non-empty answer or null
    /// when every provider fails / record absent — TXT metadata is
    /// always optional, callers must tolerate missing values.
    /// </summary>
    private async Task<string?> LookupTxtSafeAsync(string name, CancellationToken ct)
    {
        foreach (var provider in _providers)
        {
            try
            {
                var txt = await QueryTxtAsync(provider, name, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(txt)) return txt;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "DoH TXT {Provider} for {Name} failed", provider, name);
            }
        }
        return null;
    }

    private async Task<string?> QueryTxtAsync(string provider, string name, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_requestTimeout);

        var url = $"{provider}?name={Uri.EscapeDataString(name)}&type=TXT";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/dns-json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var doh = await response.Content.ReadFromJsonAsync<DohResponse>(cts.Token).ConfigureAwait(false);
        if (doh is null || doh.Status != 0 || doh.Answer is null) return null;

        var answer = doh.Answer.FirstOrDefault(a => a.Type == 16);
        if (answer is null) return null;

        var parsed = TxtParser.Parse(answer.Data).Trim();
        return parsed.Length == 0 ? null : parsed;
    }

    // -------- DoH wire types --------

    private sealed record DohResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] IReadOnlyList<DohAnswer>? Answer);

    private sealed record DohAnswer(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] int    Type,
        [property: JsonPropertyName("data")] string Data);
}

/// <summary>Surface error type so callers can distinguish DNS failure from other exceptions.</summary>
public sealed class SrvResolveException : Exception
{
    public SrvResolveException(string message) : base(message) { }
    public SrvResolveException(string message, Exception inner) : base(message, inner) { }
}
