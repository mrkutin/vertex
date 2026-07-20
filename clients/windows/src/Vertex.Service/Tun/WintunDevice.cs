using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vertex.Service.Tun;

/// <summary>
/// Real WinTUN-backed implementation of <see cref="ITunDevice"/>. Owns
/// one adapter + one session for the lifetime of the instance.
///
/// Thread-safety: WinTUN sessions are documented thread-safe for
/// concurrent receive/send, but the C handle becomes invalid the moment
/// <c>WintunCloseAdapter</c> returns — any in-flight pointer from
/// <c>WintunReceivePacket</c> / <c>WintunAllocateSendPacket</c> would
/// then alias driver-freed memory and an unsafe span copy crashes the
/// service. We therefore serialise all hot-path access through a lock
/// and use a shutdown event to break out of the indefinite read wait.
///
/// Lifetime: <see cref="SignalShutdown"/> is the cooperative way to
/// unblock <see cref="ReceivePacket"/> from the outside without tearing
/// down the adapter. <see cref="Dispose"/> additionally closes the
/// session and adapter; idempotent and single-shot.
///
/// Phase 1.7 ships the lifecycle + receive / send hot path. IP address,
/// routes, MTU, and DNS are configured by Phase 1.8 RouteManager /
/// DnsLeakGuard against <see cref="AdapterLuid"/>.
/// </summary>
public sealed class WintunDevice : ITunDevice
{
    /// <summary>Tunnel type advertised to the driver (shows up in Device Manager).</summary>
    public const string DefaultTunnelType = "Vertex";

    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly IntPtr _adapter;
    private readonly IntPtr _session;
    private readonly IntPtr _readWaitEvent;
    private readonly IntPtr _shutdownEvent;
    private int _disposed;

    /// <summary>NETWORK LUID of the underlying adapter — feed into iphlpapi route / DNS APIs.</summary>
    public ulong AdapterLuid { get; }

