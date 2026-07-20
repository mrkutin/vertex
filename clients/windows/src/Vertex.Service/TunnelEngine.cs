using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vertex.Core.Config;
using Vertex.Core.Crypto;
using Vertex.Core.Discovery;
using Vertex.Core.Net;
using Vertex.Core.Protocol;
using Vertex.Core.Transport;
using Vertex.Service.Diagnostics;
using Vertex.Service.Ipc;
using Vertex.Service.Net;
using Vertex.Service.Storage;
using Vertex.Service.Tun;
using Vertex.Shared;
using Vertex.Shared.Ipc;

namespace Vertex.Service;

/// <summary>
/// Orchestrates one VPN session lifecycle on Windows. Equivalent of
/// Swift's <c>NEPacketTunnelProvider</c> + Kotlin's <c>VertexVpnService</c>:
/// load identity → MQTT connect → discovery subscribe → join handshake →
/// session DH derive → WinTUN open → routes / DNS / MTU → packet pipeline.
///
/// Phase 1.9 ships the happy-path lifecycle. Network-change reactive
/// recovery (path monitor → checkLiveness, exit auto-switch on
/// <c>shouldSwitch</c>) is Phase 2.
///
/// Threading: every state mutation runs on the host's <see cref="BackgroundService"/>
/// task. IPC handlers from <see cref="PipeServer"/> dispatch back through
/// the engine's serial queue (<see cref="_serialize"/>).
/// </summary>
public sealed class TunnelEngine : BackgroundService
{
    private static readonly TimeSpan DefaultDiscoveryWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultJoinTimeout   = TimeSpan.FromSeconds(8);

    private readonly ILogger<TunnelEngine> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IdentityStore _identityStore;
    private readonly PasswordStore _passwordStore;
    private readonly StateStore _stateStore;
    private readonly SrvResolver _srvResolver;
    private readonly PipeServer _pipe;
    private readonly SemaphoreSlim _serialize = new(1, 1);

    // Active session state — guarded by _serialize.
    private TunnelConfig? _activeConfig;
    private MqttTransport? _transport;
    private WintunDevice? _wintun;
    private RouteManager? _routes;
    private SplitRouter? _split;
    private DnsLeakGuard? _dns;
    private PacketPipeline? _pipeline;
    private DiscoveryTracker? _discovery;
    private SessionCrypto? _session;
    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    private ConnectionStatus? _lastPushedStatus;
    private long _connectedSinceEpochMs;

    // PingProbe lifecycle state — guarded by _serialize.
    private PingProbe? _pingProbe;
    private CancellationTokenSource? _pingProbeCts;
    private Task? _pingProbeLoop;

    // Path monitor — fires when the routing table changes (Wi-Fi → Ethernet
    // hand-off, USB modem hot-plug). Triggers a debounced re-probe so the
    // engine can pick a faster broker if one became reachable.
    private PathMonitor? _pathMonitor;
    private DateTime _lastPathChange = DateTime.MinValue;
    private static readonly TimeSpan PathDebounce = TimeSpan.FromSeconds(2);

    // Stats pump — pushes a StatsEnvelope every 1s while connected so
    // the App's TunnelViewModel can update its rolling-rate window for
    // SpeedPill. Reset alongside the rest of session state on Disconnect.
    private CancellationTokenSource? _statsPumpCts;
    private Task? _statsPumpLoop;
    private static readonly TimeSpan StatsPumpInterval = TimeSpan.FromSeconds(1);

    // Last DiscoveryUpdate / BrokerUpdate snapshots for App-side replay
    // on pipe reconnect. Both populated by ConnectBodyAsync; cleared on
    // Disconnect. Use the IPC-level ExitInfo / BrokerInfo (Vertex.Shared.Ipc)
    // — the Core-side ExitInfo carries the full RTT map, the IPC one is
    // the App-facing projection.
    private IReadOnlyList<Vertex.Shared.Ipc.BrokerInfo> _lastBrokerInfos = Array.Empty<Vertex.Shared.Ipc.BrokerInfo>();
    private IReadOnlyList<Vertex.Shared.Ipc.ExitInfo>   _lastExitInfos   = Array.Empty<Vertex.Shared.Ipc.ExitInfo>();

