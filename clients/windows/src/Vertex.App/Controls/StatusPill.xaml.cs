using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vertex.Shared;

namespace Vertex.App.Controls;

public sealed partial class StatusPill : UserControl
{
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(ConnectionState), typeof(StatusPill),
            new PropertyMetadata(ConnectionState.Disconnected, (d, _) => ((StatusPill)d).Refresh()));

    public static readonly DependencyProperty AssignedIpProperty =
        DependencyProperty.Register(nameof(AssignedIp), typeof(string), typeof(StatusPill),
            new PropertyMetadata(null, (d, _) => ((StatusPill)d).Refresh()));

    public static readonly DependencyProperty ConnectedSinceEpochMsProperty =
        DependencyProperty.Register(nameof(ConnectedSinceEpochMs), typeof(long?), typeof(StatusPill),
            new PropertyMetadata(null, (d, _) => ((StatusPill)d).Refresh()));

    public ConnectionState State           { get => (ConnectionState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public string? AssignedIp              { get => (string?)GetValue(AssignedIpProperty);    set => SetValue(AssignedIpProperty, value); }
    public long? ConnectedSinceEpochMs     { get => (long?)GetValue(ConnectedSinceEpochMsProperty); set => SetValue(ConnectedSinceEpochMsProperty, value); }

    private readonly DispatcherTimer _ticker;

    public StatusPill()
    {
        InitializeComponent();
        _ticker = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => Refresh();
        Loaded   += (_, _) => _ticker.Start();
        Unloaded += (_, _) => _ticker.Stop();
        Refresh();
    }

    private void Refresh()
    {
        // Dot color + glow visibility.
        Brush dotBrush = State switch
        {
            ConnectionState.Connected
                => (Brush)Application.Current.Resources["StateConnectedBrush"],
            ConnectionState.Connecting or ConnectionState.Handshaking or ConnectionState.Reconnecting
                => (Brush)Application.Current.Resources["StateTransitioningBrush"],
            _   => (Brush)Application.Current.Resources["StateDormantBrush"],
        };
        Dot.Fill = dotBrush;
        Glow.Opacity = State == ConnectionState.Connected ? 0.55 : 0.0;
        Glow.Fill = dotBrush;

        Label.Text = State switch
        {
            ConnectionState.Connected when ConnectedSinceEpochMs is long ms && AssignedIp is { Length: > 0 } ip
                => $"{ip}  ·  {Uptime(ms)}",
            ConnectionState.Connected when AssignedIp is { Length: > 0 } ip
                => ip,
            ConnectionState.Connected     => "Connected",
            ConnectionState.Connecting    => "Connecting…",
            ConnectionState.Handshaking   => "Handshaking…",
            ConnectionState.Reconnecting  => "Reconnecting…",
            _                              => "Not connected",
        };
    }

    private static string Uptime(long startEpochMs)
    {
        long nowMs   = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long elapsed = System.Math.Max(0, (nowMs - startEpochMs) / 1000);
        long h = elapsed / 3600;
        long m = (elapsed % 3600) / 60;
        long s = elapsed % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }
}
