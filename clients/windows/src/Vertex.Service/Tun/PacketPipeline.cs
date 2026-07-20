using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Core.Crypto;

namespace Vertex.Service.Tun;

/// <summary>
/// Drives the two TUN ↔ MQTT packet flows.
/// <list type="bullet">
///   <item><b>up</b> thread: read IP packet from TUN → ChaCha20-Poly1305 seal → publish on the outbound topic.</item>
///   <item><b>down</b> handler: subscribe → open → write to TUN.</item>
/// </list>
/// Mirror of Kotlin <c>PacketPipeline</c> (Android) and Swift's
/// <c>PacketTunnelProvider</c> data plane (iOS / macOS). The crypto session
/// is settable post-start so the engine can swap it on exit auto-switch
/// without restarting the threads (Phase 2+); Phase 1 sets it once after
/// the join handshake.
///
/// IP version filter: silently drops IPv6 packets on both directions
/// (parity with Kotlin Phase 1; the tunnel is IPv4-only). Dropped frames
/// are counted in <see cref="PacketsDroppedV6"/> so a misconfigured route
/// or OS-generated link-local v6 traffic is observable.
///
/// Ownership: this pipeline does NOT own the <see cref="ITunDevice"/>.
/// The owner (typically <c>TunnelEngine</c>) constructs the device,
/// hands it in, and is responsible for <see cref="IDisposable.Dispose"/>.
/// On <see cref="Stop"/> we call <see cref="WintunDevice.SignalShutdown"/>
/// (or its <see cref="ITunDevice"/> contract equivalent — see helper
/// below) to wake the up-loop without closing the adapter, so the same
/// adapter can survive a pipeline rebuild on exit auto-switch.
/// </summary>
public sealed class PacketPipeline : IDisposable
{
    /// <summary>
    /// Conservative MTU — same reasoning as Android: ChaCha20-Poly1305 (28B
    /// overhead) wrapped in MQTT/TLS/TCP/IP/Wi-Fi headers spills past the
    /// 1500-byte physical MTU; ICMP "frag needed" is dropped by most RU
    /// ISPs (DPI rule), so PMTU discovery black-holes. 1300 leaves
    /// &gt;180 B headroom per frame for every layer above TCP. WireGuard
    /// / OpenVPN use the same default.
    /// </summary>
    public const int DefaultMtu = 1300;

    /// <summary>Read-buffer slack over MTU — over-sized frames show as <c>n &gt; MTU</c> rather than silent truncation.</summary>
    private const int ReadBufferSlack = 16;

    /// <summary>
    /// Hands one outbound (already-sealed) packet to the MQTT transport.
    /// MAY throw if the transport is mid-shutdown; the up loop catches and
    /// exits cleanly. Implementers SHOULD NOT block — this is on the hot
    /// path and any blocking work directly throttles TUN read throughput.
    /// </summary>
    public delegate void PublishUpload(ReadOnlyMemory<byte> sealedPacket);

    /// <summary>
    /// Subscribes the inbound (encrypted) topic with a handler the
    /// pipeline supplies. Called once on <see cref="Start"/>.
    /// </summary>
    public delegate void DownloadRegistrar(Action<byte[]> handler);

    private readonly ITunDevice _tun;
    private readonly PublishUpload _publishUpload;
    private readonly int _mtu;
    private readonly ILogger _log;

    // SetSession is one-shot in Phase 1; Phase 2 exit-switch must not
    // Dispose the old crypto until the up-thread is drained — see
    // Phase 1.7 review MAJOR-6 for the RCU-style replacement plan.
    private SessionCrypto? _crypto;

    private Thread? _upThread;
    private int _running; // 0 = stopped, 1 = running — atomic via Interlocked

    // Counters surface to the host UI.
    private long _bytesUp, _bytesDown, _packetsUp, _packetsDown;
    private long _decryptErrors, _packetsDroppedV6, _packetsDroppedDown;
    public long BytesUp             => Interlocked.Read(ref _bytesUp);
    public long BytesDown           => Interlocked.Read(ref _bytesDown);
    public long PacketsUp           => Interlocked.Read(ref _packetsUp);
    public long PacketsDown         => Interlocked.Read(ref _packetsDown);
    public long DecryptErrors       => Interlocked.Read(ref _decryptErrors);
    public long PacketsDroppedV6    => Interlocked.Read(ref _packetsDroppedV6);
    public long PacketsDroppedDown  => Interlocked.Read(ref _packetsDroppedDown);

