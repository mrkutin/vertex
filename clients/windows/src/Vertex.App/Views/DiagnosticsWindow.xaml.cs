using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vertex.App.ViewModels;
using Vertex.Core.Config;
using Vertex.Shared;
using Vertex.Shared.Ipc;

namespace Vertex.App.Views;

/// <summary>
/// Read-only "Connection" page with the assigned IP, broker / exit
/// hostnames, connected timestamp, and cumulative byte / packet counters.
/// Mirror of macOS <c>StatsSheet.swift</c>; opens from the Settings
/// gear → Diagnostics on the main screen.
/// </summary>
public sealed partial class DiagnosticsWindow : Window
{
    private readonly TunnelViewModel _vm;
    private DispatcherTimer? _refreshTimer;

    public DiagnosticsWindow(TunnelViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Title = "Vertex Connection";

        _vm.PropertyChanged += OnVmChanged;
        Render();
        // Stats arrive every 1s on the IPC stream and TunnelViewModel
        // re-raises BytesUp / BytesDown PropertyChanged each time, but
        // the connected time text isn't a property — it derives from
        // ConnectedSinceEpochMs + Now, so tick a 1s timer to keep it
        // current without coupling to stats arrival.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => Render();
        _refreshTimer.Start();

        Closed += (_, _) =>
        {
            _vm.PropertyChanged -= OnVmChanged;
            _refreshTimer?.Stop();
            _refreshTimer = null;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        Hero.State              = _vm.State;
        Hero.UploadBytesPerSec  = _vm.UploadBytesPerSec;
        Hero.DownloadBytesPerSec = _vm.DownloadBytesPerSec;
        StatusLine.Text         = _vm.StatusLabel;

        VertexRowsHost.Children.Clear();
        if (!string.IsNullOrEmpty(_vm.AssignedIp))
            VertexRowsHost.Children.Add(MakeRow("Assigned IP", _vm.AssignedIp!, mono: true));
        if (!string.IsNullOrEmpty(_vm.CurrentBroker))
            VertexRowsHost.Children.Add(MakeRow("Vertex", _vm.CurrentBroker!));
        if (!string.IsNullOrEmpty(_vm.CurrentExit))
            VertexRowsHost.Children.Add(MakeRow("Edge", _vm.CurrentExit!.ToUpperInvariant()));
        var connected = ConnectedDuration();
        if (connected is not null)
            VertexRowsHost.Children.Add(MakeRow("Connected", connected, mono: true));
        if (VertexRowsHost.Children.Count == 0)
            VertexRowsHost.Children.Add(MakePlaceholder("Not connected"));

        TrafficRowsHost.Children.Clear();
        TrafficRowsHost.Children.Add(MakeRow("Sent",     FormatBinary(_vm.BytesUp),   mono: true));
        TrafficRowsHost.Children.Add(MakeRow("Received", FormatBinary(_vm.BytesDown), mono: true));
    }

    private string? ConnectedDuration()
    {
        if (_vm.State != ConnectionState.Connected || _vm.ConnectedSinceEpochMs is not long since) return null;
        var elapsed = TimeSpan.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - since);
        if (elapsed.TotalDays >= 1)
            return $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        return $"{elapsed.Hours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }

    private static UIElement MakeRow(string label, string value, bool mono = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var v = new TextBlock
        {
            Text = value,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            FontSize = 14,
            FontFamily = mono ? new FontFamily("Consolas") : null!,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);
        return grid;
    }

    private static UIElement MakePlaceholder(string text) => new TextBlock
    {
        Text = text,
        Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
        FontSize = 13,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>Bytes → KiB / MiB / GiB (binary prefixes, paritет с macOS ByteCountFormatter style: .binary).</summary>
    private static string FormatBinary(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var c = CultureInfo.InvariantCulture;
        if (bytes < 1024L * 1024)         return string.Format(c, "{0:F1} KiB", bytes / 1024.0);
        if (bytes < 1024L * 1024 * 1024)  return string.Format(c, "{0:F1} MiB", bytes / (1024.0 * 1024));
        return string.Format(c, "{0:F2} GiB", bytes / (1024.0 * 1024 * 1024));
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        // FileSavePicker requires the window's HWND to be passed in the
        // initialization Pickers API on WinUI 3 (no implicit owner).
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedFileName = $"vertex-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}";
        picker.FileTypeChoices.Add("ZIP archive", new[] { ".zip" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await _vm.ExportDiagnosticsAsync(file.Path);
    }

    private void OnDoneClick(object sender, RoutedEventArgs e) => Close();
}
