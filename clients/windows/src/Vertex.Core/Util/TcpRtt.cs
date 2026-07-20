using System.Diagnostics;
using System.Net.Sockets;

namespace Vertex.Core.Util;

/// <summary>
/// Measures the time from <c>TcpClient.ConnectAsync</c> issue to the
/// connection becoming established (one TCP round-trip — SYN → SYN+ACK).
/// TLS / WebSocket handshake is intentionally NOT included so the
/// reading reflects path latency, not certificate-chain cost.
///
/// Mirror of Swift <c>TCPRTT.measure</c> (NWConnection-based) and Kotlin
/// <c>TcpRtt.measure</c> (java.net.Socket-based). Returns elapsed
/// milliseconds on success, <c>null</c> on timeout / refusal / no path.
/// </summary>
public static class TcpRtt
{
    /// <summary>
    /// Probe <paramref name="host"/>:<paramref name="port"/> once. The
    /// returned task either yields the elapsed milliseconds or
    /// <c>null</c> for any failure (timeout, ICMP unreachable, refused,
    /// DNS failure, …). Always cancels the underlying socket on
    /// resolution so a slow broker doesn't leave a half-open connection
    /// sitting on the path.
    /// </summary>
    public static async Task<int?> MeasureAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var tcp = new TcpClient { NoDelay = true };
        var sw = Stopwatch.StartNew();
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return (int)sw.Elapsed.TotalMilliseconds;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { tcp.Client.Close(0); } catch { /* swallow */ }
            try { tcp.Dispose(); }      catch { /* swallow */ }
        }
    }
}
