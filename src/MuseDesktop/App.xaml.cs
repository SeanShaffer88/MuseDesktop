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

        // The command-line surface (S3 #23, S4 #33; completed by
        // S11 #100): --background, --smoke-test, --profile <id>,
        // --settings, --home, --downloads, --dictate, --safe-mode and
        // musedesktop:// links; anything unknown is tolerated, never
        // fatal.
        var options = CommandLine.Parse(e.Args);

        // R7 #161: even the pre-instance answers below speak the
        // user's language — a read-only settings peek purely to pick
        // the catalog language (the real load happens further down,
        // unchanged; Load's corrupt-file preservation is timestamped
        // and best-effort, so the peek is safe).
        try { Strings.Initialize(SettingsStore.Load(AppPaths.SettingsPath)); }
        catch { /* English is the fallback — language never breaks startup */ }

        // S11 #100: --help / --version are information, not actions —
        // answered by THIS process, before single-instance, so asking
        // never wakes (or waits behind the lock of) a running instance.
        if (options.ShowVersion)
        {
            var version = typeof(App).Assembly.GetName().Version;
            System.Windows.MessageBox.Show(
                Strings.Format("app.version.body", version?.ToString(3) ?? "?"),
                "MuseDesktop", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        if (options.ShowHelp)
        {
            var version = typeof(App).Assembly.GetName().Version;
            System.Windows.MessageBox.Show(
                Strings.Format("app.usage.text", version?.ToString(3) ?? "?"),
                Strings.Get("app.help.caption"), MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // S8 #72/#73: safe mode is the --safe-mode flag, or Shift held
        // at launch (a rescue for a mouse-only recovery) — the flag is
        // the contract, Shift the convenience. The startup clock
        // starts here, before anything else can be measured.
        var startupClock = new StartupClock();
        var safeMode = options.SafeMode;
        try
        {
            if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
                safeMode = true;
        }
        catch { /* keyboard state unavailable — the flag carries it */ }

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
            // S11 #99/#100: hand the running instance this launch's
            // INTENT before waking it — the pending-intent file is the
            // message, the event below only the knock, and writing
            // first is the ordering that makes the hand-off reliable.
            // Without an actionable flag the intent is "show the
            // window", the behavior second launches have always had.
            try
            {
                PendingIntentStore.Save(AppPaths.PendingIntentPath, SecondLaunchIntent(options));
            }
            catch { /* the wake still applies — a plain show */ }
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
        // Shell language (R7 #161/#162/#166): resolve the catalog
        // language from the real settings (the peek above may have
        // run on defaults), and give every window the language's
        // reading direction as it loads — one class handler, so
        // right-to-left readiness covers windows added later too.
        Strings.Initialize(Settings);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window)
                    window.FlowDirection = Strings.Current.IsRightToLeft
                        ? System.Windows.FlowDirection.RightToLeft
                        : System.Windows.FlowDirection.LeftToRight;
            }));
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
                 (options.Background ? (startHidden ? " (background, in tray)" : " (background)") : "") +
                 (safeMode ? " [SAFE MODE]" : ""));

        // musedesktop:// link (S11 #99): parsed against this launch's
        // real profile list, before the lock gate — a link gets no
        // shortcut past the lock. A profile route joins the --profile
        // flow below (persist-as-active, like a per-profile shortcut);
        // the other routes apply as an intent once the window is up. A
        // refusal is logged (scheme/host/path only) and the launch
        // proceeds as a plain one — a bad link never stops the app
        // from opening.
        LaunchIntent? protocolIntent = null;
        string? protocolProfileId = null;
        if (options.ProtocolUri is not null)
        {
            var parsed = LaunchIntent.ParseProtocolUri(
                options.ProtocolUri, Settings.Profiles.Select(p => p.Id).ToArray());
            if (parsed.Intent is not null)
            {
                protocolIntent = parsed.Intent;
                if (parsed.Note is not null)
                    Log.Info($"musedesktop:// link: {parsed.Note}");
                if (parsed.Intent.Kind == LaunchIntentKind.SwitchProfile)
                    protocolProfileId = parsed.Intent.ProfileId;
            }
            else
            {
                Log.Warn($"musedesktop:// link refused: {parsed.Refusal} " +
                         $"({LaunchIntent.DescribeForLog(options.ProtocolUri)})");
            }
        }
        // Protocol registration (S11 #99): the installer registered
        // the scheme at install time; this keeps the registration in
        // line with the Settings toggle (and is the only registration
        // a portable copy ever gets).
        ProtocolRegistration.Sync(Settings, Log);

        // App lock (S7 #64). A broken lock record was already repaired
        // by Sanitized on load (fail-open, never a lockout) — say so.
        if (Settings.AppLockWasRepaired)
            Log.Warn("App lock was turned off: its stored password record was missing or damaged. " +
                     "Set a new app password in Settings → Privacy & security to turn it back on.");
        // The startup gate: for a launch that will SHOW a window, the
        // lock screen runs before the main window exists — WebView2 is
        // never created, so no page can render or navigate before
        // unlock, and cancelling the gate exits the app (no locked
        // zombie). A hidden tray launch skips the modal — showing a
        // password prompt for an autostart launch would be a rude
        // surprise — and the main window starts locked instead: it
        // defers WebView2 entirely and its lock screen appears when
        // the window is first requested from the tray.
        var mainWindowStartsLocked = false;
        if (AppLock.IsLockActive(Settings) && !startHidden)
        {
            if (!AppLock.TryGetRecord(Settings, out var gateRecord) || gateRecord is null
                || !ShowStartupLockGate(gateRecord))
            {
                Log.Info("App lock: startup cancelled at the lock screen — exiting");
                Shutdown();
                return;
            }
            Log.Info("App lock: unlocked at startup");
        }
        else if (AppLock.IsLockActive(Settings))
        {
            mainWindowStartsLocked = true;
            Log.Info("App lock is on — starting locked in the tray (unlock on first show)");
        }

        // --profile <id> (S3 #23): a per-profile shortcut lands in that
        // profile's session. Like a Settings switch, the choice is
        // persisted as the active profile (that's what the existing
        // switch flow does — the last-used profile is what plain launches
        // reopen). An unknown id falls back to the saved active profile.
        // A musedesktop://profile/<id> link (S11 #99) arrives here too,
        // already validated against the profile list above.
        var launchProfileId = options.ProfileId ?? protocolProfileId;
        if (launchProfileId is not null)
        {
            var (launchId, fellBack) = Profiles.ResolveLaunchProfile(Settings, launchProfileId);
            if (fellBack)
                Log.Info($"Launch profile '{launchProfileId}' names no profile; using '{launchId}'");
            Settings.ActiveProfileId = launchId;
        }

        // Stamp the launched profile as last used (S3 #27). Persisted by
        // the main window's settings saves; harmless if lost in a crash.
        var activeProfile = Settings.Profiles.FirstOrDefault(p => p.Id == Settings.ActiveProfileId);
        if (activeProfile is not null)
            activeProfile.LastUsedUtc = DateTimeOffset.UtcNow;

        // R3 #129: count this launch in the local-only usage tallies.
        // Best-effort by design — a tally failure never blocks a launch.
        try
        {
            var usage = ProfileUsageStore.Load(AppPaths.ProfileUsagePath);
            usage.RecordLaunch(Settings.ActiveProfileId, DateTimeOffset.UtcNow);
            ProfileUsageStore.Save(AppPaths.ProfileUsagePath, usage);
        }
        catch (Exception ex) { Log.Warn($"Profile usage tally failed: {ex.Message}"); }

        // R5 #150: the same launch in the weekly stats (per-week
        // buckets for the dashboard + digest). Equally best-effort,
        // equally local.
        try
        {
            var weekly = WeeklyStatsStore.Load(AppPaths.WeeklyStatsPath);
            weekly.RecordLaunch(Settings.ActiveProfileId, DateTimeOffset.UtcNow);
            WeeklyStatsStore.Save(AppPaths.WeeklyStatsPath, weekly);
        }
        catch (Exception ex) { Log.Warn($"Weekly stats tally failed: {ex.Message}"); }

        // R3 #122: the LAUNCH profile's own PIN, gated after the
        // global app lock above (the global credential is always asked
        // first). A visible launch gets the profile gate now, as its
        // own modal; a hidden launch defers it to the main window's
        // first real show (the hidden-start contract: no window pops
        // at sign-in).
        AppLockRecord? pendingProfileLock = null;
        if (activeProfile is not null
            && ProfileLock.IsLockActive(activeProfile)
            && ProfileLock.TryGetRecord(activeProfile, out var profileRecord)
            && profileRecord is not null)
        {
            if (!startHidden)
            {
                if (!ShowStartupLockGate(profileRecord))
                {
                    Log.Info("Profile PIN: startup cancelled at the profile lock screen — exiting");
                    Shutdown();
                    return;
                }
                Log.Info("Profile PIN: unlocked at startup");
            }
            else
            {
                pendingProfileLock = profileRecord;
                Log.Info("Profile PIN is on for the launch profile — starting in the tray (PIN asked on first show)");
            }
        }

        RunPendingProfileFolderDeletions();
        // S8 #75: the startup cache-cap pass — capped profiles over
        // their cap get their allowlisted caches trimmed, and the
        // cleanup is stamped so Settings can show it happened (never
        // silent). Runs before any WebView2 environment exists.
        try
        {
            if (CacheCleanupRunner.RunAllProfiles(Settings, Log))
                SettingsStore.Save(AppPaths.SettingsPath, Settings);
        }
        catch (Exception ex) { Log.Warn($"Startup cache cleanup failed: {ex.Message}"); }
        // S8 #79: the once-a-day automatic backup also runs here, so
        // a launch-only session still leaves a fresh backup behind.
        BackupService.MaybeAutoBackup(Log);

        DispatcherUnhandledException += (_, args) =>
        {
            // Full exception (type, inner exceptions, stack) — v0.1.0 logged
            // only Message and the inner cause of its startup crash was lost.
            Log.Error($"Unhandled: {args.Exception}");
            args.Handled = true;
            System.Windows.MessageBox.Show(
                Strings.Format("app.unhandled_error", AppPaths.LogPath),
                "MuseDesktop", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(WaitForShowSignal) { IsBackground = true, Name = "ShowSignal" };
        listener.Start();

        Autostart.Sync(Settings.StartWithWindows);

        try
        {
            _window = new MainWindow(Settings, Log, mainWindowStartsLocked, safeMode, startupClock);
        }
        catch (Exception ex)
        {
            Log.Error($"Main window creation failed: {ex}");
            System.Windows.MessageBox.Show(
                Strings.Format("app.main_window_failed", AppPaths.LogPath),
                "MuseDesktop", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        MainWindow = _window;
        // R3 #122: hand the deferred profile PIN to the window (its
        // first real show presents it, after any global unlock).
        if (pendingProfileLock is not null)
            _window.RequireProfileUnlock(pendingProfileLock);
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
        // S11 #99/#100: the rest of this launch's intent applies now
        // the window is up, through the same ApplyLaunchIntent a
        // second launch uses — including its lock rule. One
        // exception: a launch that started HIDDEN in the tray only
        // queues its intent — popping the lock screen or a window at
        // sign-in is exactly what a background launch promises not
        // to do (S7); the intent applies at the first real show.
        // --settings ran above and --profile before the window
        // existed, so neither re-applies here.
        var coldIntent = protocolIntent ?? LaunchIntent.FromCommandLine(options);
        if (coldIntent.Kind is LaunchIntentKind.GoHome or LaunchIntentKind.OpenDownloads
                or LaunchIntentKind.QuickDictate or LaunchIntentKind.ShowHelp
            || (coldIntent.Kind == LaunchIntentKind.OpenSettings && !options.OpenSettings))
        {
            var window = _window;
            if (startHidden)
                window.QueueLaunchIntent(coldIntent);
            else
                Dispatcher.BeginInvoke(new Action(() => window.ApplyLaunchIntent(coldIntent)));
        }
        if (firstRun)
        {
            var window = _window;
            Dispatcher.BeginInvoke(new Action(() => RunFirstRunWizard(window)));
        }
        else if (!mainWindowStartsLocked)
        {
            // S9 #82: What's New shows once on the first launch of a
            // new version. A locked window skips it this launch (the
            // version stays unrecorded, so it shows after unlock on a
            // later launch) — release notes never precede the password.
            var window = _window;
            Dispatcher.BeginInvoke(new Action(() => MaybeShowWhatsNew(window)));
        }
    }

    /// <summary>
    /// What's New (S9 #82): Core decides from the last-seen version;
    /// this shows the running version's CHANGELOG section from the
    /// WhatsNew.md content file shipped beside the exe, then records
    /// the version as seen — including when the notes are missing, so
    /// a packaging slip can't nag every launch.
    /// </summary>
    private void MaybeShowWhatsNew(MainWindow owner)
    {
        try
        {
            var current = typeof(App).Assembly.GetName().Version?.ToString(3);
            if (current is null)
                return;
            var decision = WhatsNew.Decide(Settings.LastSeenVersion, current, Settings.ShowWhatsNew);
            if (decision == WhatsNewDecision.Skip)
                return;
            if (decision == WhatsNewDecision.Show)
            {
                var notesPath = Path.Combine(AppContext.BaseDirectory, "WhatsNew.md");
                var body = File.Exists(notesPath)
                    ? WhatsNew.ExtractSection(File.ReadAllText(notesPath), current)
                    : null;
                if (body is not null)
                {
                    var dialog = new WhatsNewWindow(current, body) { Owner = owner };
                    ShellTextScaling.Apply(dialog, Settings.ShellFontScale);
                    FocusReturn.ShowDialog(dialog);
                    if (dialog.NeverShowAgain)
                        Settings.ShowWhatsNew = false;
                    Log.Info($"What's New shown for v{current}");
                }
                else
                {
                    Log.Warn($"What's New: no section for v{current} in WhatsNew.md — nothing shown");
                }
            }
            Settings.LastSeenVersion = current;
            try { SettingsStore.Save(AppPaths.SettingsPath, Settings); }
            catch (Exception ex) { Log.Warn($"Saving the What's New seen-version failed: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Log.Warn($"What's New failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A second launch's intent (S11 #99/#100), computed in the
    /// launching process: a musedesktop:// link validates against the
    /// SAVED profile list (a read-only load — the file the running
    /// instance owns isn't touched); a refused link still wakes the
    /// window, because the user clicked something and silence plus a
    /// log line they can't see would be worse than the app appearing.
    /// --safe-mode on a running instance is noted and dropped: safe
    /// mode describes how a process starts.
    /// </summary>
    private LaunchIntent SecondLaunchIntent(CommandLineOptions options)
    {
        if (options.ProtocolUri is not null)
        {
            var saved = SettingsStore.Load(AppPaths.SettingsPath);
            var parsed = LaunchIntent.ParseProtocolUri(
                options.ProtocolUri, saved.Profiles.Select(p => p.Id).ToArray());
            if (parsed.Intent is not null)
            {
                if (parsed.Note is not null)
                    Log.Info($"musedesktop:// link: {parsed.Note}");
                return parsed.Intent;
            }
            Log.Warn($"musedesktop:// link refused: {parsed.Refusal} " +
                     $"({LaunchIntent.DescribeForLog(options.ProtocolUri)})");
            return LaunchIntent.ShowWindow;
        }
        if (options.SafeMode)
            Log.Info("--safe-mode ignored: MuseDesktop is already running (safe mode is launch-time only)");
        return LaunchIntent.FromCommandLine(options);
    }

    /// <summary>
    /// The startup lock gate (S7 #64): a modal lock screen with no
    /// owner and no main window behind it. True only when the user
    /// unlocked; closing it (or its "Exit MuseDesktop" button)
    /// returns false and the caller shuts down.
    /// </summary>
    private bool ShowStartupLockGate(AppLockRecord record)
    {
        var gate = new LockWindow(record, new AppLockThrottle(), exitOnClose: true);
        ShellTextScaling.Apply(gate, Settings.ShellFontScale);
        FocusReturn.ShowDialog(gate);
        return gate.Unlocked;
    }

    /// <summary>
    /// OS preference changes re-resolve the shell palette (S5 #43 made
    /// this Follow-Windows-only; S6 #51 widened it): a High Contrast
    /// toggle must also repaint the shell even in Dark/Light mode, and
    /// re-applying is cheap and idempotent, so every preference change
    /// simply re-applies.
    /// </summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
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
        var saved = FocusReturn.ShowDialog(wizard) == true;
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
                Dispatcher.Invoke(() =>
                {
                    // S11 #99/#100: the wake carries the launching
                    // process's intent (written before it knocked).
                    // No file (a wake from before this feature, or a
                    // failed write) is the plain "show the window".
                    var intent = PendingIntentStore.Take(AppPaths.PendingIntentPath)
                                 ?? LaunchIntent.ShowWindow;
                    _window?.ApplyLaunchIntent(intent);
                });
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
