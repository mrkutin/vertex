using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vertex.App.Controls.Glyphs;
using Vertex.App.ViewModels;

namespace Vertex.App.Views;

/// <summary>
/// Picker for the broker URL list. Mirror of Swift
/// <c>BrokerListView</c> — "Auto" pseudo-row first (subtitle shows the
/// resolved broker short-name while connected, "Lowest TCP RTT"
/// otherwise), then every SRV-resolved URL with host + scheme port.
/// Refresh button surfaces the App's <c>RefreshDiscovery</c> IPC.
/// </summary>
public sealed partial class BrokerListDialog : ContentDialog
{
    private readonly TunnelViewModel _vm;
    private NotifyCollectionChangedEventHandler? _brokersChanged;

    public BrokerListDialog(TunnelViewModel vm, XamlRoot root)
    {
        _vm = vm;
        InitializeComponent();
        XamlRoot = root;
        Loaded += (_, _) =>
        {
            Render();
            _brokersChanged = (_, _) => Render();
            _vm.AvailableBrokers.CollectionChanged += _brokersChanged;
            _vm.PropertyChanged += OnVmChanged;
        };
        Closed += (_, _) =>
        {
            if (_brokersChanged is not null)
                _vm.AvailableBrokers.CollectionChanged -= _brokersChanged;
            _vm.PropertyChanged -= OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TunnelViewModel.SelectedBroker)
                          or nameof(TunnelViewModel.CurrentBroker)
                          or nameof(TunnelViewModel.IsConnected))
        {
            Render();
        }
    }

    private async void OnRefreshClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Don't dismiss the dialog on Refresh — the list updates in place.
        args.Cancel = true;
        await _vm.RefreshDiscoveryAsync();
    }

    private void OnCloseClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) { /* default close */ }

    private void Render()
    {
        RowsHost.Children.Clear();

        // "Auto" pseudo-row.
        RowsHost.Children.Add(BuildAutoRow(
            selected: _vm.IsSelectedBrokerAuto,
            subtitle: _vm.IsConnected && !string.IsNullOrEmpty(_vm.CurrentBroker)
                ? $"Now: {ShortName(_vm.CurrentBroker!)}"
                : "Lowest TCP RTT"));

        for (int i = 0; i < _vm.AvailableBrokers.Count; i++)
        {
            RowsHost.Children.Add(BuildDivider());
            var url = _vm.AvailableBrokers[i];
            RowsHost.Children.Add(BuildUrlRow(url, selected: url == _vm.SelectedBroker));
        }
    }

    private Border BuildDivider() => new()
    {
        Background = (Brush)Application.Current.Resources["BorderSubtleBrush"],
        Height = 0.5,
        Margin = new Thickness(56, 0, 0, 0),
    };

    private UIElement BuildAutoRow(bool selected, string subtitle)
    {
        var grid = MakeRowGrid();
        grid.Children.Add(WithColumn(new VxAsteriskGlyph { GlyphSize = 22, VerticalAlignment = VerticalAlignment.Center }, 0));
        grid.Children.Add(WithColumn(MakeTextBlock("Auto", subtitle, selected), 1));
        if (selected) grid.Children.Add(WithColumn(MakeCheckmark(), 2));
        var btn = WrapInButton(grid);
        btn.Tapped += async (_, _) =>
        {
            await _vm.SetSelectedBrokerAsync("auto");
            Hide();
        };
        return btn;
    }

    private UIElement BuildUrlRow(string url, bool selected)
    {
        var grid = MakeRowGrid();
        grid.Children.Add(WithColumn(new VxAsteriskGlyph { GlyphSize = 22, VerticalAlignment = VerticalAlignment.Center }, 0));

        var host = HostFromUrl(url);
        var scheme = SchemeFromUrl(url);
        // Subscript chip: V₀ · YC, V₁ · SBER, … (parity with macOS
        // NodeLabels.vertexLabel). Index is position in the SRV-resolved
        // list, deduped by host so the same broker on 8883/443 shares a
        // chip number.
        var uniqueHosts = _vm.AvailableBrokers.Select(HostFromUrl).Distinct().ToList();
        var idx = uniqueHosts.IndexOf(host);
        var subtitle = idx >= 0
            ? $"{scheme}    V{ExitListDialog.Subscript(idx)} · {ShortName(host)}"
            : scheme;

        grid.Children.Add(WithColumn(MakeTextBlock(host, subtitle, selected), 1));
        if (selected) grid.Children.Add(WithColumn(MakeCheckmark(), 2));

        var btn = WrapInButton(grid);
        btn.Tapped += async (_, _) =>
        {
            await _vm.SetSelectedBrokerAsync(url);
            Hide();
        };
        return btn;
    }

    private static Grid MakeRowGrid()
    {
        var g = new Grid { Padding = new Thickness(8, 12, 8, 12), MinHeight = 56 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return g;
    }

    private static T WithColumn<T>(T el, int col) where T : FrameworkElement
    {
        Grid.SetColumn(el, col);
        return el;
    }

    private static StackPanel MakeTextBlock(string title, string subtitle, bool emphasized)
    {
        var sp = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            FontSize = 15,
            FontWeight = emphasized ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        });
        sp.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
        });
        return sp;
    }

    private static FontIcon MakeCheckmark() => new()
    {
        Glyph = "",
        FontSize = 14,
        Foreground = (Brush)Application.Current.Resources["AccentPrimaryBrush"],
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Button WrapInButton(UIElement content)
    {
        return new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = content,
        };
    }

    internal static string HostFromUrl(string url)
        => Vertex.Core.Config.BrokerUrl.TryParse(url, out var b) ? b.Host : url;

    internal static string SchemeFromUrl(string url)
    {
        if (!Vertex.Core.Config.BrokerUrl.TryParse(url, out var b)) return "";
        return $"{b.Scheme.ToUpperInvariant()} · {b.Port}";
    }

    /// <summary>
    /// Short name extracted from a broker host: <c>mqtt-yc.vertices.ru</c>
    /// → <c>YC</c>. Mirror of Swift NodeLabels.vertexLabel.
    /// </summary>
    internal static string ShortName(string host)
    {
        var trimmed = host.StartsWith("mqtt-", System.StringComparison.Ordinal) ? host[5..] : host;
        var dot = trimmed.IndexOf('.');
        var first = dot > 0 ? trimmed[..dot] : trimmed;
        return first.ToUpperInvariant();
    }
}
