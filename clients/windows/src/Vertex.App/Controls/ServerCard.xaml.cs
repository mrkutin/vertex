using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Vertex.App.Controls;

public sealed partial class ServerCard : UserControl
{
    public static readonly DependencyProperty VertexDisplayNameProperty =
        DependencyProperty.Register(nameof(VertexDisplayName), typeof(string), typeof(ServerCard),
            new PropertyMetadata("Auto", (d, e) => ((ServerCard)d).VertexValue.Text = (string)e.NewValue));

    public static readonly DependencyProperty VertexCodeProperty =
        DependencyProperty.Register(nameof(VertexCode), typeof(string), typeof(ServerCard),
            new PropertyMetadata(null, (d, e) => ((ServerCard)d).VertexCode.Text = (string)e.NewValue ?? string.Empty));

    public static readonly DependencyProperty EdgeDisplayNameProperty =
        DependencyProperty.Register(nameof(EdgeDisplayName), typeof(string), typeof(ServerCard),
            new PropertyMetadata("Auto", (d, e) => ((ServerCard)d).EdgeValue.Text = (string)e.NewValue));

    public static readonly DependencyProperty EdgeCodeProperty =
        DependencyProperty.Register(nameof(EdgeCode), typeof(string), typeof(ServerCard),
            new PropertyMetadata(null, (d, e) => ((ServerCard)d).EdgeCode.Text = (string)e.NewValue ?? string.Empty));

    public static readonly DependencyProperty IsCardEnabledProperty =
        DependencyProperty.Register(nameof(IsCardEnabled), typeof(bool), typeof(ServerCard),
            new PropertyMetadata(true, (d, e) =>
            {
                var c = (ServerCard)d;
                bool enabled = (bool)e.NewValue;
                c.BrokerRow.IsEnabled = enabled;
                c.ExitRow.IsEnabled   = enabled;
                c.Opacity = enabled ? 1.0 : 0.55;
            }));

    public string VertexDisplayName { get => (string)GetValue(VertexDisplayNameProperty); set => SetValue(VertexDisplayNameProperty, value); }
    public string? VertexCodeText   { get => (string?)GetValue(VertexCodeProperty);       set => SetValue(VertexCodeProperty, value); }
    public string EdgeDisplayName   { get => (string)GetValue(EdgeDisplayNameProperty);   set => SetValue(EdgeDisplayNameProperty, value); }
    public string? EdgeCodeText     { get => (string?)GetValue(EdgeCodeProperty);         set => SetValue(EdgeCodeProperty, value); }
    public bool   IsCardEnabled     { get => (bool)GetValue(IsCardEnabledProperty);       set => SetValue(IsCardEnabledProperty, value); }

    public event RoutedEventHandler? BrokerTapped;
    public event RoutedEventHandler? ExitTapped;

    public ServerCard()
    {
        InitializeComponent();
        // Seed the TextBlocks from the DependencyProperty defaults —
        // the PropertyChangedCallback only fires on value transitions,
        // so a fresh ServerCard with the default "Auto" never paints
        // unless we copy the seed manually here.
        VertexValue.Text = VertexDisplayName ?? "Auto";
        EdgeValue.Text   = EdgeDisplayName   ?? "Auto";
    }

    private void OnBrokerClick(object sender, RoutedEventArgs e) => BrokerTapped?.Invoke(this, e);
    private void OnExitClick(object sender, RoutedEventArgs e)   => ExitTapped?.Invoke(this, e);
}
