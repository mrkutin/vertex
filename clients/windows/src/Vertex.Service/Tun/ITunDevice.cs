namespace Vertex.Service.Tun;

/// <summary>
/// Abstraction over a TUN device used by <see cref="PacketPipeline"/>.
/// One real implementation (<see cref="WintunDevice"/>) plus a test
/// double in <c>Vertex.Service.Tests</c>.
///
/// The contract intentionally mirrors what WinTUN gives us:
/// <list type="bullet">
///   <item><see cref="ReceivePacket"/> blocks until a packet arrives, an
///   error occurs, or the device is closed (returns 0).</item>
///   <item><see cref="SendPacket"/> hands one IP packet to the driver
///   for queue-up to userland-bound delivery.</item>
/// </list>
/// </summary>
public interface ITunDevice : IDisposable
{
    /// <summary>
    /// Read one IP packet from the device into <paramref name="destination"/>.
    /// Blocks until a packet arrives, the device is shut down, or an
    /// error occurs.
    /// Returns:
    ///   <list type="bullet">
    ///     <item><c>0</c> — device was shut down (see <see cref="WintunDevice.SignalShutdown"/>) or closed; caller stops the loop.</item>
    ///     <item><c>-1</c> — single oversized / malformed frame was dropped; caller should continue without stopping.</item>
    ///     <item><c>&gt;0</c> — number of bytes written to the buffer.</item>
    ///   </list>
    /// </summary>
    /// <exception cref="InvalidOperationException">Underlying driver returned an unrecoverable error.</exception>
    int ReceivePacket(Span<byte> destination);

    /// <summary>
    /// Send one IP packet downstream. Returns <c>true</c> if the packet
    /// was queued, <c>false</c> if the driver's send ring is full and the
    /// packet was dropped (caller should increment a drop counter).
    /// </summary>
    /// <exception cref="ObjectDisposedException">Device has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Driver returned an unrecoverable error.</exception>
    bool SendPacket(ReadOnlySpan<byte> packet);
}
