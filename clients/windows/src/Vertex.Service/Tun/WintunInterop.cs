using System.Runtime.InteropServices;

namespace Vertex.Service.Tun;

/// <summary>
/// P/Invoke surface for WinTUN 0.14.1 (<c>wintun.dll</c>). WinTUN is the
/// kernel-mode TUN driver shipped by WireGuard — Microsoft-attested
/// signed driver, so we don't need EV signing for the user-mode wrapper.
/// Reference docs: <c>https://www.wintun.net</c>.
///
/// Pointer / handle convention (matches the WinTUN C API):
/// <list type="bullet">
///   <item><c>WINTUN_ADAPTER_HANDLE</c> ⟶ <see cref="IntPtr"/>, opaque per-adapter handle.</item>
///   <item><c>WINTUN_SESSION_HANDLE</c> ⟶ <see cref="IntPtr"/>, opaque per-session handle.</item>
///   <item>Packet buffers are <see cref="IntPtr"/> + length, allocated and
///   released by the driver — see Receive / Send APIs.</item>
/// </list>
///
/// All P/Invokes use <c>SetLastError = true</c> so callers translate via
/// <see cref="Marshal.GetLastPInvokeError"/> on failure.
/// </summary>
internal static class WintunInterop
{
    private const string Lib = "wintun.dll";

    /// <summary>Maximum packet size accepted by Send / Receive (per WinTUN docs).</summary>
    public const int MaxIpPacketSize = 0xFFFF;

    /// <summary>Recommended ring-buffer capacity in bytes (must be a power of two between 0x20000 and 0x4000000).</summary>
    public const uint DefaultRingCapacity = 0x400000; // 4 MiB

    // ---- Adapter lifecycle ----

    [DllImport(Lib, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr WintunCreateAdapter(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string tunnelType,
        IntPtr requestedGuid); // null → auto-generate

    [DllImport(Lib, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr WintunOpenAdapter(
        [MarshalAs(UnmanagedType.LPWStr)] string name);

    [DllImport(Lib, SetLastError = true)]
    public static extern void WintunCloseAdapter(IntPtr adapter);

    // WinTUN exports the function as `WintunGetAdapterLUID` (uppercase
    // initialism) — not `Luid`. Without the explicit EntryPoint the
    // PInvoke marshaller looks for a wrong-cased symbol and throws
    // EntryPointNotFoundException at first call.
    [DllImport(Lib, SetLastError = true, EntryPoint = "WintunGetAdapterLUID")]
    public static extern void WintunGetAdapterLuid(IntPtr adapter, out ulong luid);

    // ---- Session lifecycle ----

    [DllImport(Lib, SetLastError = true)]
    public static extern IntPtr WintunStartSession(IntPtr adapter, uint capacity);

    [DllImport(Lib, SetLastError = true)]
    public static extern void WintunEndSession(IntPtr session);

    /// <summary>
    /// Returns a Windows event handle that becomes signalled when at least
    /// one packet is waiting in the receive ring. Used together with
    /// <see cref="WintunReceivePacket"/> to implement blocking reads:
    /// poll → if NULL → <c>WaitForSingleObject(event, INFINITE)</c> → retry.
    /// </summary>
    [DllImport(Lib, SetLastError = true)]
    public static extern IntPtr WintunGetReadWaitEvent(IntPtr session);

    // ---- Receive (driver → user) ----

    /// <summary>
    /// Returns a pointer to the next IP packet in the ring, with
    /// <paramref name="packetSize"/> set. Returns <see cref="IntPtr.Zero"/>
    /// when the ring is empty or on error (check <see cref="Marshal.GetLastPInvokeError"/>;
    /// <c>ERROR_NO_MORE_ITEMS = 259</c> means "ring empty, wait", anything
    /// else is fatal). Caller must call <see cref="WintunReleaseReceivePacket"/>
    /// after consuming, otherwise the ring stalls.
    /// </summary>
    [DllImport(Lib, SetLastError = true)]
    public static extern IntPtr WintunReceivePacket(IntPtr session, out uint packetSize);

    [DllImport(Lib, SetLastError = true)]
    public static extern void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

    // ---- Send (user → driver) ----

    /// <summary>
    /// Reserves <paramref name="packetSize"/> bytes in the send ring.
    /// Returns <see cref="IntPtr.Zero"/> if the ring is full
    /// (<c>ERROR_BUFFER_OVERFLOW = 111</c>) — callers should drop the
    /// packet rather than retry tightly.
    /// </summary>
    [DllImport(Lib, SetLastError = true)]
    public static extern IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);

    [DllImport(Lib, SetLastError = true)]
    public static extern void WintunSendPacket(IntPtr session, IntPtr packet);

    // ---- Win32 wait + event primitives ----

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern uint WaitForMultipleObjects(
        uint nCount,
        [In] IntPtr[] lpHandles,
        [MarshalAs(UnmanagedType.Bool)] bool bWaitAll,
        uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateEventW", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateEvent(
        IntPtr lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpName);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    public const uint WaitInfinite = 0xFFFFFFFF;
    public const uint WaitObject0  = 0x00000000;
    public const int  ErrorNoMoreItems    = 259;
    public const int  ErrorBufferOverflow = 111;
}
