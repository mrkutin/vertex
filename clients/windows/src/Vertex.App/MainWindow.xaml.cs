using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Vertex.App.ViewModels;
using Vertex.App.Views;
using Windows.Graphics;
using WinRT.Interop;

namespace Vertex.App;

public sealed partial class MainWindow : Window
{
    private SettingsWindow? _settings;
    private readonly TunnelViewModel _vm;

    public MainWindow(TunnelViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Title = "Vertex";

        // Fixed-size, non-resizable window like the macOS app. WinUI's
        // AppWindow.Resize() takes physical pixels — must scale by DPI
        // or the window comes out tiny on hi-DPI displays.
        WindowSizing.SetFixedSize(this, widthDip: 460, heightDip: 760);

        var screen = new ConnectScreen(vm);
        screen.SettingsRequested += OnSettingsRequested;
        Root.Children.Add(screen);

        Closed += async (_, _) =>
        {
            try { _settings?.Close(); } catch { }
            try { await vm.DisposeAsync(); } catch { }
        };
    }

    private void OnSettingsRequested(object? sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(_vm);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Activate();
        }
        else
        {
            _settings.Activate();
        }
    }
}

internal static class WindowSizing
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(System.IntPtr hwnd);

    public static void SetFixedSize(Window w, int widthDip, int heightDip)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(w);
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            int wPx = (int)(widthDip  * dpi / 96.0);
            int hPx = (int)(heightDip * dpi / 96.0);

            var id = Win32Interop.GetWindowIdFromWindow(hwnd);
            var aw = AppWindow.GetFromWindowId(id);
            aw.Resize(new SizeInt32(wPx, hPx));
            if (aw.Presenter is OverlappedPresenter op)
            {
                op.IsResizable = false;
                op.IsMaximizable = false;
                op.IsMinimizable = true;
            }
        }
        catch { /* swallow on platforms missing AppWindow APIs */ }
    }
}
