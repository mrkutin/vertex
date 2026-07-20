using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Service.Storage;
using Vertex.Shared;

namespace Vertex.Service.Diagnostics;

/// <summary>
/// Bundles state.json (sanitized — no secrets), recent EventLog lines
/// (best-effort), and the last <see cref="ConnectionStatus"/> snapshot
/// into a ZIP at the App-supplied path. Mirror of macOS / Android
/// diagnostics export — admins ship this to support when
/// triaging a connect failure.
/// </summary>
public sealed class DiagnosticsExporter
{
    private const string EventLogSource = "Vertex VPN";
    private const int MaxLogLines = 500;

    private readonly StateStore _store;
    private readonly ILogger _log;

    public DiagnosticsExporter(StateStore store, ILogger<DiagnosticsExporter>? log = null)
    {
        _store = store;
        _log = log ?? NullLogger<DiagnosticsExporter>.Instance;
    }

    /// <summary>
    /// Write a ZIP at <paramref name="targetPath"/>. Returns true on
    /// success; false if the path is unwritable. Best-effort within
    /// the ZIP — a missing log source or unparseable state.json is
    /// noted in the bundle as a placeholder rather than aborting.
    /// </summary>
    public bool Export(string targetPath, ConnectionStatus currentStatus)
    {
        try
        {
            // Atomic write: build at .tmp, swap into place, so a
            // half-written archive never lands at targetPath.
            var tmp = targetPath + ".tmp";
            try { File.Delete(tmp); } catch { /* swallow */ }

            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                AddJsonEntry(zip, "status.json", currentStatus);
                AddStateEntry(zip);
                AddEventLogEntry(zip);
                AddManifestEntry(zip);
            }

            File.Move(tmp, targetPath, overwrite: true);
            _log.LogInformation("Diagnostics exported to {Path}", targetPath);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Diagnostics export failed for {Path}", targetPath);
            return false;
        }
    }

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
    };

    private static void AddJsonEntry(ZipArchive zip, string name, object payload)
    {
        var entry = zip.CreateEntry(name);
        using var w = new StreamWriter(entry.Open());
        w.Write(JsonSerializer.Serialize(payload, PrettyJson));
    }

    private void AddStateEntry(ZipArchive zip)
    {
        // Pull fields directly so secrets stay out: identity/password
        // are in DPAPI-protected blobs and never in state.json, but the
        // export still drops them through a sanitized record so a
        // future leak (e.g., an admin-only debug field) doesn't
        // silently flow out.
        var state = _store.Load<ServiceState>() ?? new ServiceState();
        var sanitized = new
        {
            state.LastGoodBroker,
            state.LastGoodExit,
            state.SelectedBroker,
            state.SelectedExit,
            state.SplitTunnelEnabled,
            state.DiscoveryDomain,
            state.SrvCacheRefreshedTicks,
            // LastSrv is a ~150 KB blob of CIDR strings — drop a
            // summary instead so the bundle stays small.
            LastSrvSummary = state.LastSrv is null
                ? null
                : new
                {
                    state.LastSrv.Domain,
                    state.LastSrv.BackupDomain,
                    state.LastSrv.UpdatedAtEpochMs,
                    BrokerCount = state.LastSrv.Brokers.Count,
                    ExitCount   = state.LastSrv.Exits.Count,
                },
        };
        AddJsonEntry(zip, "state.json", sanitized);
    }

    private void AddEventLogEntry(ZipArchive zip)
    {
        try
        {
            // Event Log API is Windows-only; reading from a 3rd-party
            // source on a different machine would throw — wrap the
            // whole walk in a try and fall back to a placeholder note.
            using var log = new System.Diagnostics.EventLog("Application", ".", EventLogSource);
            var entries = new List<object>(MaxLogLines);
            int count = log.Entries.Count;
            for (int i = Math.Max(0, count - MaxLogLines); i < count; i++)
            {
                var e = log.Entries[i];
                entries.Add(new
                {
                    Time = e.TimeWritten.ToUniversalTime().ToString("o"),
                    Type = e.EntryType.ToString(),
                    e.InstanceId,
                    Message = e.Message,
                });
            }
            AddJsonEntry(zip, "events.json", entries);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Event log read failed; skipping");
            var note = new { error = "event log unavailable", reason = ex.Message };
            AddJsonEntry(zip, "events.json", note);
        }
    }

    private void AddManifestEntry(ZipArchive zip)
    {
        var manifest = new
        {
            ExportedAt = DateTime.UtcNow.ToString("o"),
            ExportedBy = "Vertex.Service",
            Version = typeof(DiagnosticsExporter).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            Os = Environment.OSVersion.ToString(),
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        };
        AddJsonEntry(zip, "manifest.json", manifest);
    }
}
