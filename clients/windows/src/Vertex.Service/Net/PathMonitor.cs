using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vertex.Service.Net;

/// <summary>
/// Watches the system's IP-interface table for changes — Wi-Fi → Ethernet
/// hand-off, USB modem hot-plug, captive-portal flips. Mirror of macOS
/// <c>NWPathMonitor</c>: when the routing table changes, dispatch a
/// <c>NotifyPathChanged</c>-equivalent callback so the engine can re-probe
/// brokers and (if a faster one became reachable) sticky-reconnect.
///
/// <para>Backed by Win32 <c>NotifyIpInterfaceChange</c>. The callback fires
/// on a system threadpool thread; the consumer is responsible for
/// marshaling onto the engine's serial queue.</para>
/// </summary>
public sealed class PathMonitor : IDisposable
{
    /// <summary>
    /// Fired on every IP-interface change (initial-state-snapshot
    /// included). Caller debounces; PathMonitor doesn't.
    /// </summary>
    public event Action? Changed;

    private readonly ILogger _log;
    private IntPtr _handle = IntPtr.Zero;
    // Keep a strong reference to the delegate — Win32 callback table
    // holds the raw function pointer, not a managed root, so without
    // this field the delegate is GC'd and the next change crashes the
    // service with an AccessViolation.
    private NotifyIpInterfaceChangeCallback? _callback;

    public PathMonitor(ILogger? log = null) => _log = log ?? NullLogger.Instance;

    public void Start()
    {
        if (_handle != IntPtr.Zero) return;

        _callback = OnInterfaceChanged;
        // initialNotification: false — we get a snapshot at registration
        // time anyway via the engine's own state push; an extra
        // synthetic event during Connect would interrupt itself.
        int rc = NotifyIpInterfaceChange(
            family: AF_UNSPEC,
            callback: _callback,
            callerContext: IntPtr.Zero,
            initialNotification: false,
            notificationHandle: out _handle);
        if (rc != 0)
        {
            _log.LogWarning("NotifyIpInterfaceChange failed with Win32 error {Err}", rc);
            _handle = IntPtr.Zero;
            _callback = null;
        }
        else
        {
            _log.LogInformation("PathMonitor started");
        }
    }

    public void Stop()
    {
        if (_handle == IntPtr.Zero) return;
        try { CancelMibChangeNotify2(_handle); }
        catch (Exception ex) { _log.LogDebug(ex, "CancelMibChangeNotify2 threw"); }
        _handle = IntPtr.Zero;
        _callback = null;
        _log.LogInformation("PathMonitor stopped");
    }

    public void Dispose() => Stop();

    private void OnInterfaceChanged(IntPtr context, IntPtr row, MibNotificationType type)
    {
        // Ignore initial snapshot; only react to genuine adds / deletes /
        // parameter-changes (route metric changes, DHCP renewals, etc.).
        // Initial type is `MibParameterNotification` for every existing
        // interface — too noisy for our consumer.
        try { Changed?.Invoke(); }
        catch (Exception ex) { _log.LogWarning(ex, "Path-change handler threw"); }
    }

    // ---- P/Invoke ----

    private const uint AF_UNSPEC = 0;

    private enum MibNotificationType
    {
        ParameterNotification = 0,
        AddInstance           = 1,
        DeleteInstance        = 2,
        InitialNotification   = 3,
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void NotifyIpInterfaceChangeCallback(IntPtr context, IntPtr row, MibNotificationType type);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int NotifyIpInterfaceChange(
        uint family,
        NotifyIpInterfaceChangeCallback callback,
        IntPtr callerContext,
        [MarshalAs(UnmanagedType.U1)] bool initialNotification,
        out IntPtr notificationHandle);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int CancelMibChangeNotify2(IntPtr notificationHandle);
}
