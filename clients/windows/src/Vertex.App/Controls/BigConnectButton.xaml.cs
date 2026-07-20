using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vertex.Shared;
using Windows.UI;

namespace Vertex.App.Controls;

public sealed partial class BigConnectButton : UserControl
{
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(ConnectionState), typeof(BigConnectButton),
            new PropertyMetadata(ConnectionState.Disconnected, (d, _) => ((BigConnectButton)d).Refresh()));

    public ConnectionState State { get => (ConnectionState)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    public event RoutedEventHandler? Activated;

    public BigConnectButton()
    {
        InitializeComponent();
        Loaded += (_, _) => RecenterScale();
        SizeChanged += (_, _) => RecenterScale();
        Refresh();
    }

    private void RecenterScale()
    {
        PressScale.CenterX = ActualWidth  / 2;
        PressScale.CenterY = ActualHeight / 2;
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e) => Activated?.Invoke(this, new RoutedEventArgs());

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
    }
    private void OnPointerExited (object sender, PointerRoutedEventArgs e) { PressScale.ScaleX = 1.0; PressScale.ScaleY = 1.0; }
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e) { PressScale.ScaleX = 0.97; PressScale.ScaleY = 0.97; }
    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) { PressScale.ScaleX = 1.0; PressScale.ScaleY = 1.0; }

    private void Refresh()
    {
        bool transitioning = State is ConnectionState.Connecting
                                  or ConnectionState.Handshaking
                                  or ConnectionState.Reconnecting;
        bool connected = State == ConnectionState.Connected;

        Spinner.Visibility = transitioning ? Visibility.Visible : Visibility.Collapsed;

        Label.Text = transitioning ? "Cancel"
                  : connected     ? "Disconnect"
                                  : "Connect";

        // Fill: idle = accent, transitioning = muted, connected = surfaceMuted (red text).
        Brush fill = transitioning
            ? (Brush)Application.Current.Resources["AccentPrimaryMutedBrush"]
            : connected
                ? (Brush)Application.Current.Resources["BgSurfaceMutedBrush"]
                : (Brush)Application.Current.Resources["AccentPrimaryBrush"];

        Brush textColor = connected
            ? (Brush)Application.Current.Resources["StateErrorBrush"]
            : transitioning
                ? (Brush)Application.Current.Resources["TextPrimaryBrush"]
                : new SolidColorBrush(Color.FromArgb(0xFF, 0x08, 0x0F, 0x26)); // bgCanvas — readable on accent

        Capsule.Background = fill;
        Label.Foreground = textColor;
    }
}
