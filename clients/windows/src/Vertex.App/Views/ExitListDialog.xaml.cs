using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vertex.App.Controls.Glyphs;
using Vertex.App.ViewModels;

namespace Vertex.App.Views;

/// <summary>
/// Picker for the exit ID list. Mirror of Swift <c>ExitListView</c> —
/// "Auto" pseudo-row (subtitle "Now: {city/ID}" while connected, "Best
/// edge per latency" otherwise) followed by every heartbeat-known exit.
/// Display-name override comes from <see cref="TunnelViewModel.ExitDisplayNames"/>;
/// when missing, the row falls back to the uppercased exit ID.
/// </summary>
public sealed partial class ExitListDialog : ContentDialog
{
    private readonly TunnelViewModel _vm;
    private NotifyCollectionChangedEventHandler? _exitsChanged;

    public ExitListDialog(TunnelViewModel vm, XamlRoot root)
    {
        _vm = vm;
        InitializeComponent();
        XamlRoot = root;
        Loaded += (_, _) =>
        {
            Render();
            _exitsChanged = (_, _) => Render();
            _vm.AvailableExits.CollectionChanged += _exitsChanged;
            _vm.PropertyChanged += OnVmChanged;
        };
        Closed += (_, _) =>
        {
            if (_exitsChanged is not null)
                _vm.AvailableExits.CollectionChanged -= _exitsChanged;
            _vm.PropertyChanged -= OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TunnelViewModel.SelectedExit)
                          or nameof(TunnelViewModel.CurrentExit)
                          or nameof(TunnelViewModel.IsConnected))
        {
            Render();
        }
    }

    private async void OnRefreshClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        await _vm.RefreshDiscoveryAsync();
    }

    private void OnCloseClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) { /* default close */ }

    private void Render()
    {
        RowsHost.Children.Clear();

        // "Auto" pseudo-row.
        string subtitle;
        if (_vm.IsConnected && !string.IsNullOrEmpty(_vm.CurrentExit))
        {
            var label = _vm.ExitDisplayNames.TryGetValue(_vm.CurrentExit!, out var n)
                ? n : _vm.CurrentExit!.ToUpperInvariant();
            subtitle = $"Now: {label}";
        }
        else
        {
            subtitle = "Best edge per latency";
        }
        RowsHost.Children.Add(BuildAutoRow(_vm.IsSelectedExitAuto, subtitle));

        for (int i = 0; i < _vm.AvailableExits.Count; i++)
        {
            RowsHost.Children.Add(BuildDivider());
            var id = _vm.AvailableExits[i];
            var display = _vm.ExitDisplayNames.TryGetValue(id, out var n) ? n : id.ToUpperInvariant();
            RowsHost.Children.Add(BuildExitRow(id, display, selected: id == _vm.SelectedExit));
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
        btn.Tapped += async (_, _) => { await _vm.SetSelectedExitAsync("auto"); Hide(); };
        return btn;
    }

    private UIElement BuildExitRow(string id, string display, bool selected)
    {
        var idx = _vm.AvailableExits.IndexOf(id);
        var code = idx >= 0 ? $"E{Subscript(idx)} · {id.ToUpperInvariant()}" : id.ToUpperInvariant();

        var grid = MakeRowGrid();
        grid.Children.Add(WithColumn(new VxEdgeGlyph { GlyphSize = 22, VerticalAlignment = VerticalAlignment.Center }, 0));
        grid.Children.Add(WithColumn(MakeTextBlock(display, code, selected), 1));
        if (selected) grid.Children.Add(WithColumn(MakeCheckmark(), 2));
        var btn = WrapInButton(grid);
        btn.Tapped += async (_, _) => { await _vm.SetSelectedExitAsync(id); Hide(); };
        return btn;
    }

    /// <summary>Render decimal index as Unicode subscript digits (₀₁₂₃…). Mirror of macOS NodeLabels.edgeLabel chip.</summary>
    internal static string Subscript(int n)
    {
        if (n < 0) return n.ToString();
        var s = n.ToString();
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch >= '0' && ch <= '9') sb.Append((char)('₀' + (ch - '0')));
            else sb.Append(ch);
        }
        return sb.ToString();
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
        Glyph = "",
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
}
