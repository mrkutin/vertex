using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vertex.App.ViewModels;
using Vertex.Core.Config;
using Vertex.Shared.Ipc;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using static Vertex.App.WindowSizing;

namespace Vertex.App.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly TunnelViewModel _vm;
    private NotifyCollectionChangedEventHandler? _brokersChanged;
    private NotifyCollectionChangedEventHandler? _exitsChanged;

    public SettingsWindow(TunnelViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Title = "Vertex Settings";

        // Fixed-size window like the macOS Settings sheet — same width as
        // the main window so the two read as siblings, slightly taller
        // to accommodate the Pivot tab strip.
        SetFixedSize(this, widthDip: 520, heightDip: 640);

        var asmVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "—";
        VersionLine.Text = $"v{asmVersion}";
#if DEBUG
        ConfigLine.Text = "Debug";
        ConfigLine.Foreground = (Brush)Application.Current.Resources["StateTransitioningBrush"];
#else
        ConfigLine.Text = "Release";
        ConfigLine.Foreground = (Brush)Application.Current.Resources["StateConnectedBrush"];
#endif

        // Routing tab — best-effort load of the bundled / refreshed RU
        // CIDR snapshot. The Service is the canonical owner; we read
        // the file directly so the UI shows live counts without an extra
        // IPC round-trip. RuNetsRefresh as a Phase 4 polish — for now
        // the count surfaces on each settings open.
        try
        {
            var loader = new Vertex.Core.Net.RuNetsLoader();
            var info = loader.LoadInfo();
            RuNetsCountLine.Text = info.LineCount > 0
                ? info.LineCount.ToString("N0")
                : "—";
            RuNetsSourceLine.Text = info.Source switch
            {
                Vertex.Core.Net.RuNetsLoader.Source.Updated => $"Updated {info.UpdatedAtUtc:yyyy-MM-dd}",
                _ => "Bundled snapshot",
            };
        }
        catch
        {
            RuNetsCountLine.Text = "—";
            RuNetsSourceLine.Text = "Bundled snapshot";
        }

        // Discovery tab — wire live broker / exit lists.
        _brokersChanged = (_, _) => RenderVertices();
        _exitsChanged   = (_, _) => RenderEdges();
        _vm.AvailableBrokers.CollectionChanged += _brokersChanged;
        _vm.AvailableExits.CollectionChanged   += _exitsChanged;
        _vm.PropertyChanged += OnVmChanged;
        RenderVertices();
        RenderEdges();

        // Identity tab — seed editable fields + fingerprint from the VM's
        // last IdentityInfo snapshot, then ask the Service to re-push so a
        // freshly opened Settings window has fresh data even after the App
        // has been running for a while (no IPC reconnect since Service
        // restart, etc.).
        ClientNameInput.Text = _vm.ClientName;
        DomainInput.Text     = _vm.DiscoveryDomain;
        ApplyFingerprint(_vm.IdentityPubkeyHex);
        _ = Task.Run(async () =>
        {
            try { await _vm.RequestIdentityInfoAsync(); }
            catch (Exception) { /* IPC down — re-attach event will retrigger */ }
        });

        Closed += (_, _) =>
        {
            if (_brokersChanged is not null)
                _vm.AvailableBrokers.CollectionChanged -= _brokersChanged;
            if (_exitsChanged is not null)
                _vm.AvailableExits.CollectionChanged   -= _exitsChanged;
            _vm.PropertyChanged -= OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ExitDisplayNames is a plain Dictionary, not observable — but
        // when DiscoveryUpdate populates it, AvailableExits also fires
        // CollectionChanged, so we re-render from there.
        if (e.PropertyName == nameof(TunnelViewModel.SelectedBroker))
            RenderVertices();
        else if (e.PropertyName == nameof(TunnelViewModel.SelectedExit))
            RenderEdges();
        else if (e.PropertyName == nameof(TunnelViewModel.ClientName))
        {
            // Only sync the TextBox when the field is unfocused — the user
            // may be mid-edit and the IdentityInfo echo from a stale Service
            // would otherwise stomp keystrokes. After Save the field loses
            // focus implicitly via ConfigureAwait(false), so the next round
            // does sync.
            if (!ClientNameInput.FocusState.HasFlag(FocusState.Keyboard) &&
                !ClientNameInput.FocusState.HasFlag(FocusState.Pointer))
            {
                ClientNameInput.Text = _vm.ClientName;
            }
        }
        else if (e.PropertyName == nameof(TunnelViewModel.DiscoveryDomain))
        {
            if (!DomainInput.FocusState.HasFlag(FocusState.Keyboard) &&
                !DomainInput.FocusState.HasFlag(FocusState.Pointer))
            {
                DomainInput.Text = _vm.DiscoveryDomain;
            }
        }
        else if (e.PropertyName == nameof(TunnelViewModel.IdentityPubkeyHex))
        {
            ApplyFingerprint(_vm.IdentityPubkeyHex);
        }
    }

    /// <summary>
    /// Render the fingerprint = first 16 hex chars formatted as 4 groups of 4
    /// separated by spaces ("abcd 1234 ef56 7890"). Empty hex → em-dash.
    /// Mirror of macOS <c>SettingsScreen.fingerprint</c>.
    /// </summary>
    private void ApplyFingerprint(string pubkeyHex)
    {
        FullPubkeyText.Text = pubkeyHex;
        if (string.IsNullOrEmpty(pubkeyHex))
        {
            // Identity not yet generated — collapse Reveal/Copy so the
            // user can't expand an empty box. Service generates the key
            // on first Connect or first PushIdentityInfo.
            FingerprintText.Text = "—";
            ToggleRevealButton.Visibility = Visibility.Collapsed;
            CopyFingerprintButton.Visibility = Visibility.Collapsed;
            FullPubkeyBox.Visibility = Visibility.Collapsed;
            ToggleRevealButton.Content = "Reveal";
            return;
        }
        ToggleRevealButton.Visibility = Visibility.Visible;
        CopyFingerprintButton.Visibility = Visibility.Visible;
        var prefix = pubkeyHex.Length >= 16 ? pubkeyHex[..16] : pubkeyHex;
        var groups = new List<string>(4);
        for (int i = 0; i < prefix.Length; i += 4)
        {
            groups.Add(prefix.Substring(i, Math.Min(4, prefix.Length - i)));
        }
        FingerprintText.Text = string.Join(' ', groups);
    }

    private async void OnSaveClientNameClick(object sender, RoutedEventArgs e)
    {
        try { await SaveClientNameAsync(); }
        catch (Exception) { /* IPC disposed mid-save — surface via banner on next attach */ }
    }

    private async void OnClientNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        try { await SaveClientNameAsync(); }
        catch (Exception) { }
    }

    private async Task SaveClientNameAsync()
    {
        var name = (ClientNameInput.Text ?? string.Empty).Trim();
        if (name.Length == 0) return;
        await _vm.SetClientNameAsync(name);
        // Sync the TextBox to whatever the Service ended up persisting —
        // sanitiser may have lowercased or rejected; if rejected, an error
        // banner already appeared, but the input must echo the canonical
        // value rather than the rejected raw text. (Reviewer Major-2.)
        ClientNameInput.Text = _vm.ClientName;
    }

    private async void OnDomainKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        try { await SaveDomainAsync(); }
        catch (Exception) { }
    }

    private async void OnDomainLostFocus(object sender, RoutedEventArgs e)
    {
        // Persist on focus-out so the user doesn't have to hit Enter or
        // Refresh — the macOS TextField commits on blur. No-op when value
        // hasn't changed (Service-side sanitiser idempotent).
        try { await SaveDomainAsync(); }
        catch (Exception) { }
    }

    private async Task SaveDomainAsync()
    {
        var domain = (DomainInput.Text ?? string.Empty).Trim();
        if (domain.Length == 0)
        {
            // User cleared the box — restore the last known good value
            // rather than push an empty domain that the Service would
            // reject anyway.
            DomainInput.Text = _vm.DiscoveryDomain;
            return;
        }
        if (string.Equals(domain, _vm.DiscoveryDomain, StringComparison.OrdinalIgnoreCase))
            return;
        await _vm.SetDiscoveryDomainAsync(domain);
        DomainInput.Text = _vm.DiscoveryDomain;
    }

    private void OnToggleRevealFingerprint(object sender, RoutedEventArgs e)
    {
        bool revealed = FullPubkeyBox.Visibility == Visibility.Visible;
        FullPubkeyBox.Visibility = revealed ? Visibility.Collapsed : Visibility.Visible;
        ToggleRevealButton.Content = revealed ? "Reveal" : "Hide";
    }

    private void OnCopyFingerprintClick(object sender, RoutedEventArgs e)
    {
        var hex = _vm.IdentityPubkeyHex;
        if (string.IsNullOrEmpty(hex)) return;
        var pkg = new DataPackage();
        pkg.SetText(hex);
        try
        {
            Clipboard.SetContent(pkg);
            // Flush so the copy survives the App being closed mid-paste.
            Clipboard.Flush();
        }
        catch (Exception)
        {
            // Another process briefly held the clipboard — silent retry on
            // the user's next click is preferable to a ContentDialog popup.
        }
    }

    private async void OnSavePasswordClick(object sender, RoutedEventArgs e)
    {
        // Trim whitespace + zero-width chars: a paste from the
        // CREDENTIALS.md table cell on macOS routinely picks up a
        // trailing space or LF that goes through DPAPI verbatim and
        // makes the broker reject the otherwise-correct password
        // ("Not authorized" CONNACK 0x87). Mosquitto's password_file
        // hash is over the raw bytes so even one trailing space breaks
        // auth. Trim defensively here and let the user paste freely.
        var pwd = (PasswordInput.Password ?? string.Empty).Trim();
        if (pwd.Length == 0) return;
        await _vm.SetPasswordAsync(pwd);
        PasswordInput.Password = string.Empty;
    }

    private async void OnResetIdentityClick(object sender, RoutedEventArgs e)
    {
        if (App.Ipc is { } ipc)
        {
            await ipc.SendAsync(new AppMessage.ResetIdentity());
        }
    }

    private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var diag = new DiagnosticsWindow(_vm);
        diag.Activate();
    }

    private async void OnSplitToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts)
        {
            await _vm.SetSplitTunnelAsync(ts.IsOn);
        }
    }

    private async void OnRefreshDiscoveryClick(object sender, RoutedEventArgs e)
    {
        // Commit any pending domain edit first so Refresh re-resolves the
        // value the user typed (the LostFocus handler races the Refresh
        // click on most input sequences but we want a deterministic
        // edit→refresh order).
        try
        {
            await SaveDomainAsync();
            await _vm.RefreshDiscoveryAsync();
        }
        catch (Exception) { }
    }

    private void RenderVertices()
    {
        VerticesHost.Children.Clear();
        if (_vm.AvailableBrokers.Count == 0)
        {
            VerticesHost.Children.Add(MakePlaceholder("Resolving discovery…"));
            return;
        }
        for (int i = 0; i < _vm.AvailableBrokers.Count; i++)
        {
            if (i > 0) VerticesHost.Children.Add(MakeDivider());
            VerticesHost.Children.Add(MakeVertexRow(_vm.AvailableBrokers[i], i));
        }
    }

    private void RenderEdges()
    {
        EdgesHost.Children.Clear();
        if (_vm.AvailableExits.Count == 0)
        {
            EdgesHost.Children.Add(MakePlaceholder("Resolving discovery…"));
            return;
        }
        for (int i = 0; i < _vm.AvailableExits.Count; i++)
        {
            if (i > 0) EdgesHost.Children.Add(MakeDivider());
            EdgesHost.Children.Add(MakeEdgeRow(_vm.AvailableExits[i], i));
        }
    }

    private TextBlock MakePlaceholder(string text) => new()
    {
        Text = text,
        Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
        FontSize = 12,
        Padding = new Thickness(16, 12, 16, 12),
    };

    private Border MakeDivider() => new()
    {
        Background = (Brush)Application.Current.Resources["BorderSubtleBrush"],
        Height = 0.5,
        Margin = new Thickness(16, 0, 0, 0),
    };

    private Grid MakeVertexRow(string url, int index)
    {
        var grid = new Grid { Padding = new Thickness(16, 12, 16, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hostText = BrokerUrl.TryParse(url, out var parsed) ? parsed.Host : url;
        var schemeText = BrokerUrl.TryParse(url, out var p2)
            ? $"{p2.Scheme.ToUpperInvariant()} · {p2.Port}"
            : url;

        var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = hostText,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            FontSize = 14,
        });
        stack.Children.Add(new TextBlock
        {
            Text = schemeText,
            Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
        });
        Grid.SetColumn(stack, 0);
        grid.Children.Add(stack);

        // Vₙ subscript chip — paritет с macOS NodeLabels.vertexLabel
        var chip = new TextBlock
        {
            Text = $"V{Subscript(index)}",
            Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(chip, 1);
        grid.Children.Add(chip);

        if (url == _vm.SelectedBroker)
        {
            var check = new FontIcon
            {
                Glyph = "",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["AccentPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }

        return grid;
    }

    private Grid MakeEdgeRow(string id, int index)
    {
        var grid = new Grid { Padding = new Thickness(16, 12, 16, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var display = _vm.ExitDisplayNames.TryGetValue(id, out var n) ? n : id.ToUpperInvariant();

        var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = display,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            FontSize = 14,
        });
        stack.Children.Add(new TextBlock
        {
            Text = id.ToUpperInvariant(),
            Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
        });
        Grid.SetColumn(stack, 0);
        grid.Children.Add(stack);

        var chip = new TextBlock
        {
            Text = $"E{Subscript(index)}",
            Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(chip, 1);
        grid.Children.Add(chip);

        if (id == _vm.SelectedExit)
        {
            var check = new FontIcon
            {
                Glyph = "",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["AccentPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }

        return grid;
    }

    /// <summary>Render decimal index as Unicode subscript digits (₀₁₂₃…). Mirror of macOS NodeLabels output.</summary>
    private static string Subscript(int n)
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
}
