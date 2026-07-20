using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vertex.Core.Util;

namespace Vertex.App.Controls;

/// <summary>
/// Compact capsule rendered next to <see cref="StatusPill"/> while a
/// session is active. Three columns: ↑ upload rate · ↓ download rate ·
/// 🕐 ping. Color-codes the ping icon (green &lt;80ms, blue &lt;200ms,
/// amber ≥200ms, tertiary when nil) — mirror of Swift
/// <c>SpeedPillView</c>.
/// </summary>
public sealed partial class SpeedPill : UserControl
{
    public static readonly DependencyProperty UploadBytesPerSecProperty =
        DependencyProperty.Register(nameof(UploadBytesPerSec), typeof(double), typeof(SpeedPill),
            new PropertyMetadata(0.0, (d, _) => ((SpeedPill)d).Refresh()));

    public static readonly DependencyProperty DownloadBytesPerSecProperty =
        DependencyProperty.Register(nameof(DownloadBytesPerSec), typeof(double), typeof(SpeedPill),
            new PropertyMetadata(0.0, (d, _) => ((SpeedPill)d).Refresh()));

    public static readonly DependencyProperty PingMsProperty =
        DependencyProperty.Register(nameof(PingMs), typeof(int?), typeof(SpeedPill),
            new PropertyMetadata(null, (d, _) => ((SpeedPill)d).Refresh()));

    public double UploadBytesPerSec   { get => (double)GetValue(UploadBytesPerSecProperty);   set => SetValue(UploadBytesPerSecProperty, value); }
    public double DownloadBytesPerSec { get => (double)GetValue(DownloadBytesPerSecProperty); set => SetValue(DownloadBytesPerSecProperty, value); }
    public int?   PingMs              { get => (int?)GetValue(PingMsProperty);                set => SetValue(PingMsProperty, value); }

    public SpeedPill()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        UpText.Text   = RateFormatter.FormatBitsPerSec(UploadBytesPerSec);
        DownText.Text = RateFormatter.FormatBitsPerSec(DownloadBytesPerSec);
        PingText.Text = RateFormatter.FormatPingMs(PingMs);
        PingIcon.Foreground = (Brush)Application.Current.Resources[PingIconBrushKey(PingMs)];
    }

    /// <summary>
    /// Brush key for the ping icon at <paramref name="ms"/>. Pure
    /// function so unit tests can assert the band → key mapping without
    /// a XAML root.
    /// <para>
    /// Note on color collapse: in the current palette
    /// <c>StateConnected</c> and <c>AccentPrimary</c> resolve to the
    /// same hex (#7DB3FF), so the &lt;80 vs &lt;200 visual step is a
    /// no-op today. The semantic distinction is preserved for the day
    /// the palette introduces a distinct stateConnectedHigh tone.
    /// </para>
    /// </summary>
    internal static string PingIconBrushKey(int? ms) => ms switch
    {
        null  => "TextTertiaryBrush",
        < 80  => "StateConnectedBrush",
        < 200 => "AccentPrimaryBrush",
        _     => "StateTransitioningBrush",
    };
}
