using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
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

        // The command-line surface (S3 #23, S4 #33): --background,
        // --smoke-test, --profile <id>, --settings; anything unknown is
        // tolerated, never fatal.
        var options = CommandLine.Parse(e.Args);

        // Smoke-test mode (used by CI): construct the real main window —
        // which runs its XAML — then exit. v0.1.0 compiled and passed every
        // test yet crashed on first launch because nothing ever built the
        // window; this mode makes CI prove the window constructs.
        if (options.SmokeTest)
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

        var settingsFileExisted = File.Exists(AppPaths.SettingsPath);
        Settings = SettingsStore.Load(AppPaths.SettingsPath);
        // Upgrade vs first run (S5 #50): a settings file written by a
        // pre-v0.9.0 build (schema < 2, no firstRunCompleted in it)
        // belongs to an existing user — that is an upgrade, not a
        // first run, and the wizard must not nag them. A file with
        // schema 2 and the flag false is a genuine first run (or a
        // post-reset state, which deliberately re-offers the wizard).
        if (settingsFileExisted && Settings.SchemaVersion < 2 && !Settings.FirstRunCompleted)
            Settings.FirstRunCompleted = true;
        Settings.SchemaVersion = Math.Max(Settings.SchemaVersion, 2);
        // Shell theme (v0.9.0, S5 #43): apply before any window exists,
        // and keep following Windows while the app runs when the mode
        // says so.
        ThemeManager.Apply(Settings);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        if (AppPaths.IsPortable)
            Log.Info($"Portable mode (S5 #49): settings live in {AppPaths.RootDir}");
        // First run (S5 #50): the setup wizard shows once — a first run
        // never starts hidden, the wizard needs a visible owner.
        var firstRun = !Settings.FirstRunCompleted;
        // Start-in-tray (S4 #40) is its own toggle, separate from
        // Start-with-Windows: it decides only what a background
        // (autostart-style) launch does. --settings wins over a hidden
        // start — a Settings window needs a visible owner.
        var startHidden = !options.OpenSettings && !firstRun
                          && LaunchVisibility.ShouldStartHidden(options.Background, Settings.StartMinimizedToTray);
        Log.Info($"MuseDesktop {typeof(App).Assembly.GetName().Version} starting" +
                 (options.Background ? (startHidden ? " (background, in tray)" : " (background)") : ""));

        // --profile <id> (S3 #23): a per-profile shortcut lands in that
        // profile's session. Like a Settings switch, the choice is
        // persisted as the active profile (that's what the existing
        // switch flow does — the last-used profile is what plain launches
        // reopen). An unknown id falls back to the saved active profile.
        if (options.ProfileId is not null)
        {
            var (launchId, fellBack) = Profiles.ResolveLaunchProfile(Settings, options.ProfileId);
            if (fellBack)
                Log.Info($"--profile '{options.ProfileId}' names no profile; using '{launchId}'");
            Settings.ActiveProfileId = launchId;
        }

        // Stamp the launched profile as last used (S3 #27). Persisted by
        // the main window's settings saves; harmless if lost in a crash.
        var activeProfile = Settings.Profiles.FirstOrDefault(p => p.Id == Settings.ActiveProfileId);
        if (activeProfile is not null)
            activeProfile.LastUsedUtc = DateTimeOffset.UtcNow;

        RunPendingProfileFolderDeletions();

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
        if (startHidden)
            _window.StartHidden();
        else
            _window.Show();
        if (options.OpenSettings)
        {
            // Jump List Settings task (S4 #33): the window is up; open
            // Settings once the dispatcher settles.
            var window = _window;
            Dispatcher.BeginInvoke(new Action(() => window.OpenSettingsWindow()));
        }
        if (firstRun)
        {
            var window = _window;
            Dispatcher.BeginInvoke(new Action(() => RunFirstRunWizard(window)));
        }
    }

    /// <summary>Follow-Windows theme mode (S5 #43): when the OS theme flips, re-resolve the shell palette.</summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Settings.Theme != MuseDesktop.Core.ThemeMode.FollowSystem)
            return;
        Dispatcher.BeginInvoke(new Action(() => ThemeManager.Apply(Settings)));
    }

    /// <summary>
    /// The first-run setup wizard (S5 #50), shown once per settings
    /// file. It edits a clone; Save adopts the four choices, and ANY
    /// exit (Save, Skip, or the X) records completion so the wizard
    /// never nags twice. Adoption writes the live settings object the
    /// main window already holds, then persists.
    /// </summary>
    private void RunFirstRunWizard(MainWindow owner)
    {
        var target = Settings.Clone();
        var wizard = new SetupWizardWindow(target, markCompletedOnSkip: false) { Owner = owner };
        var saved = wizard.ShowDialog() == true;
        if (saved)
        {
            Settings.CloseToTray = target.CloseToTray;
            Settings.StartWithWindows = target.StartWithWindows;
            Settings.DownloadsFolder = target.DownloadsFolder;
            Settings.CheckForUpdates = target.CheckForUpdates;
        }
        Settings.FirstRunCompleted = true;
        Autostart.Sync(Settings.StartWithWindows);
        try { SettingsStore.Save(AppPaths.SettingsPath, Settings); }
        catch (Exception ex) { Log.Warn($"Saving settings after the first-run wizard failed: {ex.Message}"); }
        Log.Info(saved ? "First-run setup wizard completed" : "First-run setup wizard skipped");
    }

    /// <summary>
    /// Deferred profile-folder deletion (S3 #28): profiles removed while
    /// their folder couldn't be deleted (it belonged to the then-running
    /// profile, or the delete failed) are retried here — after the single-
    /// instance check, before the main window creates any WebView2
    /// environment, and never for the profile this launch is using.
    /// Successes leave the list; failures stay queued for next launch.
    /// </summary>
    private void RunPendingProfileFolderDeletions()
    {
        if (Settings.PendingProfileFolderDeletions.Count == 0)
            return;
        var changed = false;
        foreach (var id in Settings.PendingProfileFolderDeletions.ToList())
        {
            if (id == Settings.ActiveProfileId)
                continue; // never delete the folder this launch is about to use
            try
            {
                var dir = AppPaths.ProfileDir(id);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                Settings.PendingProfileFolderDeletions.Remove(id);
                changed = true;
                Log.Info($"Deleted data folder of previously removed profile {id}");
            }
            catch (Exception ex)
            {
                Log.Warn($"Deferred deletion of profile {id} folder failed (will retry next launch): {ex.Message}");
            }
        }
        if (changed)
        {
            try { SettingsStore.Save(AppPaths.SettingsPath, Settings); }
            catch (Exception ex) { Log.Warn($"Saving settings after deferred deletions failed: {ex.Message}"); }
        }
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
