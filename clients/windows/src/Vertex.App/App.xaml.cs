using System.Threading;
using Microsoft.UI.Xaml;
using Vertex.App.Services;
using Vertex.App.ViewModels;

namespace Vertex.App;

public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// Process-wide IPC client. Created lazily on app launch and
    /// exposed via <see cref="ViewModel"/>.
    /// </summary>
    public static IpcClient? Ipc { get; private set; }

    /// <summary>Single shared <see cref="TunnelViewModel"/> bound to the main window.</summary>
    public static TunnelViewModel? ViewModel { get; private set; }

    /// <summary>
    /// Held for the lifetime of the process — second-launch detection.
    /// Mirror of macOS NSApplicationDelegate single-instance check; on
    /// Windows we rely on a session-scoped named Mutex. Real foreground
    /// activation handoff (AppInstance.RedirectActivationToAsync) is
    /// deferred until the app is packaged as MSIX in Phase 5.
    /// </summary>
    private static Mutex? _instanceLock;

    public App()
    {
        // Earliest possible diagnostic — fires before InitializeComponent
        // so a XAML-load crash still leaves a breadcrumb on disk.
        Trace("ctor: entered");
        try { InitializeComponent(); } catch (System.Exception ex) { Trace($"InitializeComponent FAILED: {ex}"); throw; }
        Trace("ctor: InitializeComponent ok");
        // Crash dump to a stable file so a Parallels-VM smoke test can
        // surface managed exceptions without a debugger attached.
        UnhandledException += (_, ex) =>
        {
            try
            {
                var path = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "Vertex", "app-crash.log");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                System.IO.File.AppendAllText(path,
                    $"=== {System.DateTime.UtcNow:o} ===\n{ex.Exception}\n\n");
            }
            catch { /* swallow — this is a last-resort diagnostic */ }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var path = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "Vertex", "app-crash.log");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                System.IO.File.AppendAllText(path,
                    $"=== {System.DateTime.UtcNow:o} (AppDomain) ===\n{e.ExceptionObject}\n\n");
            }
            catch { }
        };
    }

    private static void Trace(string msg)
    {
        try
        {
            var path = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "Vertex", "app-trace.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.AppendAllText(path, $"[{System.DateTime.UtcNow:o}] {msg}\n");
        }
        catch { /* swallow */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Trace("OnLaunched: entered");
        // Bail if another instance is already running. Without packaged
        // identity we can't activate the existing window from here, but
        // at least we avoid two named-pipe clients fighting over the
        // Service. The mutex is per-user-session so two users on one
        // machine each get one app instance.
        const string mutexName = "Local\\ru.vertices.app";
        _instanceLock = new Mutex(initiallyOwned: true, name: mutexName, out bool createdNew);
        if (!createdNew)
        {
            // Already running — exit silently. A future Phase 5 polish
            // will use Microsoft.Windows.AppLifecycle.AppInstance to
            // foreground the existing window via redirected activation.
            Exit();
            return;
        }

        try
        {
            Trace("OnLaunched: building IpcClient");
            Ipc = new IpcClient();
            Ipc.Start();
            Trace("OnLaunched: IpcClient started");
            ViewModel = new TunnelViewModel(Ipc);
            Trace("OnLaunched: ViewModel built");

            _window = new MainWindow(ViewModel);
            Trace("OnLaunched: MainWindow built");
            _window.Activate();
            Trace("OnLaunched: MainWindow activated");
        }
        catch (System.Exception ex)
        {
            try
            {
                var path = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "Vertex", "app-crash.log");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                System.IO.File.AppendAllText(path,
                    $"=== {System.DateTime.UtcNow:o} (OnLaunched) ===\n{ex}\n\n");
            }
            catch { }
            throw;
        }
    }
}