    public PacketPipeline(
        ITunDevice tun,
        PublishUpload publishUpload,
        int mtu = DefaultMtu,
        ILogger? log = null)
    {
        _tun = tun;
        _publishUpload = publishUpload;
        _mtu = mtu;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Set or replace the per-session crypto state. Until called, the up
    /// loop drops every packet (no key to seal with) and the down handler
    /// drops every payload (no key to open with) — parity with
    /// pre-handshake silence on Android.
    /// </summary>
    public void SetSession(SessionCrypto crypto)
    {
        Volatile.Write(ref _crypto, crypto);
    }

    public void Start(DownloadRegistrar register)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        // Download direction: the MqttTransport's serial worker invokes
        // this handler. No additional thread needed — it's already
        // sequenced and uses our (single-writer) tun.SendPacket API.
        register(HandleDownload);

        // Upload direction: dedicated background thread because
        // ITunDevice.ReceivePacket blocks indefinitely (WinTUN
        // WaitForMultipleObjects(INFINITE)) — exactly the case PLAN.md
        // calls out as "must be a Thread, not a Task".
        var t = new Thread(RunUpLoop)
        {
            Name = "vtx-tun-up",
            IsBackground = true,
        };
        _upThread = t;
        t.Start();
    }

    /// <summary>
    /// Cooperative stop. Wakes the up-loop via the device's shutdown
    /// signal and joins the thread. Does NOT dispose the device — the
    /// owner (typically <c>TunnelEngine</c>) does that.
    /// </summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;

        // Wake the receive loop without closing the adapter. WintunDevice
        // exposes SignalShutdown; for any other ITunDevice impl the
        // device.Dispose path also unblocks the loop (FakeTunDevice
        // marks the queue complete-for-adding).
        if (_tun is WintunDevice wd) wd.SignalShutdown();

        try { _upThread?.Join(TimeSpan.FromSeconds(5)); } catch { /* swallow */ }
        _upThread = null;
    }

    public void Dispose() => Stop();

    // ---- private ----

    private void RunUpLoop()
    {
        var buf = new byte[_mtu + ReadBufferSlack];
        try
        {
            while (Volatile.Read(ref _running) != 0)
            {
                int n;
                try { n = _tun.ReceivePacket(buf); }
                catch (Exception ex)
                {
                    if (Volatile.Read(ref _running) != 0)
                    {
                        _log.LogWarning(ex, "TUN read failed — pipeline stopping");
                    }
                    return;
                }
                if (n == 0) return;          // shutdown / closed
                if (n < 0)  continue;         // oversized — already dropped at device layer

                if (!IsIPv4(buf.AsSpan(0, n)))
                {
                    Interlocked.Increment(ref _packetsDroppedV6);
                    continue;
                }

                var crypto = Volatile.Read(ref _crypto);
                if (crypto is null) continue;   // drop until handshake completes

                byte[] sealedPacket;
                try { sealedPacket = crypto.Seal(buf.AsSpan(0, n)); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Seal failed; dropping packet");
                    continue;
                }

                // Re-check running before publishing — Stop() may have
                // flipped the flag while we were sealing, and pushing
                // one last frame through MqttTransport after teardown
                // shows up as a ghost frame in stats.
                if (Volatile.Read(ref _running) == 0) return;

                try { _publishUpload(sealedPacket); }
                catch (Exception ex)
                {
                    if (Volatile.Read(ref _running) != 0)
                    {
                        _log.LogWarning(ex, "Publish threw; pipeline stopping");
                    }
                    return;
                }

                Interlocked.Add(ref _bytesUp, n);
                Interlocked.Increment(ref _packetsUp);
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _running) != 0) _log.LogError(ex, "Up loop crashed");
        }
    }

    private void HandleDownload(byte[] sealedPayload)
    {
        if (Volatile.Read(ref _running) == 0) return;
        var crypto = Volatile.Read(ref _crypto);
        if (crypto is null) return;

        byte[] plain;
        try { plain = crypto.Open(sealedPayload); }
        catch (Exception ex)
        {
            long n = Interlocked.Increment(ref _decryptErrors);
            if (n <= 5) _log.LogWarning(ex, "Decrypt failed (#{N})", n);
            return;
        }

        if (!IsIPv4(plain))
        {
            Interlocked.Increment(ref _packetsDroppedV6);
            return;
        }

        try
        {
            bool sent = _tun.SendPacket(plain);
            if (!sent)
            {
                long dropped = Interlocked.Increment(ref _packetsDroppedDown);
                if (dropped <= 5) _log.LogWarning("WinTUN send ring overflow (#{N}) — dropping packet", dropped);
                return;
            }
            Interlocked.Add(ref _bytesDown, plain.Length);
            Interlocked.Increment(ref _packetsDown);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TUN write failed");
        }
    }

    private static bool IsIPv4(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty) return false;
        return ((packet[0] >> 4) & 0x0F) == 4;
    }
}
