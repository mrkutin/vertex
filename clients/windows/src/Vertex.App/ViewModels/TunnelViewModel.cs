using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Vertex.App.Services;
using Vertex.Shared;
using Vertex.Shared.Ipc;

namespace Vertex.App.ViewModels;

/// <summary>
/// View-model bound to <c>ConnectScreen</c>. Subscribes to
/// <see cref="IpcClient.MessagesAsync"/> and projects every inbound
/// status / error / discovery / broker / stats event into observable
/// properties the XAML data-binds to. Mirror of Swift
/// <c>TunnelViewModel</c> + Kotlin <c>TunnelViewModel</c>.
/// </summary>
public sealed partial class TunnelViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IpcClient _ipc;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Captured at construction (the App boots VM on the UI thread). Every
    /// inbound IPC message is marshalled to this dispatcher before
    /// touching observable state — WinUI 3 throws <c>RPC_E_WRONG_THREAD</c>
    /// if PropertyChanged fires from a worker thread (Phase 1.10 review
    /// CRITICAL-1).
    /// </summary>
    private readonly DispatcherQueue _dispatcher;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private ConnectionState _state = ConnectionState.Disconnected;

    [ObservableProperty] private string? _assignedIp;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolvedBrokerDisplay))]
    private string? _currentBroker;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolvedExitDisplay))]
    private string? _currentExit;
    [ObservableProperty] private string? _lastErrorMessage;
    [ObservableProperty] private int? _pingMs;
    [ObservableProperty] private long? _connectedSinceEpochMs;

    /// <summary>
    /// Device's client name (== Mosquitto user suffix). Sourced from the
    /// Service's <c>IdentityInfo</c> envelope on attach / after a
    /// <c>SetClientName</c>. The Settings TextBox binds two-way: editing
    /// the field calls <see cref="SetClientNameAsync"/>, which round-trips
    /// through the Service so the displayed value always reflects what's
    /// persisted, not unsaved local edits.
    /// </summary>
    [ObservableProperty] private string _clientName = "windows";

    /// <summary>SRV discovery domain (default "vertices.ru").</summary>
    [ObservableProperty] private string _discoveryDomain = "vertices.ru";

    /// <summary>
    /// Lowercase 64-char hex of the device's X25519 identity public key.
    /// Empty until the Service's first <see cref="ExtensionResponse.IdentityInfo"/>
    /// arrives. Settings derives the displayed fingerprint by formatting the
    /// first 16 chars as 4 groups of 4 (paritет macOS).
    /// </summary>
    [ObservableProperty] private string _identityPubkeyHex = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UploadBytesPerSec))]
    private long _bytesUp;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadBytesPerSec))]
    private long _bytesDown;

    /// <summary>
    /// Rolling stats samples for rate computation. Kept for
    /// <see cref="HistoryHorizonSeconds"/> seconds; rate is computed
    /// over <see cref="RateWindowSeconds"/> by diffing newest vs oldest
    /// sample within the window. Mirror of macOS
    /// <c>TunnelViewModel.statsHistory</c> + <c>instantRate</c>.
    /// </summary>
    private readonly List<StatsSample> _statsHistory = new();
    private const double RateWindowSeconds     = 3.0;
    private const double HistoryHorizonSeconds = 5.0;

    /// <summary>Bytes-per-second uploaded over the rolling rate window. Returns 0 when not enough samples.</summary>
    public double UploadBytesPerSec   => InstantRate(s => s.BytesUp);

    /// <summary>Bytes-per-second downloaded over the rolling rate window.</summary>
    public double DownloadBytesPerSec => InstantRate(s => s.BytesDown);

    private readonly record struct StatsSample(DateTime Time, long BytesUp, long BytesDown);

    private double InstantRate(Func<StatsSample, long> selector)
    {
        if (_statsHistory.Count == 0) return 0;
        var newest = _statsHistory[^1];
        var cutoff = newest.Time.AddSeconds(-RateWindowSeconds);

        StatsSample? oldest = null;
        foreach (var s in _statsHistory)
        {
            if (s.Time >= cutoff) { oldest = s; break; }
        }
        if (oldest is null || oldest.Value.Time >= newest.Time) return 0;

        var elapsed = (newest.Time - oldest.Value.Time).TotalSeconds;
        if (elapsed <= 0) return 0;
        var delta = selector(newest) - selector(oldest.Value);
        return delta > 0 ? delta / elapsed : 0;
    }

    /// <summary>User's broker pick — <c>"auto"</c> or a literal broker URL. Send via <see cref="SetSelectedBrokerAsync"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedBrokerAuto))]
    [NotifyPropertyChangedFor(nameof(ResolvedBrokerDisplay))]
    private string _selectedBroker = "auto";

    /// <summary>User's exit pick — <c>"auto"</c> or a literal exit ID.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedExitAuto))]
    [NotifyPropertyChangedFor(nameof(ResolvedExitDisplay))]
    private string _selectedExit = "auto";

    /// <summary>SRV-resolved broker URL list (ordered by priority). Empty until first BrokerUpdate from Service.</summary>
    public ObservableCollection<string> AvailableBrokers { get; } = new();

    /// <summary>Heartbeat-known exit IDs (ordered by score). Empty until first DiscoveryUpdate.</summary>
    public ObservableCollection<string> AvailableExits { get; } = new();

    /// <summary>Per-exit display label from SRV TXT records (e.g. "aws" → "Toronto, Canada").</summary>
    public Dictionary<string, string> ExitDisplayNames { get; } = new();

    public bool IsSelectedBrokerAuto => SelectedBroker == "auto";
    public bool IsSelectedExitAuto   => SelectedExit   == "auto";

    /// <summary>
    /// What the ServerCard renders next to "Vertex". When the user
    /// picked a literal broker → that URL's host. When auto + connected
    /// → "Auto · {short name from CurrentBroker}". Otherwise just "Auto".
    /// Mirror of Swift <c>BrokerListView.autoRow</c> "Now: {short}" line.
    /// </summary>
    public string ResolvedBrokerDisplay
    {
        get
        {
            if (!IsSelectedBrokerAuto)
            {
                // SelectedBroker is a URL; surface the host.
                return Vertex.Core.Config.BrokerUrl.TryParse(SelectedBroker, out var url)
                    ? url.Host
                    : SelectedBroker;
            }
            if (IsConnected && !string.IsNullOrEmpty(CurrentBroker))
            {
                return $"Auto · {ShortBrokerName(CurrentBroker!)}";
            }
            return "Auto";
        }
    }

    /// <summary>
    /// What the ServerCard renders next to "Edge". On explicit pick →
    /// the city from TXT, or the uppercased ID. On auto + connected →
    /// "Auto · {city/ID}". Otherwise "Auto".
    /// </summary>
    public string ResolvedExitDisplay
    {
        get
        {
            if (!IsSelectedExitAuto)
            {
                return ExitDisplayNames.TryGetValue(SelectedExit, out var n) ? n
                    : SelectedExit.ToUpperInvariant();
            }
            if (IsConnected && !string.IsNullOrEmpty(CurrentExit))
            {
                // Always render the short uppercased ID after "Auto ·" — matches
                // iOS / macOS where the auto-resolved label is "Auto · STO",
                // not "Auto · Stockholm, Sweden". The full city/country form
                // is reserved for explicit (non-auto) exit picks.
                return $"Auto · {CurrentExit!.ToUpperInvariant()}";
            }
            return "Auto";
        }
    }

    /// <summary>
    /// Short name extracted from a broker host. Convention: first
    /// dot-label after the leading <c>mqtt-</c> prefix, uppercased.
    /// "mqtt-yc.vertices.ru" → "YC". Mirror of <c>NodeLabels.vertexLabel</c>.
    /// </summary>
    private static string ShortBrokerName(string host)
    {
        var trimmed = host.StartsWith("mqtt-", StringComparison.Ordinal) ? host[5..] : host;
        var dot = trimmed.IndexOf('.');
        var first = dot > 0 ? trimmed[..dot] : trimmed;
        return first.ToUpperInvariant();
    }

    public bool IsConnected => State == ConnectionState.Connected;
    public bool IsBusy      => State is ConnectionState.Connecting
                                   or ConnectionState.Handshaking
                                   or ConnectionState.Reconnecting;

    public string StatusLabel => State switch
    {
        ConnectionState.Disconnected  => "Disconnected",
        ConnectionState.Connecting    => "Connecting…",
        ConnectionState.Handshaking   => "Handshaking…",
        ConnectionState.Connected     => "Connected",
        ConnectionState.Reconnecting  => "Reconnecting…",
        _ => "—",
    };

    public TunnelViewModel(IpcClient ipc, ILogger? log = null)
    {
        _ipc = ipc;
        _log = log ?? NullLogger.Instance;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                "TunnelViewModel must be constructed on a UI thread (DispatcherQueue.GetForCurrentThread() returned null).");
        _ = Task.Run(() => RunMessagePumpAsync(_cts.Token));
    }

    [RelayCommand]
    private async Task ConnectAsync() => await _ipc.SendAsync(new AppMessage.Connect()).ConfigureAwait(false);

    [RelayCommand]
    private async Task DisconnectAsync() => await _ipc.SendAsync(new AppMessage.Disconnect()).ConfigureAwait(false);

    [RelayCommand]
    private async Task RefreshStatusAsync() => await _ipc.SendAsync(new AppMessage.RequestStatus()).ConfigureAwait(false);

    /// <summary>Push a new broker password into Service-owned DPAPI storage.</summary>
    public Task SetPasswordAsync(string password) =>
        _ipc.SendAsync(new AppMessage.SetPassword(password));

    /// <summary>Pick an exit (or pass <c>"auto"</c>). Updates <see cref="SelectedExit"/> immediately for snappy UI feedback.</summary>
    public Task SetSelectedExitAsync(string exitId)
    {
        SelectedExit = exitId;
        return _ipc.SendAsync(new AppMessage.SetSelectedExit(exitId));
    }

    /// <summary>Pick a broker URL (or pass <c>"auto"</c>). Updates <see cref="SelectedBroker"/> immediately.</summary>
    public Task SetSelectedBrokerAsync(string brokerUrl)
    {
        SelectedBroker = brokerUrl;
        return _ipc.SendAsync(new AppMessage.SetSelectedBroker(brokerUrl));
    }

    /// <summary>Force a fresh SRV resolve + broker probe sweep on the Service side.</summary>
    public Task RefreshDiscoveryAsync() =>
        _ipc.SendAsync(new AppMessage.RefreshDiscovery());

    /// <summary>Toggle the split-tunnel (RU bypass). Applies on next Connect.</summary>
    public Task SetSplitTunnelAsync(bool enabled) =>
        _ipc.SendAsync(new AppMessage.SetSplitTunnel(enabled));

    /// <summary>
    /// Persist a new client name. Service sanitises and re-pushes
    /// <c>IdentityInfo</c>, so <see cref="ClientName"/> updates from the
    /// Service-canonical value rather than the raw input — invalid edits
    /// surface as an ErrorEnvelope without mutating local state.
    /// </summary>
    public Task SetClientNameAsync(string name) =>
        _ipc.SendAsync(new AppMessage.SetClientName(name));

    /// <summary>Persist a new SRV discovery domain.</summary>
    public Task SetDiscoveryDomainAsync(string domain) =>
        _ipc.SendAsync(new AppMessage.SetDiscoveryDomain(domain));

    /// <summary>Ask the Service to re-push the IdentityInfo snapshot. Settings calls this on open.</summary>
    public Task RequestIdentityInfoAsync() =>
        _ipc.SendAsync(new AppMessage.RequestIdentityInfo());

    /// <summary>
    /// Ask the Service to bundle state.json + recent log lines + the
    /// current ConnectionStatus into a ZIP at <paramref name="targetPath"/>.
    /// Fire-and-forget — the Service writes the file directly.
    /// </summary>
    public Task ExportDiagnosticsAsync(string targetPath) =>
        _ipc.SendAsync(new AppMessage.ExportDiagnostics(targetPath));

    private async Task RunMessagePumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _ipc.MessagesAsync.WithCancellation(ct).ConfigureAwait(false))
            {
                // Marshal to UI thread BEFORE touching observable state.
                // PropertyChanged listeners (x:Bind generated code) run on
                // the dispatcher and would crash with RPC_E_WRONG_THREAD
                // if we mutated from this background pump. (Phase 1.10
                // review CRITICAL-1.)
                _dispatcher.TryEnqueue(() => Apply(msg));
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex) { _log.LogWarning(ex, "Message pump crashed"); }
    }

    private void Apply(ExtensionResponse msg)
    {
        switch (msg)
        {
            case ExtensionResponse.StatusEnvelope s:
                State = s.Status.State;
                if (s.Status.State == ConnectionState.Disconnected)
                {
                    // Clear stale fields so the UI doesn't show "Connected
                    // to aws via mqtt-yc" while State == Disconnected
                    // (Phase 1.10 review MAJOR-5).
                    AssignedIp           = null;
                    CurrentBroker        = null;
                    CurrentExit          = null;
                    ConnectedSinceEpochMs = null;
                    BytesUp              = 0;
                    BytesDown            = 0;
                    _statsHistory.Clear();
                    OnPropertyChanged(nameof(UploadBytesPerSec));
                    OnPropertyChanged(nameof(DownloadBytesPerSec));
                }
                else
                {
                    AssignedIp    = s.Status.AssignedIp;
                    CurrentBroker = s.Status.CurrentBroker;
                    CurrentExit   = s.Status.CurrentExit;
                }
                ConnectedSinceEpochMs = s.Status.ConnectedSinceEpochMs;
                PingMs           = s.Status.PingMs;
                // Auto-clear the error banner once we reach a healthy
                // Connected state — the user already saw the previous
                // error; stale text under a working tunnel is confusing.
                // Status.LastError still wins on Disconnected so the
                // last failure stays visible until the next attempt.
                if (s.Status.State == ConnectionState.Connected)
                    LastErrorMessage = null;
                else
                    LastErrorMessage = s.Status.LastError;
                break;

            case ExtensionResponse.StatsEnvelope st:
                BytesUp   = st.Stats.BytesUp;
                BytesDown = st.Stats.BytesDown;
                // Append + prune to the rolling window.
                var now = DateTime.UtcNow;
                _statsHistory.Add(new StatsSample(now, st.Stats.BytesUp, st.Stats.BytesDown));
                var horizon = now.AddSeconds(-HistoryHorizonSeconds);
                while (_statsHistory.Count > 0 && _statsHistory[0].Time < horizon)
                    _statsHistory.RemoveAt(0);
                OnPropertyChanged(nameof(UploadBytesPerSec));
                OnPropertyChanged(nameof(DownloadBytesPerSec));
                break;

            case ExtensionResponse.ErrorEnvelope e:
                // Age filter — paritет с macOS. A stale error that
                // arrives during pipe replay (Service held a TunnelError
                // from minutes ago when the App reconnects) shouldn't
                // pop the banner. 60s window matches Swift.
                var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - e.Error.TimestampEpochMs;
                if (ageMs > 60_000)
                {
                    _log.LogDebug("Stale error ignored: {Kind} age={AgeMs}ms", e.Error.Kind, ageMs);
                    break;
                }
                LastErrorMessage = e.Error.UserMessage;
                _log.LogWarning("Service reported error: {Kind}: {Detail}", e.Error.Kind, e.Error.Detail);
                break;

            case ExtensionResponse.BrokerUpdate bu:
                ReplaceCollection(AvailableBrokers, bu.Brokers.Select(b => b.Url));
                OnPropertyChanged(nameof(ResolvedBrokerDisplay));
                break;

            case ExtensionResponse.IdentityInfo ii:
                ClientName        = ii.ClientName;
                DiscoveryDomain   = ii.Domain;
                IdentityPubkeyHex = ii.PubkeyHex;
                break;

            case ExtensionResponse.DiscoveryUpdate du:
                ReplaceCollection(AvailableExits, du.Exits.Select(e => e.Id));
                // Rebuild the display-names dictionary from the IPC bundle
                // (Service reads from StateStore.LastSrv.ExitDisplayNames
                // — the SRV TXT records). Missing entry → row falls back
                // to uppercased ID.
                ExitDisplayNames.Clear();
                foreach (var e in du.Exits)
                {
                    if (!string.IsNullOrEmpty(e.DisplayName))
                        ExitDisplayNames[e.Id] = e.DisplayName;
                }
                OnPropertyChanged(nameof(ResolvedExitDisplay));
                break;

            // ack — surfaced as log only (no UI feedback wired yet).
        }
    }

    private static void ReplaceCollection(ObservableCollection<string> target, IEnumerable<string> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        await _ipc.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
