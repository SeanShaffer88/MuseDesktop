using System.Threading;
using System.Windows;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// App entry: single-instance enforcement, settings load, diagnostics,
/// then the main window. A second launch never opens a second window —
/// it signals the running instance to come to the front instead.
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\MuseDesktop.SingleInstance";
    private const string ShowEventName = @"Local\MuseDesktop.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private MainWindow? _window;

    public AppSettings Settings { get; private set; } = new();
    public DiagnosticLog Log { get; private set; } = new(AppPaths.LogPath);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var startInBackground = e.Args.Any(a =>
            a.Equals("--background", StringComparison.OrdinalIgnoreCase));

        // Smoke-test mode (used by CI): construct the real main window —
        // which runs its XAML — then exit. v0.1.0 compiled and passed every
        // test yet crashed on first launch because nothing ever built the
        // window; this mode makes CI prove the window constructs.
        if (e.Args.Any(a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            RunSmokeTest();
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // Wake the running instance and exit quietly.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch { /* the running instance will simply keep its window */ }
            Shutdown();
            return;
        }

        Settings = SettingsStore.Load(AppPaths.SettingsPath);
        Log.Info($"MuseDesktop {typeof(App).Assembly.GetName().Version} starting" +
                 (startInBackground ? " (background)" : ""));

        DispatcherUnhandledException += (_, args) =>
        {
            // Full exception (type, inner exceptions, stack) — v0.1.0 logged
            // only Message and the inner cause of its startup crash was lost.
            Log.Error($"Unhandled: {args.Exception}");
            args.Handled = true;
            System.Windows.MessageBox.Show(
                "MuseDesktop hit an unexpected error but kept your session safe.\n\n" +
                $"Details were written to the log:\n{AppPaths.LogPath}",
                "MuseDesktop", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(WaitForShowSignal) { IsBackground = true, Name = "ShowSignal" };
        listener.Start();

        Autostart.Sync(Settings.StartWithWindows);

        try
        {
            _window = new MainWindow(Settings, Log);
        }
        catch (Exception ex)
        {
            Log.Error($"Main window creation failed: {ex}");
            System.Windows.MessageBox.Show(
                "MuseDesktop could not create its main window.\n\n" +
                $"The full error was written to the log:\n{AppPaths.LogPath}",
                "MuseDesktop", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        MainWindow = _window;
        if (startInBackground)
            _window.StartHidden();
        else
            _window.Show();
    }

    private async void RunSmokeTest()
    {
        Settings = SettingsStore.Load(AppPaths.SettingsPath);
        MainWindow window;
        try
        {
            window = new MainWindow(Settings, Log);
        }
        catch (Exception ex)
        {
            Log.Error($"SMOKE-TEST FAILED (window construction): {ex}");
            Shutdown(1);
            return;
        }

        Log.Info("SMOKE-TEST: main window constructed");
        try
        {
            window.Show();
            var ready = await window.WaitForWebViewReadyAsync(TimeSpan.FromSeconds(45));
            if (ready)
            {
                Log.Info("SMOKE-TEST OK: window constructed and WebView2 initialized");
                window.Close();
                Shutdown(0);
            }
            else
            {
                Log.Error("SMOKE-TEST FAILED: WebView2 did not initialize within 45s");
                Shutdown(1);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"SMOKE-TEST FAILED: {ex}");
            Shutdown(1);
        }
    }

    private void WaitForShowSignal()
    {
        while (_showEvent is not null)
        {
            try
            {
                _showEvent.WaitOne();
                Dispatcher.Invoke(() => _window?.ShowFromTray());
            }
            catch (ObjectDisposedException) { return; }
            catch (ThreadInterruptedException) { return; }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Info("MuseDesktop exiting");
            _showEvent?.Dispose();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { /* shutdown must not hang on cleanup */ }
        base.OnExit(e);
    }
}