    /// <summary>
    /// Open or create a WinTUN adapter and start a session on it.
    /// </summary>
    /// <param name="adapterName">Visible name (e.g. <c>"Vertex"</c>).</param>
    /// <param name="stableGuid">
    ///   Optional stable adapter GUID. Reusing the same GUID across
    ///   service restarts gives a stable LUID, which keeps RouteManager /
    ///   DnsLeakGuard bindings valid after a process crash. Pass
    ///   <c>null</c> to let WinTUN auto-generate a fresh GUID per session
    ///   (the adapter then becomes non-persistent).
    /// </param>
    /// <param name="ringCapacity">
    ///   Send / receive ring capacity in bytes. Must be a power of two
    ///   between 0x20000 (128 KiB) and 0x4000000 (64 MiB). Default 4 MiB
    ///   matches the WireGuard reference and gives ~30 ms of buffering
    ///   at gigabit.
    /// </param>
    public WintunDevice(
        string adapterName,
        Guid? stableGuid = null,
        uint ringCapacity = WintunInterop.DefaultRingCapacity,
        ILogger? log = null)
    {
        _log = log ?? NullLogger.Instance;

        // Try to open an existing adapter first; on a clean install we
        // create one. Both calls require admin (kernel driver INF).
        IntPtr adapter = WintunInterop.WintunOpenAdapter(adapterName);
        if (adapter == IntPtr.Zero)
        {
            int openErr = Marshal.GetLastPInvokeError();
            _log.LogDebug("WintunOpenAdapter('{Name}') → Win32 error {Err} — will create", adapterName, openErr);

            adapter = stableGuid is Guid g
                ? CreateAdapterWithGuid(adapterName, DefaultTunnelType, g)
                : WintunInterop.WintunCreateAdapter(adapterName, DefaultTunnelType, IntPtr.Zero);

            if (adapter == IntPtr.Zero)
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"WintunCreateAdapter('{adapterName}', '{DefaultTunnelType}') failed with Win32 error {err}.");
            }
            _log.LogInformation("Created WinTUN adapter '{Name}'", adapterName);
        }
        else
        {
            _log.LogInformation("Opened existing WinTUN adapter '{Name}'", adapterName);
        }

        _adapter = adapter;

        WintunInterop.WintunGetAdapterLuid(adapter, out ulong luid);
        AdapterLuid = luid;

        IntPtr session = WintunInterop.WintunStartSession(adapter, ringCapacity);
        if (session == IntPtr.Zero)
        {
            int err = Marshal.GetLastPInvokeError();
            WintunInterop.WintunCloseAdapter(adapter);
            throw new InvalidOperationException(
                $"WintunStartSession failed with Win32 error {err}.");
        }
        _session = session;
        _readWaitEvent = WintunInterop.WintunGetReadWaitEvent(session);

        // Manual-reset event signalled by Dispose / SignalShutdown to
        // wake the receive loop out of an indefinite wait without
        // tearing the session down from another thread.
        _shutdownEvent = WintunInterop.CreateEvent(IntPtr.Zero, bManualReset: true, bInitialState: false, lpName: null);
        if (_shutdownEvent == IntPtr.Zero)
        {
            int err = Marshal.GetLastPInvokeError();
            WintunInterop.WintunEndSession(session);
            WintunInterop.WintunCloseAdapter(adapter);
            throw new InvalidOperationException(
                $"CreateEvent(shutdown) failed with Win32 error {err}.");
        }
    }

    private static IntPtr CreateAdapterWithGuid(string name, string tunnelType, Guid guid)
    {
        // Marshal the Guid value into unmanaged memory so we can pass an
        // IntPtr to the WinTUN ABI (the C function expects a `GUID*`,
        // which the C# function pointer would otherwise box differently).
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(guid, ptr, fDeleteOld: false);
            return WintunInterop.WintunCreateAdapter(name, tunnelType, ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public int ReceivePacket(Span<byte> destination)
    {
        while (true)
        {
            int err;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return 0;

                IntPtr ptr = WintunInterop.WintunReceivePacket(_session, out uint size);
                if (ptr != IntPtr.Zero)
                {
                    int n = (int)size;
                    if (n > destination.Length)
                    {
                        // Oversized frame — release back to the ring and
                        // signal the caller to skip this iteration without
                        // tearing the pipeline down (a single bad frame
                        // shouldn't kill the tunnel).
                        WintunInterop.WintunReleaseReceivePacket(_session, ptr);
                        _log.LogWarning(
                            "Oversized WinTUN frame: {Got}B > buffer {Buf}B — dropping",
                            n, destination.Length);
                        return -1;
                    }
                    unsafe { new ReadOnlySpan<byte>((void*)ptr, n).CopyTo(destination); }
                    WintunInterop.WintunReleaseReceivePacket(_session, ptr);
                    return n;
                }

                err = Marshal.GetLastPInvokeError();
            }

            if (err == WintunInterop.ErrorNoMoreItems)
            {
                // Ring empty — wait outside the lock so Dispose can
                // proceed. Wake on either the driver's read-event or
                // our shutdown event.
                var handles = new[] { _readWaitEvent, _shutdownEvent };
                uint w = WintunInterop.WaitForMultipleObjects(
                    (uint)handles.Length, handles, bWaitAll: false, WintunInterop.WaitInfinite);

                if (w == WintunInterop.WaitObject0)             continue; // packet ready
                if (w == WintunInterop.WaitObject0 + 1)         return 0; // shutdown
                if (Volatile.Read(ref _disposed) != 0)          return 0;

                throw new InvalidOperationException(
                    $"WaitForMultipleObjects on WinTUN events returned 0x{w:X8}.");
            }

            if (Volatile.Read(ref _disposed) != 0) return 0;
            throw new InvalidOperationException(
                $"WintunReceivePacket failed with Win32 error {err}.");
        }
    }

    public bool SendPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty) return true;

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(WintunDevice));
            }

            IntPtr buf = WintunInterop.WintunAllocateSendPacket(_session, (uint)packet.Length);
            if (buf == IntPtr.Zero)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == WintunInterop.ErrorBufferOverflow)
                {
                    return false; // ring full — caller increments drop counter
                }
                throw new InvalidOperationException(
                    $"WintunAllocateSendPacket({packet.Length}) failed with Win32 error {err}.");
            }
            unsafe { packet.CopyTo(new Span<byte>((void*)buf, packet.Length)); }
            WintunInterop.WintunSendPacket(_session, buf);
            return true;
        }
    }

    /// <summary>
    /// Wake the receive loop without disposing the adapter. The receive
    /// loop sees a signalled <see cref="_shutdownEvent"/>, returns 0, and
    /// the caller's pipeline thread exits cleanly. Multiple calls are
    /// idempotent. Use this when the owner needs to hand the adapter
    /// over to a fresh pipeline (e.g. exit auto-switch in Phase 2)
    /// without tearing down WinTUN.
    /// </summary>
    public void SignalShutdown()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        WintunInterop.SetEvent(_shutdownEvent);
    }

    public void Dispose()
    {
        // Single-shot. Take the lock so any in-flight receive / send
        // completes (or sees _disposed != 0) before we close the session.
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            if (_shutdownEvent != IntPtr.Zero)
            {
                try { WintunInterop.SetEvent(_shutdownEvent); }    catch { }
            }
            try { WintunInterop.WintunEndSession(_session); }      catch { }
            try { WintunInterop.WintunCloseAdapter(_adapter); }    catch { }
            if (_shutdownEvent != IntPtr.Zero)
            {
                try { WintunInterop.CloseHandle(_shutdownEvent); } catch { }
            }
        }
    }
}