    public TunnelEngine(
        ILogger<TunnelEngine> log,
        ILoggerFactory loggerFactory,
        IdentityStore identityStore,
        PasswordStore passwordStore,
        StateStore stateStore,
        SrvResolver srvResolver,
        PipeServer pipe)
    {
        _log = log;
        _loggerFactory = loggerFactory;
        _identityStore = identityStore;
        _passwordStore = passwordStore;
        _stateStore = stateStore;
        _srvResolver = srvResolver;
        _pipe = pipe;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Vertex Service starting");
        _pipe.Start();

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Vertex Service stopping — tearing down active session");
            await SerialAsync(StopTunnelInternalAsync, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Public entry from <see cref="PipeServer"/> — handle one App
    /// command. Marshals onto the serial queue.
    /// </summary>
    public Task HandleAppMessageAsync(AppMessage msg)
    {
        switch (msg)
        {
            case AppMessage.Connect:           return SerialAsync(ConnectInternalAsync,    default);
            case AppMessage.Disconnect:        return SerialAsync(StopTunnelInternalAsync, default);
            // Explicit status request from the App — bypass the
            // change-debounce so a tab refresh / pipe reconnect always
            // gets the current snapshot, not silence (Phase 2.4 review HIGH).
            case AppMessage.RequestStatus:     return SerialAsync(_  => { PushStatusAlways(); return Task.CompletedTask; }, default);
            case AppMessage.RequestStats:      return SerialAsync(_  => { PushStatsSnapshot(); return Task.CompletedTask; }, default);
            case AppMessage.SetPassword sp:    return SerialAsync(_  => { _passwordStore.Set(sp.Password); return Task.CompletedTask; }, default);
            case AppMessage.SetSelectedExit se:   return SerialAsync(_ => { UpdateState(s => s with { SelectedExit   = se.ExitId   }); return Task.CompletedTask; }, default);
            case AppMessage.SetSelectedBroker sb: return SerialAsync(_ =>
                {
                    // Validate so a malformed URL stored in state.json
                    // can't crash the next Connect inside BrokerUrl.Parse.
                    // "auto" is the magic literal; anything else must
                    // round-trip through TryParse.
                    if (sb.BrokerId != "auto" && !BrokerUrl.TryParse(sb.BrokerId, out var _parsed))
                    {
                        EmitError(TunnelErrorKind.Configuration, $"Invalid broker URL: {sb.BrokerId}");
                        return Task.CompletedTask;
                    }
                    UpdateState(s => s with { SelectedBroker = sb.BrokerId });
                    return Task.CompletedTask;
                }, default);
            case AppMessage.ResetIdentity:     return SerialAsync(_  =>
                {
                    _identityStore.Reset();
                    // Re-push so the App's Settings tab shows the freshly
                    // generated pubkey (LoadOrCreate inside PushIdentityInfo
                    // regenerates on the next read).
                    PushIdentityInfo();
                    return Task.CompletedTask;
                }, default);

            case AppMessage.SetClientName scn: return SerialAsync(_ =>
                {
                    var sanitized = SanitizeClientName(scn.ClientName);
                    if (sanitized is null)
                    {
                        EmitError(TunnelErrorKind.Configuration,
                            "Invalid client name (allowed: 1–32 chars of [a-z0-9_-], no leading/trailing dash or underscore).");
                        return Task.CompletedTask;
                    }
                    try { UpdateState(s => s with { ClientName = sanitized }); }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Failed to persist client name");
                        EmitError(TunnelErrorKind.Configuration, $"Failed to save client name: {ex.Message}");
                        return Task.CompletedTask;
                    }
                    _log.LogInformation("Client name set to {Name} (applies on next connect)", sanitized);
                    PushIdentityInfo();
                    return Task.CompletedTask;
                }, default);

            case AppMessage.SetDiscoveryDomain sdd: return SerialAsync(_ =>
                {
                    var sanitized = SanitizeDomain(sdd.Domain);
                    if (sanitized is null)
                    {
                        EmitError(TunnelErrorKind.Configuration,
                            "Invalid discovery domain (ASCII DNS name only — for IDN zones use punycode).");
                        return Task.CompletedTask;
                    }
                    try { UpdateState(s => s with { DiscoveryDomain = sanitized }); }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Failed to persist discovery domain");
                        EmitError(TunnelErrorKind.Configuration, $"Failed to save discovery domain: {ex.Message}");
                        return Task.CompletedTask;
                    }
                    _log.LogInformation("Discovery domain set to {Domain} (applies on next refresh / connect)", sanitized);
                    PushIdentityInfo();
                    return Task.CompletedTask;
                }, default);

            case AppMessage.RequestIdentityInfo: return SerialAsync(_ =>
                {
                    PushIdentityInfo();
                    return Task.CompletedTask;
                }, default);

            case AppMessage.RefreshDiscovery: return SerialAsync(RefreshDiscoveryAsync, default);
            case AppMessage.SetSplitTunnel st: return SerialAsync(_ =>
                {
                    UpdateState(s => s with { SplitTunnelEnabled = st.Enabled });
                    _log.LogInformation("Split tunnel toggled to {Enabled} (applies on next connect)", st.Enabled);
                    return Task.CompletedTask;
                }, default);

            case AppMessage.ExportDiagnostics ed: return SerialAsync(_ =>
                {
                    var exporter = new DiagnosticsExporter(_stateStore,
                        _loggerFactory.CreateLogger<DiagnosticsExporter>());
                    exporter.Export(ed.TargetPath, _status);
                    return Task.CompletedTask;
                }, default);

            default:
                _log.LogWarning("Unknown message type {Type}", msg.GetType().Name);
                return Task.CompletedTask;
        }
    }

    // ---- internal lifecycle (always called under _serialize) ----

