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
            Log.Error($"Unhandled: {args.Exception.GetType().Name}: {args.Exception.Message}");
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

        _window = new MainWindow(Settings, Log);
        MainWindow = _window;
        if (startInBackground)
            _window.StartHidden();
        else
            _window.Show();
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
