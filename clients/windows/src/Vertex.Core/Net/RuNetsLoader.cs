using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vertex.Core.Net;

/// <summary>
/// Loads the RU CIDR aggregated zone used by the split-tunnel feature.
/// Two sources, fall-through priority:
/// <list type="number">
///   <item>%ProgramData%\Vertex\ru-aggregated.zone — refreshed copy from
///   ipdeny.com, atomically swapped via File.Move with overwrite.</item>
///   <item>Resources\ru-aggregated.zone shipped next to Vertex.Service.exe
///   — bundled snapshot, used for cold-start before the first refresh
///   succeeds.</item>
/// </list>
///
/// Mirror of macOS / Android RuNetsRepository — the file format and
/// thresholds match (line-per-CIDR, ~8585 lines, ~135 KB; min 50 KB +
/// 1000 valid CIDRs to accept a refresh).
/// </summary>
public sealed class RuNetsLoader
{
    public const string FileName = "ru-aggregated.zone";
    public const string RefreshUrl = "https://www.ipdeny.com/ipblocks/data/aggregated/ru-aggregated.zone";

    /// <summary>Floor on a successful refresh body. ipdeny served ~135 KB at last benchmark; 50 KB catches truncation / captive portals.</summary>
    public const long MinBodyBytes = 50_000;

    /// <summary>Floor on the count of validly-parsed CIDRs. Bundled zone has ~8585; 1000 is the safety net.</summary>
    public const int MinCidrLines = 1_000;

    public enum Source { Bundled, Updated }

    public sealed record Info(int LineCount, long SizeBytes, DateTime? UpdatedAtUtc, Source Source);

    private readonly string _persistentPath;
    private readonly string _bundledPath;
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly object _refreshLock = new();
    private bool _refreshing;

    /// <summary>
    /// Default %ProgramData%\Vertex directory for the refreshed copy.
    /// Caller may override by passing <c>persistentPath</c>; the App
    /// uses the same default so the Routing settings UI can show
    /// counts without an IPC round-trip.
    /// </summary>
    public static readonly string DefaultPersistentDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Vertex");

    public RuNetsLoader(
        string? persistentPath = null,
        string? bundledPath = null,
        HttpClient? http = null,
        ILogger<RuNetsLoader>? log = null)
    {
        _persistentPath = persistentPath
            ?? Path.Combine(DefaultPersistentDirectory, FileName);
        _bundledPath = bundledPath
            ?? Path.Combine(AppContext.BaseDirectory, "Resources", FileName);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _log = log ?? NullLogger<RuNetsLoader>.Instance;
    }

    /// <summary>Resolved active path — updated copy if present, otherwise bundled.</summary>
    public string ActivePath => File.Exists(_persistentPath) ? _persistentPath : _bundledPath;

    /// <summary>Active source flag — for the Routing settings UI.</summary>
    public Source ActiveSource => File.Exists(_persistentPath) ? Source.Updated : Source.Bundled;

    /// <summary>
    /// Load CIDR strings from the active zone file. Each line is one
    /// CIDR ("dotted-quad/prefix"); blank lines and comment lines (starting
    /// with #) are skipped. Caller may want to take only the top-N most
    /// specific entries for the route table — see SplitRouter.
    /// </summary>
    public IReadOnlyList<string> Load()
    {
        var path = ActivePath;
        if (!File.Exists(path))
        {
            _log.LogWarning("RuNets zone file missing: {Path}", path);
            return Array.Empty<string>();
        }
        var result = new List<string>(8600);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// Snapshot of the active file's metadata for the Routing UI. Reads
    /// the line count via streaming so a 135 KB file doesn't load fully
    /// into memory.
    /// </summary>
    public Info LoadInfo()
    {
        var path = ActivePath;
        if (!File.Exists(path)) return new Info(0, 0, null, Source.Bundled);
        var size = new FileInfo(path).Length;
        int count = 0;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            count++;
        }
        var src = ActiveSource;
        DateTime? mtime = src == Source.Updated ? File.GetLastWriteTimeUtc(path) : null;
        return new Info(count, size, mtime, src);
    }

    /// <summary>
    /// Download the latest zone, validate it, and atomically swap into
    /// place. Returns the new <see cref="Info"/> on success or throws
    /// on validation / network failure. Concurrent calls are coalesced
    /// via <see cref="_refreshing"/>; the second caller waits and
    /// receives the live LoadInfo() snapshot.
    /// </summary>
    public async Task<Info> RefreshAsync(CancellationToken ct = default)
    {
        lock (_refreshLock)
        {
            if (_refreshing) return LoadInfo();
            _refreshing = true;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_persistentPath)!);
            var tmp = _persistentPath + ".tmp";
            try { File.Delete(tmp); } catch { /* swallow */ }

            using (var resp = await _http.GetAsync(RefreshUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            // Validate before promoting — a truncated download or a
            // captive-portal HTML page would otherwise stomp the bundled
            // copy with garbage.
            var size = new FileInfo(tmp).Length;
            if (size < MinBodyBytes)
            {
                File.Delete(tmp);
                throw new InvalidDataException($"Refresh body too small: {size} bytes (min {MinBodyBytes})");
            }
            var validCount = CountValidCidrs(tmp);
            if (validCount < MinCidrLines)
            {
                File.Delete(tmp);
                throw new InvalidDataException($"Refresh has {validCount} valid CIDRs, min {MinCidrLines}");
            }

            // Atomic swap. File.Move(overwrite) on the same volume is
            // implemented via MoveFileEx with MOVEFILE_REPLACE_EXISTING
            // — a renaming-in-place that an in-flight reader keeps
            // observing under its original handle.
            File.Move(tmp, _persistentPath, overwrite: true);

            var info = LoadInfo();
            _log.LogInformation(
                "RuNets refreshed: {Count} CIDRs, {Bytes} bytes, mtime={Mtime}",
                info.LineCount, info.SizeBytes, info.UpdatedAtUtc);
            return info;
        }
        finally
        {
            lock (_refreshLock) _refreshing = false;
        }
    }

    /// <summary>
    /// Validate-and-count: every line that parses as <c>x.x.x.x/N</c>
    /// with octets in 0–255 and prefix in 0–32 counts.
    /// </summary>
    public static int CountValidCidrs(string path)
    {
        int n = 0;
        foreach (var raw in File.ReadLines(path))
        {
            if (TryParseCidr(raw, out _, out _)) n++;
        }
        return n;
    }

    /// <summary>
    /// Parse a single zone line. Returns false on anything that isn't a
    /// valid IPv4 CIDR — comments, blanks, malformed entries.
    /// </summary>
    public static bool TryParseCidr(string raw, out IPAddress network, out byte prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == '#') return false;
        var slash = line.IndexOf('/');
        if (slash <= 0) return false;
        var hostPart = line[..slash];
        var prefixPart = line[(slash + 1)..];
        if (!byte.TryParse(prefixPart, out var p) || p > 32) return false;
        if (!IPAddress.TryParse(hostPart, out var ip)) return false;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = hostPart.Split('.');
        if (bytes.Length != 4) return false;
        foreach (var b in bytes)
        {
            if (!byte.TryParse(b, out _)) return false;
        }
        network = ip;
        prefix = p;
        return true;
    }
}
