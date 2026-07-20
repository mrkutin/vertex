using System.Globalization;

namespace Vertex.Core.Util;

/// <summary>
/// Network-rate / latency formatters shared between the Windows
/// SpeedPill UI and any future diagnostics view. Mirror of Swift
/// <c>SpeedPillView.formatRate</c> + <c>formatPing</c>.
/// </summary>
public static class RateFormatter
{
    /// <summary>
    /// Bytes-per-second → human-readable bits-per-second string.
    /// Decimal SI prefixes (Kbps / Mbps / Gbps) — convention for
    /// network speeds, matches Speedtest and ISP advertised throughput.
    /// Returns "—" below 1 Kbps to suppress noise.
    /// </summary>
    public static string FormatBitsPerSec(double bytesPerSec)
    {
        if (double.IsNaN(bytesPerSec) || bytesPerSec < 0) return "—";
        var bps = bytesPerSec * 8;
        if (bps < 1_000)            return "—";
        // InvariantCulture so the output is locale-stable (always decimal
        // point) — matches the macOS / iOS rendering, and the App-side
        // PingMs / rate display is intentionally not localised because
        // the unit suffixes (Kbps/Mbps/Gbps) are English-only.
        var c = CultureInfo.InvariantCulture;
        if (bps >= 1_000_000_000)   return string.Format(c, "{0:F1} Gbps", bps / 1_000_000_000);
        if (bps >= 1_000_000)       return string.Format(c, "{0:F1} Mbps", bps / 1_000_000);
        return string.Format(c, "{0:F0} Kbps", bps / 1_000);
    }

    /// <summary>
    /// RTT in milliseconds → "{N} ms", or "—" when nil. nil happens
    /// while the first probe is in flight or after a Disconnect cleared
    /// the sticky value (per <c>ConnectionStatus.PingMs</c> contract).
    /// </summary>
    public static string FormatPingMs(int? ms)
        => ms is null ? "—" : $"{ms} ms";
}
