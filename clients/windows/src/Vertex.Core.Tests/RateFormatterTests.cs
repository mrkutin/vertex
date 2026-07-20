using FluentAssertions;
using Vertex.Core.Util;
using Xunit;

namespace Vertex.Core.Tests;

/// <summary>
/// Anchors the SpeedPill formatter ladder. Numbers below 1 Kbps suppress
/// to "—" so a one-packet keepalive (~70 bytes/s) doesn't paint as
/// "0 Kbps"; the 1 Mbps and 1 Gbps thresholds match SI decimal prefixes
/// (Speedtest / ISP convention) — paritет с macOS SpeedPillView.
/// </summary>
public class RateFormatterTests
{
    [Theory]
    [InlineData(0,         "—")]
    [InlineData(100,       "—")]   // 800 bps below 1 Kbps
    [InlineData(124,       "—")]   // 992 bps below 1 Kbps
    [InlineData(125,       "1 Kbps")]
    [InlineData(124_999,   "1000 Kbps")]   // just under 1 Mbps boundary
    [InlineData(125_000,   "1.0 Mbps")]    // exactly at 1 Mbps boundary
    [InlineData(200_000,   "1.6 Mbps")]
    [InlineData(125_000_000, "1.0 Gbps")]
    public void FormatBitsPerSec_Ladder(double bytesPerSec, string expected)
    {
        RateFormatter.FormatBitsPerSec(bytesPerSec).Should().Be(expected);
    }

    [Fact]
    public void FormatBitsPerSec_NegativeOrNaN_RendersDash()
    {
        // Defense-in-depth: rolling-rate counter reset is supposed to
        // collapse to 0 in InstantRate, but if anything slips through
        // the formatter must not show "-1.0 Mbps".
        RateFormatter.FormatBitsPerSec(-1).Should().Be("—");
        RateFormatter.FormatBitsPerSec(double.NaN).Should().Be("—");
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0,    "0 ms")]
    [InlineData(42,   "42 ms")]
    [InlineData(9999, "9999 ms")]
    public void FormatPingMs_NumericOrDash(int? ms, string expected)
    {
        RateFormatter.FormatPingMs(ms).Should().Be(expected);
    }
}