    private async Task ConnectInternalAsync(CancellationToken ct)
    {
        if (_transport is not null)
        {
            _log.LogInformation("Already connected — ignoring Connect");
            return;
        }

        // Whole body wrapped in try/catch so any uncaught throw cleans up
        // half-built session state instead of stranding `_transport != null`
        // across the next Connect IPC (Phase 1.9 review CRITICAL-3).
        try
        {
            await ConnectBodyAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ConnectInternalAsync threw — tearing down");
            await StopTunnelInternalAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ConnectBodyAsync(CancellationToken ct)
    {
        var config = BuildConfig();
        if (config is null)
        {
            EmitError(TunnelErrorKind.Configuration, "Missing brokers / clientName / password");
            return;
        }

        // 0. SRV-resolve the discovery domain, then probe each broker for
        // TCP RTT and reorder so the fastest reachable broker is at index
        // 0 of MqttTransport's failover list. Sticky-bias against the
        // last-known-good broker so a slightly slower probe doesn't
        // ping-pong us between brokers on every reconnect.
        //
        // Show the discovery domain (e.g. "vertices.ru") in the UI while
        // the resolver runs — broker hosts are only known after SRV
        // returns. Mirrors iOS / macOS where the picker says "Resolving…"
        // pre-discovery rather than guessing at a host.
        EmitStatus(ConnectionState.Connecting,
            currentBroker: config.DiscoveryDomain,
            currentExit:   config.SelectedExit);

        var resolved = await ResolveAndProbeBrokersAsync(config, ct).ConfigureAwait(false);
        if (resolved.Count == 0)
        {
            EmitError(TunnelErrorKind.DiscoveryTimeout,
                $"No brokers resolved for {config.DiscoveryDomain}");
            return;
        }
        config = config with { Brokers = resolved };
        _activeConfig = config;

        EmitStatus(ConnectionState.Connecting,
            currentBroker: config.Brokers[0].Host,
            currentExit:   config.SelectedExit);

        // 1. Identity.
        IdentityKey identity = _identityStore.LoadOrCreate();

        // 2. MqttTransport. Drop the broken Substring(0, 36) from the
        // earlier pre-review draft — Mosquitto's MQTT 5.0 path has no
        // 36-char client-id cap and chopping the trailing GUID erased
        // the disambiguator (Phase 1.9 review MAJOR-2).
        var transportLog = _loggerFactory.CreateLogger("Vertex.Mqtt");
        var transport = new MqttTransport(
            brokers: config.Brokers,
            username: config.MqttUsername,
            password: _passwordStore.Get() ?? string.Empty,
            clientId: $"vtx-client-{config.ClientName}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}",
            keepAliveSeconds: 30, // matches CLAUDE.md PINGRESP fix; iOS=30, Android=20 (doze).
            onAuthFailure: (rc, reason) =>
            {
                EmitError(TunnelErrorKind.Authentication, reason);
                // Schedule a teardown so the user actually settles in
                // Disconnected — auth failure stops MqttTransport's retry
                // loop but leaves our state half-up. Fire-and-forget on
                // the serial queue (NOT awaited — _serialize is
                // non-reentrant; the inner stop runs after our connect
                // body releases the lock).
                _ = SerialAsync(StopTunnelInternalAsync, default);
            },
            onFatalError: msg =>
            {
                EmitError(TunnelErrorKind.Unknown, msg);
                _ = SerialAsync(StopTunnelInternalAsync, default);
            },
            log: transportLog);
        _transport = transport;

        try
        {
            await transport.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MQTT connect failed");
            await StopTunnelInternalAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        // 3. Discovery + JOIN handshake.
        EmitStatus(ConnectionState.Handshaking, currentBroker: transport.CurrentBroker, currentExit: config.SelectedExit);
        var discovery = new DiscoveryTracker();
        _discovery = discovery;

        var handshake = new JoinHandshake(transport, discovery, _loggerFactory.CreateLogger("Vertex.Join"));
        JoinHandshake.JoinResult join;
        try
        {
            join = await handshake.RunAsync(
                config, identity,
                discoveryWait: config.DiscoveryWait ?? DefaultDiscoveryWait,
                joinTimeout:   config.JoinTimeout   ?? DefaultJoinTimeout,
                ct).ConfigureAwait(false);
        }
        catch (TransportException ex)
        {
            var kind = ex.Message.Contains("did not announce", StringComparison.OrdinalIgnoreCase)
                ? TunnelErrorKind.DiscoveryTimeout
                : TunnelErrorKind.JoinTimeout;
            _log.LogWarning(ex, "Join handshake failed");
            EmitError(kind, ex.Message);
            await StopTunnelInternalAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }
        _session = join.Session;

        // Stop spinning JSON-decode on every retained heartbeat broadcast
        // and any stray control replies — paritет with Kotlin TunnelEngine
        // post-handshake unsubscribe (Phase 1.9 review MINOR-7).
        try { transport.Unsubscribe(Topics.DiscoveryAll); }                catch { }
        try { transport.Unsubscribe(Topics.ControlAny(config.ClientName)); } catch { }

        // 4. WinTUN.
        var adapterGuid = AdapterGuidStore.LoadOrCreate();
        WintunDevice wintun;
        try
        {
            wintun = new WintunDevice(
                adapterName: config.AdapterName,
                stableGuid:  adapterGuid,
                log:         _loggerFactory.CreateLogger("Vertex.Wintun"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WinTUN open failed (admin required?)");
            EmitError(TunnelErrorKind.Configuration, "WinTUN open failed — service must run as Administrator / LocalSystem.");
            await StopTunnelInternalAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }
        _wintun = wintun;

        // 5. Bind IP → set MTU → install routes (in this order — review
        // CRITICAL-1 + CRITICAL-2). Without unicast bind, Windows source
        // selection picks the physical NIC; setting MTU after address
        // binding avoids the kernel resetting NlMtu when the interface
        // table is rebuilt.
        var routes = new RouteManager(_loggerFactory.CreateLogger("Vertex.Routes"));
        _routes = routes;

        byte prefix = IpHelperInterop.MaskToPrefix(IPAddress.Parse(join.AssignedMask));
        routes.SetTunAddress(wintun.AdapterLuid, IPAddress.Parse(join.AssignedIp), prefix);
        routes.SetTunMtu(wintun.AdapterLuid, config.Mtu);

        // Broker bypass: resolve every broker hostname to IPv4 and install
        // /32 routes via the physical NIC. Without this, the moment we
        // add the catch-all default-via-TUN routes the MQTT socket
        // itself loops back through the TUN that depends on it. Mirror
        // of Swift BrokerURL.resolveIPs() + excludedRoutes plumbing in
        // the iOS / macOS extensions.
        var bypassIps = await ResolveBrokerIpsAsync(config.Brokers, ct).ConfigureAwait(false);
        routes.AddBrokerBypass(bypassIps);

        // Split tunnel — when enabled, install RU CIDR routes through
        // the physical NIC at higher specificity (longer prefix wins
        // over the catch-all 0/1 + 128/1). Counterpart of macOS
        // excludedRoutes. The toggle lives in ServiceState.SplitTunnelEnabled.
        //
        // Order matters: SplitRouter.Apply must run BEFORE AddDefaultViaTun,
        // because Apply uses GetBestInterface/GetBestRoute2 to decide which
        // interface and next-hop to install each RU CIDR through. Once 0/1
        // sits on TUN, those probes match the TUN route and Apply would
        // happily install RU CIDRs on TUN — the exact opposite of split.
        var splitTunnel = (_stateStore.Load<ServiceState>() ?? new ServiceState()).SplitTunnelEnabled;
        if (splitTunnel)
        {
            try
            {
                var ruLoader = new RuNetsLoader(log: _loggerFactory.CreateLogger<RuNetsLoader>());
                _split = new SplitRouter(_loggerFactory.CreateLogger<SplitRouter>());
                _split.Apply(ruLoader.Load());
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Split tunnel apply failed — falling through to full tunnel");
                _split?.Cleanup();
                _split = null;
            }
        }

        routes.AddDefaultViaTun(wintun.AdapterLuid, IPAddress.Parse(join.AssignedGateway));

        // DNS — exits don't run a resolver on the gateway IP, so pin to
        // the same public resolvers iOS/Android use (Phase 1.9 review
        // MAJOR-1). TODO Phase 3: per-exit DNS server in heartbeat.
        var dns = new DnsLeakGuard(_loggerFactory.CreateLogger("Vertex.Dns"));
        _dns = dns;
        dns.Apply(new[] { "1.1.1.1", "8.8.8.8" });

        // 6. Packet pipeline. Start the pipeline FIRST (so the handler
        // is published) and only then subscribe — otherwise the
        // transport's worker thread can fire before _pipelineDownHandler
        // is non-null (Phase 1.9 review MAJOR-5).
        var pipeline = new PacketPipeline(
            tun:           wintun,
            publishUpload: payload => transport.Publish(Topics.Upload(join.Exit, config.ClientName), payload),
            mtu:           config.Mtu,
            log:           _loggerFactory.CreateLogger("Vertex.Pipeline"));
        _pipeline = pipeline;
        pipeline.SetSession(join.Session);
        pipeline.Start(handler => Volatile.Write(ref _pipelineDownHandler, handler));

        transport.Subscribe(Topics.Download(join.Exit, config.ClientName), (topic, payload) =>
        {
            var h = Volatile.Read(ref _pipelineDownHandler);
            h?.Invoke(payload);
        });

        _connectedSinceEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        EmitStatus(ConnectionState.Connected,
            currentBroker: transport.CurrentBroker,
            currentExit:   join.Exit,
            assignedIp:    join.AssignedIp);

        UpdateState(s => s with
        {
            LastGoodBroker = transport.CurrentBroker,
            LastGoodExit   = join.Exit,
        });

        // 7. Ping probe — start AFTER Connected so the first probe traverses
        // a fully built tunnel. PingMsChanged marshals back to the serial
        // queue so the status push runs in the same lane as state mutations.
        StartPingProbe();

        // 8. Stats pump — pushes a StatsEnvelope every 1s so the App's
        // TunnelViewModel can update its rolling-rate window for SpeedPill.
        StartStatsPump();

        // 8b. Path monitor — react to Wi-Fi ↔ Ethernet swaps and other
        // routing-table changes by re-running BrokerProbe and (if a
        // faster broker emerges) forcing a transport reconnect. Sticky
        // pingMs survives because PingProbe.Reset isn't called.
        StartPathMonitor();

        // 9. Surface the discovery snapshot so the App's ExitListPage can
        // render rows. The tracker has been collecting retained heartbeats
        // since JoinHandshake subscribed to discovery/exits/+, so the
        // snapshot is non-empty by now.
        PushDiscoverySnapshot();
    }

    /// <summary>
    /// Push the latest cached status + broker/discovery snapshots to the
    /// currently connected App. Used on pipe reconnect — App gets one
    /// frame of every kind it would have missed while detached.
    /// </summary>
    public Task PushCurrentStatusAsync() =>
        SerialAsync(_ =>
        {
            PushStatusAlways();
            // Identity snapshot first so the Settings tab can render the
            // editable client-name / domain / fingerprint without an
            // additional RequestIdentityInfo round-trip on attach.
            PushIdentityInfo();
            if (_lastBrokerInfos.Count > 0)
                _pipe.Push(new ExtensionResponse.BrokerUpdate(_lastBrokerInfos));
            if (_lastExitInfos.Count > 0)
                _pipe.Push(new ExtensionResponse.DiscoveryUpdate(_lastExitInfos));
            return Task.CompletedTask;
        }, default);

    /// <summary>Pipeline's download handler captured on Start so the transport's PUBLISH callback can hand it bytes.</summary>
    private Action<byte[]>? _pipelineDownHandler;

    private async Task StopTunnelInternalAsync(CancellationToken ct)
    {
        // Cancel + AWAIT the probe loop BEFORE Reset() so no in-flight
        // probe can re-set Current after we null it (PingProbe sticky
        // contract — see Net/PingProbe.cs Reset() XML doc).
        await StopPingProbeAsync().ConfigureAwait(false);
        await StopStatsPumpAsync().ConfigureAwait(false);
        StopPathMonitor();

        try { _pipeline?.Stop();          } catch (Exception ex) { _log.LogWarning(ex, "pipeline stop"); }
        try { _split?.Cleanup();          } catch (Exception ex) { _log.LogWarning(ex, "split cleanup"); }
        try { _routes?.Cleanup();         } catch (Exception ex) { _log.LogWarning(ex, "routes cleanup"); }
        try { _dns?.Cleanup();            } catch (Exception ex) { _log.LogWarning(ex, "dns cleanup"); }
        try { _wintun?.Dispose();         } catch (Exception ex) { _log.LogWarning(ex, "wintun dispose"); }
        if (_transport is { } t)
        {
            try { await t.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "transport dispose"); }
        }

        _pipeline = null;
        _split = null;
        _routes = null;
        _dns = null;
        _wintun = null;
        _transport = null;
        _session = null;
        _discovery = null;
        _pipelineDownHandler = null;
        _activeConfig = null;
        _connectedSinceEpochMs = 0;
        // _lastBrokerInfos / _lastExitInfos intentionally NOT cleared on
        // disconnect — they are a discovery cache, not session state.
        // The picker dialogs render this cache when the user opens them
        // before the first Connect, mirroring macOS where the broker /
        // exit lists survive disconnect (TunnelViewModel.swift:206-209).

        EmitStatus(ConnectionState.Disconnected);
    }

    // ---- ping probe lifecycle ----

    private void StartPingProbe()
    {
        _pingProbe = new PingProbe(log: _loggerFactory.CreateLogger<PingProbe>());
        _pingProbe.PingMsChanged += OnPingMsChanged;
        _pingProbeCts = new CancellationTokenSource();
        _pingProbeLoop = _pingProbe.RunAsync(_pingProbeCts.Token);
    }

    private async Task StopPingProbeAsync()
    {
        var probe = _pingProbe;
        var cts   = _pingProbeCts;
        var loop  = _pingProbeLoop;
        if (probe is null) return;

        try { cts?.Cancel(); } catch { /* swallow */ }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "ping probe loop awaited with throw"); }
        }
        probe.PingMsChanged -= OnPingMsChanged;
        probe.Reset(); // sticky -> null on actual disconnect

        try { cts?.Dispose(); } catch { /* swallow */ }
        _pingProbe = null;
        _pingProbeCts = null;
        _pingProbeLoop = null;
    }

    // ---- path monitor ----

    private void StartPathMonitor()
    {
        if (_pathMonitor is not null) return;
        _pathMonitor = new PathMonitor(_loggerFactory.CreateLogger<PathMonitor>());
        _pathMonitor.Changed += OnPathChanged;
        _pathMonitor.Start();
    }

    private void StopPathMonitor()
    {
        if (_pathMonitor is null) return;
        _pathMonitor.Changed -= OnPathChanged;
        try { _pathMonitor.Dispose(); } catch { /* swallow */ }
        _pathMonitor = null;
        _lastPathChange = DateTime.MinValue;
    }

    /// <summary>
    /// PathMonitor fires on a system threadpool thread; debounce in-line
    /// (interface adds + parameter-change notifications fire in bursts
    /// during a Wi-Fi→Ethernet hand-off) and marshal into the serial
    /// queue for the actual probe + reconnect work.
    /// </summary>
    private void OnPathChanged()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPathChange < PathDebounce) return;
        _lastPathChange = now;
        _ = SerialAsync(async ct =>
        {
            // Re-probe brokers; MqttTransport's own sticky reconnect
            // already retries the last connected broker on PINGRESP
            // timeout, but a path swap might bring back a faster
            // alternative that the transport hasn't re-evaluated.
            if (_activeConfig is null || _transport is null) return;
            await ResolveAndProbeBrokersAsync(_activeConfig, ct).ConfigureAwait(false);
            // ForceReconnect is exposed on IMqttTransport for exactly
            // this scenario; it tears the current TCP socket and
            // re-runs the failover list with the freshly-sorted brokers.
            try { _transport.ForceReconnect("path-changed"); }
            catch (Exception ex) { _log.LogWarning(ex, "ForceReconnect after path change threw"); }
        }, default);
    }

    // ---- stats pump ----

    private void StartStatsPump()
    {
        _statsPumpCts = new CancellationTokenSource();
        _statsPumpLoop = Task.Run(() => StatsPumpAsync(_statsPumpCts.Token));
    }

    private async Task StopStatsPumpAsync()
    {
        var cts = _statsPumpCts;
        var loop = _statsPumpLoop;
        if (loop is null) return;

        try { cts?.Cancel(); } catch { /* swallow */ }
        try { await loop.ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "stats pump awaited with throw"); }

        try { cts?.Dispose(); } catch { /* swallow */ }
        _statsPumpCts  = null;
        _statsPumpLoop = null;
    }

    private async Task StatsPumpAsync(CancellationToken ct)
    {
        // CRITICAL: pass the pump's `ct` to SerialAsync rather than `default`.
        // A deadlock window otherwise: pump tick fires while teardown is
        // already inside `_serialize` (StopTunnelInternalAsync), pump
        // awaits WaitAsync(default) forever, teardown awaits
        // _statsPumpLoop forever → engine wedged. Cancellation has to
        // propagate into the semaphore wait, not just the Task.Delay.
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await SerialAsync(_ => { PushStatsSnapshot(); return Task.CompletedTask; }, ct).ConfigureAwait(false);
                try { await Task.Delay(StatsPumpInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
    }

    private void PushStatsSnapshot()
    {
        var pipeline = _pipeline;
        if (pipeline is null) return;
        var stats = new TunnelStats(
            BytesUp:    pipeline.BytesUp,
            BytesDown:  pipeline.BytesDown,
            PacketsUp:  pipeline.PacketsUp,
            PacketsDown: pipeline.PacketsDown);
        _pipe.Push(new ExtensionResponse.StatsEnvelope(stats));
    }

    /// <summary>
    /// PingProbe fires this on its own thread when the sticky value
    /// transitions. Marshal back onto the serial queue so the status
    /// push runs in the same lane as every other state mutation.
    /// </summary>
    private void OnPingMsChanged(int? value)
    {
        // Fire-and-forget; SerialAsync logs any throw.
        _ = SerialAsync(_ => { PushPingStatus(); return Task.CompletedTask; }, default);
    }

    private void PushPingStatus()
    {
        // Mutate _status in place: only the pingMs field is fresh; every
        // other field was set by the last EmitStatus call. Pushing
        // through PushStatus runs the debounce check, which prevents a
        // duplicate status frame when nothing actually changed (e.g.,
        // probe transitioned but Reset already nulled _pingProbe).
        _status = _status with { PingMs = _pingProbe?.Current };
        PushStatus();
    }

    // ---- helpers ----

    private TunnelConfig? BuildConfig()
    {
        var state = _stateStore.Load<ServiceState>() ?? new ServiceState();

        // Default per-platform identity ("windows") for users with one
        // box, override-able via the SetClientName IPC. Sanitiser on the
        // ingress path keeps the stored value Mosquitto-safe; the fallback
        // here protects against an old state.json that pre-dates the field
        // (deserialiser would synthesise an empty string under non-default
        // JsonSerializerOptions). Surface the fallback so admins debugging
        // a hand-edited state.json see why the connection lands as
        // "windows" rather than the override they expected.
        string clientName;
        if (string.IsNullOrWhiteSpace(state.ClientName))
        {
            _log.LogWarning("state.json has empty ClientName — falling back to default \"windows\"");
            clientName = "windows";
        }
        else
        {
            clientName = state.ClientName;
        }

        // Brokers come from SRV resolution only. The discovery domain is
        // the single point of truth — paritет с iOS / macOS / Android,
        // which never carry hardcoded broker FQDNs in the binary.
        // SrvResolver.ResolveWithFallbackAsync handles the cache fallback
        // chain (primary → cached backup → cached previous result).
        return new TunnelConfig(
            ClientName:      clientName,
            MqttUsername:    $"vtx-client-{clientName}",
            Brokers:         Array.Empty<BrokerUrl>(),
            SelectedExit:    state.SelectedExit,
            DiscoveryDomain: state.DiscoveryDomain);
    }

    /// <summary>
    /// Resolve every broker hostname to IPv4 addresses for /32 broker
    /// bypass routes. Hostnames that fail to resolve are skipped with a
    /// warning — bypass-routes must be best-effort because a broker
    /// listed in the SRV zone but down for maintenance shouldn't block
    /// the rest of the failover list. Duplicates collapse so a host
    /// served on both 8883 and 443 only installs one /32.
    /// </summary>
    private async Task<IEnumerable<IPAddress>> ResolveBrokerIpsAsync(
        IReadOnlyList<BrokerUrl> brokers, CancellationToken ct)
    {
        var seen = new HashSet<IPAddress>();
        var result = new List<IPAddress>();
        foreach (var broker in brokers)
        {
            var ips = await broker.ResolveIpsAsync(ct).ConfigureAwait(false);
            if (ips.Count == 0)
            {
                _log.LogWarning("Broker {Host} did not resolve to any IPv4 — bypass route skipped", broker.Host);
                continue;
            }
            foreach (var ip in ips)
            {
                if (seen.Add(ip)) result.Add(ip);
            }
        }
        return result;
    }

    /// <summary>
    /// Resolve the discovery domain via DoH, then run TCP RTT probes
    /// across every broker URL the zone declares and return the list
    /// reordered by ascending RTT.
    /// <para>
    /// No engine-level sticky bias — paritет with iOS / macOS, where the
    /// extension probes + reorders and continuity is handled at the
    /// transport layer (<see cref="MqttTransport"/>'s own sticky
    /// reconnect, which retries the most recently connected broker
    /// first on PINGRESP timeout). LastGoodBroker is still persisted
    /// for diagnostics but does NOT bias broker order.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<BrokerUrl>> ResolveAndProbeBrokersAsync(
        TunnelConfig config, CancellationToken ct)
    {
        // SrvResolver.ResolveWithFallbackAsync already implements the full
        // fallback chain (primary → cached backup → cached previous
        // result → null). Identical algorithm to iOS / macOS / Android
        // SRVDiscovery.resolveWithFallback. No extra hardcoded broker
        // list at this layer — the discovery domain is the single point
        // of truth.
        SrvDiscoveryResult? srv = null;
        try
        {
            srv = await _srvResolver.ResolveWithFallbackAsync(config.DiscoveryDomain, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "SRV discovery threw");
        }

        if (srv is not { Brokers.Count: > 0 })
        {
            _log.LogError("SRV discovery returned no brokers for {Domain}", config.DiscoveryDomain);
            return Array.Empty<BrokerUrl>();
        }

        var brokers = srv.BrokerUrls.Select(BrokerUrl.Parse).ToList();

        var (sortedReadOnly, rttMs) = await BrokerProbe.ReorderWithRttsAsync(brokers, ct: ct).ConfigureAwait(false);
        var sorted = sortedReadOnly.ToList();
        _log.LogInformation("Broker probe: {Order}", BrokerProbe.FormatOrder(sorted, rttMs));

        // Honor the user's manual broker pick: if state.SelectedBroker is
        // a literal URL that matches one of the resolved brokers, pin it
        // to position 0. Mirrors macOS TunnelViewModel.connect() which
        // moves the selected URL to the head of orderedURLs before
        // building the TunnelConfig. Without this the picker is a UI
        // lie — checkmark shows pinned to YC but engine still RTT-sorts
        // to whichever is fastest.
        var state = _stateStore.Load<ServiceState>();
        var pickedUrl = state?.SelectedBroker;
        if (!string.IsNullOrEmpty(pickedUrl) && pickedUrl != "auto" &&
            BrokerUrl.TryParse(pickedUrl, out var pinned))
        {
            int idx = sorted.FindIndex(b => b.Host == pinned.Host && b.Port == pinned.Port && b.Scheme == pinned.Scheme);
            if (idx > 0)
            {
                var x = sorted[idx];
                sorted.RemoveAt(idx);
                sorted.Insert(0, x);
                _log.LogInformation("Broker pin: moved {Host}:{Port} from idx {Idx} to head", pinned.Host, pinned.Port, idx);
            }
            else if (idx < 0)
            {
                _log.LogWarning("SelectedBroker {Url} not present in resolved set — falling through to RTT order", pickedUrl);
            }
        }

        // Surface the resolved + probed broker list to the App so the
        // BrokerListPage can render the picker rows. Repeated probe
        // sweeps replace the previous snapshot.
        var infos = sorted.Select(u => new Vertex.Shared.Ipc.BrokerInfo(
            Id:     u.Host,
            Url:    u.ToString(),
            RttMs:  rttMs.TryGetValue(u.Host, out var ms) ? ms : null,
            Ok:     rttMs.ContainsKey(u.Host),
            Detail: null)).ToList();
        _lastBrokerInfos = infos;
        _pipe.Push(new ExtensionResponse.BrokerUpdate(infos));

        return sorted;
    }

    /// <summary>
    /// Re-resolve SRV + probe brokers + push fresh BrokerUpdate /
    /// DiscoveryUpdate to the App. Safe to call while connected — does
    /// NOT tear down the active session.
    /// </summary>
    private async Task RefreshDiscoveryAsync(CancellationToken ct)
    {
        if (_activeConfig is null)
        {
            // Outside an active session: still useful to refresh broker
            // probe + SRV cache so the picker shows live data before the
            // next Connect.
            var cfg = BuildConfig();
            if (cfg is null) return;
            await ResolveAndProbeBrokersAsync(cfg, ct).ConfigureAwait(false);
            return;
        }

        await ResolveAndProbeBrokersAsync(_activeConfig, ct).ConfigureAwait(false);
        PushDiscoverySnapshot();
    }

    /// <summary>
    /// Push the current DiscoveryTracker snapshot to the App. Called
    /// after JOIN handshake (initial heartbeats collected) and on
    /// explicit RefreshDiscovery requests.
    /// </summary>
    private void PushDiscoverySnapshot()
    {
        if (_discovery is null) return;
        var snap = _discovery.Snapshot();
        if (snap.Count == 0) return;

        var brokerHost = _transport?.CurrentBroker ?? string.Empty;
        // Pull SRV TXT display names from the cached SrvDiscoveryResult
        // — these were resolved during SRV discovery and persisted in
        // state.json. Service-side cache so the App receives one bundle
        // and doesn't need a separate lookup.
        var displayNames = _stateStore.Load<ServiceState>()?.LastSrv?.ExitDisplayNames
            ?? new Dictionary<string, string>();
        var exits = snap.Select(e => new Vertex.Shared.Ipc.ExitInfo(
            Id:           e.Id,
            Country:      e.Country,
            Clients:      e.Clients,
            Capacity:     e.MaxClients,
            BrokerRttMs:  e.BrokerRttMs.TryGetValue(brokerHost, out var rtt) ? rtt : 0,
            Score:        ScoreExit(e, brokerHost),
            StaleSeconds: (DateTime.UtcNow - e.ReceivedAt).TotalSeconds,
            DisplayName:  displayNames.TryGetValue(e.Id, out var dn) ? dn : null)).ToList();
        _lastExitInfos = exits;
        _pipe.Push(new ExtensionResponse.DiscoveryUpdate(exits));
    }

    private static double ScoreExit(Vertex.Core.Discovery.ExitInfo info, string brokerHost)
    {
        // Mirror DiscoveryTracker.ScoreLocked at the IPC boundary so the
        // App can render exits in the same order the engine picks them.
        int rtt = info.BrokerRttMs.TryGetValue(brokerHost, out var r) ? r : 100;
        double cap = info.MaxClients > 0 ? info.MaxClients : 253;
        return rtt * (1.0 + info.Clients / cap * 2.0);
    }

    private void UpdateState(Func<ServiceState, ServiceState> mutate)
    {
        var current = _stateStore.Load<ServiceState>() ?? new ServiceState();
        var next = mutate(current);
        _stateStore.Save(next);
    }

    /// <summary>
    /// Push the identity snapshot (client name, X25519 pubkey hex, SRV
    /// domain) so Settings can render its Identity / Discovery tabs.
    /// LoadOrCreate is the same call used by ConnectBodyAsync — a Reset
    /// followed by this push surfaces the freshly generated key without
    /// requiring the user to connect first. The IdentityKey is disposed
    /// immediately; we only need the hex.
    /// <para>
    /// <b>Side effect</b>: LoadOrCreate generates and persists a fresh
    /// X25519 keypair on first call if <c>identity.bin</c> doesn't exist.
    /// That means opening Settings before the very first Connect creates
    /// the device identity — a divergence from the macOS reference, where
    /// the Keychain key is generated lazily on Connect. Reasonable on
    /// Windows because the App can't reach DPAPI directly (Service-owned),
    /// so the Service-side eager-generate avoids an empty fingerprint UI.
    /// </para>
    /// </summary>
    private void PushIdentityInfo()
    {
        var state = _stateStore.Load<ServiceState>() ?? new ServiceState();
        var clientName = string.IsNullOrWhiteSpace(state.ClientName) ? "windows" : state.ClientName;
        string pubkeyHex;
        try
        {
            using var identity = _identityStore.LoadOrCreate();
            pubkeyHex = identity.PublicKeyHex;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Identity load failed during PushIdentityInfo");
            pubkeyHex = string.Empty;
        }
        _pipe.Push(new ExtensionResponse.IdentityInfo(
            ClientName: clientName,
            PubkeyHex:  pubkeyHex,
            Domain:     state.DiscoveryDomain));
    }

    /// <summary>
    /// Mosquitto-safe normalisation: trim, lowercase, accept only
    /// <c>[a-z0-9_-]</c>, length 1..32, no leading or trailing
    /// dash / underscore. Returns <c>null</c> on rejection. Mirrors the
    /// constraints baked into the broker's password_file generation step
    /// (<c>vtx-admin add</c> rejects anything else); the leading/trailing
    /// guard avoids ACL globs going odd around <c>vtx-client--foo</c>.
    /// </summary>
    private static string? SanitizeClientName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().ToLowerInvariant();
        if (trimmed.Length is 0 or > 32) return null;
        // Reject leading/trailing dash or underscore for ACL hygiene.
        if (trimmed[0] is '-' or '_' || trimmed[^1] is '-' or '_') return null;
        foreach (var ch in trimmed)
        {
            bool ok = (ch >= 'a' && ch <= 'z')
                   || (ch >= '0' && ch <= '9')
                   || ch == '-' || ch == '_';
            if (!ok) return null;
        }
        return trimmed;
    }

    /// <summary>
    /// DNS-name validation: trim, lowercase, length 1..253, at least one
    /// dot, each label 1..63 of <c>[a-z0-9-]</c>, no leading / trailing
    /// hyphen. IDN zones (e.g. <c>пример.рф</c>) are converted to A-label
    /// form via <see cref="System.Globalization.IdnMapping"/> before
    /// validation — DoH wire requires ASCII, so the on-disk value must be
    /// punycode. Strict enough to keep <c>SrvResolver</c> from embedding
    /// garbage into a DoH query.
    /// </summary>
    private static string? SanitizeDomain(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().TrimEnd('.');
        if (trimmed.Length == 0) return null;

        // IDN → punycode. Throws on truly malformed input; treat as reject.
        string ascii;
        try
        {
            var idn = new System.Globalization.IdnMapping { AllowUnassigned = false, UseStd3AsciiRules = true };
            ascii = idn.GetAscii(trimmed).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (ascii.Length is 0 or > 253) return null;
        var labels = ascii.Split('.');
        if (labels.Length < 2) return null;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63) return null;
            if (label[0] == '-' || label[^1] == '-') return null;
            foreach (var ch in label)
            {
                bool ok = (ch >= 'a' && ch <= 'z')
                       || (ch >= '0' && ch <= '9')
                       || ch == '-';
                if (!ok) return null;
            }
        }
        return ascii;
    }

    private void EmitStatus(ConnectionState state,
        string? currentBroker = null, string? currentExit = null, string? assignedIp = null)
    {
        _status = new ConnectionStatus(
            State:                  state,
            AssignedIp:             assignedIp,
            CurrentBroker:          currentBroker,
            CurrentExit:            currentExit,
            ConnectedSinceEpochMs:  _connectedSinceEpochMs == 0 ? null : _connectedSinceEpochMs,
            // Carry the sticky pingMs across status emissions while the
            // probe is alive; on Disconnected the probe is already
            // Reset() so Current is null — matches the IPC contract that
            // only an actual disconnect clears the field.
            PingMs:                 _pingProbe?.Current);
        PushStatus();
    }

    private void EmitError(TunnelErrorKind kind, string detail)
    {
        var report = new TunnelErrorReport(kind, detail, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _pipe.Push(new ExtensionResponse.ErrorEnvelope(report));
        _log.LogWarning("Fatal: {Kind}: {Detail}", kind, detail);
    }

    /// <summary>
    /// Push only when <see cref="_status"/> structurally differs from
    /// the last push. Used by transition emitters (EmitStatus,
    /// PushPingStatus) so a no-op tick doesn't spam the IPC channel.
    /// </summary>
    private void PushStatus()
    {
        if (_lastPushedStatus is { } prev && prev.Equals(_status)) return;
        _lastPushedStatus = _status;
        _pipe.Push(new ExtensionResponse.StatusEnvelope(_status));
    }

    /// <summary>
    /// Push the current status unconditionally — used for App-side
    /// replay paths (<c>RequestStatus</c>, <c>OnClientConnected</c>) so
    /// a freshly attached App always receives one frame even if nothing
    /// has logically changed since the last broadcast.
    /// </summary>
    private void PushStatusAlways()
    {
        _lastPushedStatus = _status;
        _pipe.Push(new ExtensionResponse.StatusEnvelope(_status));
    }

    private async Task SerialAsync(Func<CancellationToken, Task> body, CancellationToken ct)
    {
        await _serialize.WaitAsync(ct).ConfigureAwait(false);
        try { await body(ct).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogError(ex, "Serialised body threw"); }
        finally { _serialize.Release(); }
    }
}
