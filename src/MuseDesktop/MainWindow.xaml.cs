using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using MuseDesktop.Core;

namespace MuseDesktop;

public partial class MainWindow : Window
{
    // Microsoft's official Evergreen bootstrapper link (see DECISIONS.md).
    private const string WebView2BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private readonly AppSettings _settings;
    private readonly DiagnosticLog _log;
    private readonly DownloadTracker _downloads;
    private readonly TrayController _tray;
    private bool _reallyExit;
    private bool _webReady;

    // v0.5.0 (S1) — navigation history model, focus mode, find, auto-retry.
    private readonly NavigationHistory _history = new();
    private bool _focusMode;
    private bool _suppressNavClick;
    private DispatcherTimer? _navPressTimer;
    private string _pageTitle = "";
    private bool _webInitFailed;
    private bool _isFullScreen;
    private WindowState _preFullScreenState;
    private WindowStyle _preFullScreenStyle;

    // v0.2.0 — update check (the check itself lives in UpdateService)
    private bool _checkingUpdate;
    private string? _updateTag;
    private string? _updateUrl;
    // v0.13.0 (S9 #81/#88) — the full selected release (assets included),
    // so the ⋯ item opens the in-app update window instead of the
    // browser; the page URL stays as the window's fallback.
    private GitHubRelease? _updateRelease;

    // v0.3.0 — the active profile's WebView2 data folder (per-profile sign-ins).
    private readonly string _profileDir;

    // v0.6.0 (S2) — taskbar progress mirror (#19), its latched failure
    // state, and the hidden retry web view's readiness (#15).
    private TaskbarProgress? _taskbar;
    private bool _downloadError;
    private bool _retryWebReady;

    // v0.8.0 (S4) — attention signal from the page title (#31/#39) and
    // the microphone-grant heuristic for tray suspend (#35). The pinned
    // WebView2 SDK exposes no microphone-capture state (see DECISIONS),
    // so a grant is remembered for the session and treated as "a voice
    // session may be live" until the page navigates.
    private AttentionSignal _lastAttention = AttentionSignal.None;
    private bool _micGrantedThisSession;

    // v0.11.0 (S7) — privacy: the Settings gateway to the live profile,
    // the counts-only privacy report state, and this session's
    // certificate-error decisions (#69: per host, session-only, never
    // persisted — a restart forgets every "continue anyway").
    private readonly WebPrivacyGateway _privacyGateway;
    private readonly PrivacyStats _privacyStats;
    private readonly Dictionary<string, bool> _certDecisions = new();

    // S7 #64 — app lock state. _isLocked is the gate every surface
    // checks; _initDeferred marks the startup case (locked before the
    // web view ever initialized — unlock runs the deferred init, so no
    // page ever renders or navigates pre-unlock); _lockThrottle is
    // shared by every lock screen this session, so closing and
    // reopening one doesn't reset the wrong-try count.
    private bool _isLocked;
    private bool _initDeferred;
    private readonly AppLockThrottle _lockThrottle = new();
    private LockWindow? _lockWindow;

    // v0.12.0 (S8) — reliability: ONE recovery coordinator (#71/#76)
    // decides every automatic reload/recreate (the S1 network retry
    // and the ProcessFailed handler used to keep separate flags); the
    // hung-page watchdog (#80); safe mode (#72); startup timings
    // (#73); the tray-suspend memory pair (#74); runtime health (#78).
    private readonly RecoveryCoordinator _recovery = new();
    private readonly WatchdogPolicy _watchdog = new();
    private DispatcherTimer? _watchdogTimer;
    private readonly bool _safeMode;
    private readonly StartupClock? _startupClock;
    private bool _firstNavigationRecorded;
    private string? _lastGoodUrl;
    private MemorySnapshot? _memBeforeSuspend;
    private MemorySnapshot? _memAfterResume;
    private RuntimeHealthResult? _runtimeHealth;

    /// <summary>True while the app lock is engaged (the shell checks this before showing any content surface).</summary>
    public bool IsLocked => _isLocked;

    public MainWindow(AppSettings settings, DiagnosticLog log, bool lockedAtStart = false,
        bool safeMode = false, StartupClock? startupClock = null)
    {
        _settings = settings;
        _log = log;
        _safeMode = safeMode;
        _startupClock = startupClock;
        // Locked-at-start is only ever the background (tray) launch
        // path — visible launches pass App's modal startup gate first.
        // The double check keeps a stray flag from locking an app
        // whose lock was removed.
        _isLocked = lockedAtStart && AppLock.IsLockActive(settings);
        _profileDir = AppPaths.ProfileDir(settings.ActiveProfileId);
        InitializeComponent();

        // v0.9.0 (S5): the web view's pre-load background follows the
        // shell palette (App applied the theme before constructing us),
        // the toolbar strips are rebuilt from the user's layout (#45),
        // shell text is scaled (#46), and later theme changes re-sync
        // the title bar + web background.
        Browser.DefaultBackgroundColor = ThemeManager.BrowserBackground;
        ApplyToolbarLayout();
        ShellTextScaling.Apply(this, _settings.ShellFontScale);
        ThemeManager.Changed += OnThemeChanged;

        // S7: the privacy gateway Settings talks to (clear data,
        // permissions, site data), and the privacy report's counters.
        _privacyGateway = new WebPrivacyGateway(
            () => _webReady ? Browser.CoreWebView2 : null,
            () => _settings.ActiveProfileId,
            () => new System.Windows.Interop.WindowInteropHelper(this).Handle,
            _log,
            OnActiveProfileCleared);
        try { _privacyStats = PrivacyStatsStore.Load(AppPaths.PrivacyStatsPath); }
        catch { _privacyStats = new PrivacyStats(); }

        _downloads = new DownloadTracker(_log, SetStatus, record =>
        {
            // Completed AND failed records land in history (S2 #16),
            // honoring the configured cap.
            try { DownloadHistoryStore.Add(AppPaths.DownloadsHistoryPath, record, _settings.DownloadHistoryCap); }
            catch (Exception ex) { _log.Warn($"Download history save failed: {ex.Message}"); }
        }, NotifyDownloadComplete);
        _downloads.ActiveChanged += active =>
        {
            CancelDownloadButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (active) StatusText.ToolTip = "";
        };
        _downloads.Completed += () => StatusText.ToolTip = "Click to open the file";
        // S2 #19: taskbar progress mirror. A failure latches the Error
        // state until the next download starts.
        _downloads.Started += () => { _downloadError = false; UpdateTaskbarProgress(); };
        _downloads.Failed += () => { _downloadError = true; UpdateTaskbarProgress(); };
        _downloads.Changed += UpdateTaskbarProgress;

        _tray = new TrayController(
            _log, Dispatcher,
            ShowFromTray,
            () => { if (_webReady) Browser.Reload(); },
            ExitApp,
            () => _settings.SuspendWhenInTray,
            () => _webReady,
            () => _downloads.IsTransferring,
            TrySuspendWebViewAsync,
            ResumeWebView,
            () => _settings.Profiles,
            () => _settings.ActiveProfileId,
            SwitchToProfileFromMenu,
            OpenDownloadsFromTray,
            OpenSettingsWindow,
            () => OnOpenInNewWindow(this, new RoutedEventArgs()),
            OpenRecentDownload,
            RecentDownloadsForTray,
            () => _settings.TraySuspendTimeout,
            () => _micGrantedThisSession,
            () => _settings.TraySingleClickRestore,
            CurrentNotificationSuppression,
            () => _isLocked,
            () => AppLock.IsLockActive(_settings),
            LockNow);

        // S6 #54: live regions for assistive-tech announcements, and
        // the download events that feed them (the wording is Core's).
        Announcer.Register(LiveRegionPolite, LiveRegionAssertive);
        _downloads.Completed += () =>
        {
            if (_downloads.CompletedPath is { } path)
                Announcer.Polite(AnnouncementText.DownloadCompleted(Path.GetFileName(path)));
        };
        _downloads.Failed += () =>
            Announcer.Assertive(AnnouncementText.DownloadFailed(
                _downloads.LastFailedName ?? "A download", _downloads.LastFailedReason));

        // S6 #58: the ring around the web view lights up while
        // keyboard focus is inside the page, so a keyboard user can
        // always tell page focus from shell focus. (Named handlers,
        // not lambdas: the S8 #71 recreate path re-attaches them to
        // the replacement control.)
        Browser.GotKeyboardFocus += OnBrowserGotKeyboardFocus;
        Browser.LostKeyboardFocus += OnBrowserLostKeyboardFocus;

        PinItem.IsChecked = _settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        // S8 #76: availability changes and resume-from-sleep feed the
        // same recovery signal as address changes — one coordinator
        // decides whether a failed page gets its one automatic reload.
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SetWindowIcon();
        ApplySavedPlacement();
        _tray.Build();
        if (_isLocked)
            _tray.SetLocked(true);
        RefreshProfileIndicator();
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // S6 #58 focus-ring handlers (named so S8 #71's recreated web view
    // control gets the same pair).
    private void OnBrowserGotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        WebFocusRing.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");

    private void OnBrowserLostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        WebFocusRing.BorderBrush = System.Windows.Media.Brushes.Transparent;

    /// <summary>
    /// Rebuild the toolbar button strips from the saved layout (S5 #45):
    /// the XAML declares the buttons once (handlers, tooltips, styles);
    /// this only re-parents them into the user's order and omits the
    /// hidden ones. The ⋯ menu button is not a layout item — it is
    /// re-added last, always.
    /// </summary>
    private void ApplyToolbarLayout()
    {
        var elements = new Dictionary<string, FrameworkElement>
        {
            ["back"] = BackButton,
            ["forward"] = ForwardButton,
            ["reload"] = ReloadButton,
            ["home"] = HomeButton,
            ["zoomout"] = ZoomOutButton,
            ["zoomlabel"] = ZoomLabel,
            ["zoomin"] = ZoomInButton,
            ["pin"] = PinButton,
            ["browser"] = BrowserButton,
        };
        LeftStrip.Children.Clear();
        foreach (var id in ToolbarLayout.VisibleIds(_settings.ToolbarOrder, _settings.ToolbarHidden, ToolbarGroup.Left))
            LeftStrip.Children.Add(elements[id]);
        RightStrip.Children.Clear();
        foreach (var id in ToolbarLayout.VisibleIds(_settings.ToolbarOrder, _settings.ToolbarHidden, ToolbarGroup.Right))
            RightStrip.Children.Add(elements[id]);
        RightStrip.Children.Add(MenuButton);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnThemeChanged(sender, e)));
            return;
        }
        ApplyDarkTitleBar();
        Browser.DefaultBackgroundColor = ThemeManager.BrowserBackground;
    }

    /// <summary>
    /// The window icon is set in code, never from a XAML attribute: v0.1.0
    /// crashed at startup because XAML's Icon="muse.ico" needed the file as
    /// a pack resource and it was only the exe's Win32 icon. Any failure
    /// here is cosmetic and must never take the app down.
    /// </summary>
    private void SetWindowIcon()
    {
        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/muse.ico"));
            return;
        }
        catch (Exception ex)
        {
            _log.Warn($"Pack icon failed, falling back to exe icon: {ex.Message}");
        }
        try
        {
            var handle = System.Drawing.Icon.ExtractAssociatedIcon(
                Environment.ProcessPath ?? "")?.Handle;
            if (handle is { } h && h != IntPtr.Zero)
            {
                Icon = Imaging.CreateBitmapSourceFromHIcon(
                    h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Exe icon fallback failed: {ex.Message}");
        }
    }

    // ───────────────────────── lifecycle ─────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyDarkTitleBar();
        // S8 #73: the window is up — the first startup milestone.
        _startupClock?.MarkWindowShown();
        try
        {
            _taskbar = new TaskbarProgress(new WindowInteropHelper(this).Handle, _log);
        }
        catch (Exception ex) { _log.Warn($"Taskbar progress init failed: {ex.Message}"); }
        // S7 #64: locked at start (background launch) — the web view
        // does not initialize, let alone navigate, until unlock. The
        // deferred work runs in OnAppUnlocked.
        if (_isLocked)
        {
            _initDeferred = true;
            _log.Info("App lock is on — web view initialization waits for unlock");
            return;
        }
        await InitWebViewAsync();
        // S8 #72: safe mode skips the automatic update check.
        MaybeAutoCheckForUpdates();
    }

    /// <summary>Started via --background: create the window, then park it in the tray.</summary>
    public void StartHidden()
    {
        Show();
        HideToTray(showBalloon: false);
    }

    public void ShowFromTray()
    {
        // S7 #64: while locked, "show the window" means the lock
        // screen — the main window (and its page) stays hidden.
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        _tray.OnShown();
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    private void HideToTray(bool showBalloon)
    {
        Hide();
        _tray.OnHidden(showBalloon);
        // S7 #64 trigger (c): hiding to the tray locks the app when
        // the sub-setting is on (it is by default). No lock UI yet —
        // it appears when the user next asks for the window.
        if (!_isLocked && _settings.AppLockOnTrayHide && AppLock.IsLockActive(_settings))
            EngageLock(presentUi: false);
    }

    // ── tray delegates (v0.8.0, S4 #32): the tray menu's window actions.
    // Each shows the window first where the action needs a visible
    // owner, then runs the exact handler the ⋯ menu uses — no
    // duplicated logic behind the tray's back.

    private void OpenDownloadsFromTray()
    {
        // S7 #64: the Downloads window is behind the lock too.
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        ShowFromTray();
        OnDownloads(this, new RoutedEventArgs());
    }

    /// <summary>Open Settings on a visible window (tray menu and the --settings launch flag, S4 #32/#33).</summary>
    public void OpenSettingsWindow()
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        ShowFromTray();
        OnSettings(this, new RoutedEventArgs());
    }

    /// <summary>Open a finished download from the tray's recent list — the Downloads window's own open behavior.</summary>
    private void OpenRecentDownload(string fullPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"Tray open-download failed: {ex.Message}");
        }
    }

    private IReadOnlyList<DownloadRecord> RecentDownloadsForTray()
    {
        try { return DownloadHistoryStore.Load(AppPaths.DownloadsHistoryPath, 5); }
        catch (Exception ex)
        {
            _log.Warn($"Tray recent-downloads load failed: {ex.Message}");
            return Array.Empty<DownloadRecord>();
        }
    }

    /// <summary>
    /// The quiet-hours gate (S4 #38): Windows' Do Not Disturb state
    /// (best-effort registry read) plus the in-app schedule, decided in
    /// Core. Every MuseDesktop-owned notification surface — balloons
    /// via the tray, the attention flash here — consults this.
    /// </summary>
    private NotificationSuppression CurrentNotificationSuppression() =>
        NotificationPolicy.Evaluate(
            SystemNotificationState.DoNotDisturbActive(),
            _settings.QuietHoursEnabled,
            _settings.QuietHoursStartMinutes,
            _settings.QuietHoursEndMinutes,
            DateTimeOffset.Now);

    // ── tray suspend delegates (v0.2.0; the timer lives in TrayController) ──

    private async Task<bool> TrySuspendWebViewAsync()
    {
        try
        {
            // S8 #74: the "before" half of the suspend memory pair,
            // captured while the renderer is still fully resident.
            _memBeforeSuspend = MemoryCollector.Collect(Browser.CoreWebView2.Environment);
            var ok = await Browser.CoreWebView2.TrySuspendAsync();
            _log.Info(ok
                ? "WebView2 suspended (window hidden in tray)"
                : "WebView2 suspend declined by the page (active media/call?)");
            return ok;
        }
        catch (Exception ex)
        {
            _log.Warn($"WebView2 suspend failed: {ex.Message}");
            return false;
        }
    }

    private bool ResumeWebView()
    {
        if (!_webReady)
            return false;
        try
        {
            Browser.CoreWebView2.Resume();
            _log.Info("WebView2 resumed");
            // S8 #74: the "after" half of the pair (the Diagnostics
            // window shows what suspend actually saved).
            _memAfterResume = MemoryCollector.Collect(Browser.CoreWebView2.Environment);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"WebView2 resume failed: {ex.Message}");
            return false;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _watchdogTimer?.Stop();
        SaveSettings();
        if (!_reallyExit && _settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray(showBalloon: true);
            return;
        }
        _tray.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        Close();
    }

    /// <summary>The active profile object (never null after Sanitized).</summary>
    private AccountProfile? ActiveProfile() =>
        _settings.Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfileId);

    /// <summary>The zoom this launch runs at (S3 #26): the active profile's override, else the shared zoom. S6 #57: clamped to the extended range when that setting is on.</summary>
    private double EffectiveZoom() =>
        Profiles.EffectiveZoom(ActiveProfile(), _settings.ZoomFactor, _settings.ExtendedZoomRange);

    /// <summary>
    /// Where a zoom value belongs (S3 #26): when the active profile has a
    /// zoom override, zoom changes update the OVERRIDE and the shared
    /// zoom is left alone; without an override they update the shared
    /// zoom, as they always have.
    /// </summary>
    private void PersistZoom(double zoom)
    {
        var active = ActiveProfile();
        if (active?.ZoomFactor is not null)
            active.ZoomFactor = ZoomMath.Clamp(zoom, _settings.ExtendedZoomRange);
        else
            _settings.ZoomFactor = ZoomMath.Clamp(zoom, _settings.ExtendedZoomRange);
    }

    // ───────────────────────── placement / settings ─────────────────────────

    private void ApplySavedPlacement()
    {
        // Per-profile placement (v0.5.0, S1 #8): the active profile's entry,
        // migrated from the legacy global fields by Sanitized().
        var placement = _settings.ActivePlacement;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Left is { } left && placement.Top is { } top)
        {
            // Multi-monitor clamp (S6 #60): the saved spot is checked
            // against the monitors connected NOW (each converted to
            // DIPs at its own scale). A monitor that has gone away
            // since the placement was saved pulls the window onto the
            // primary instead of restoring it into the void; the
            // 80x40 minimum-visible rule matches the old virtual-screen
            // check this replaces.
            var saved = new ScreenRect(left, top, placement.Width, placement.Height);
            var clamped = PlacementClamp.Clamp(saved, MonitorRects.AllInDip());
            Left = clamped.Left;
            Top = clamped.Top;
            if (clamped != saved)
                _log.Info("Saved window placement was off the connected monitors; clamped onto the primary monitor");
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (placement.Maximized)
            WindowState = WindowState.Maximized;
        ZoomLabel.Text = ZoomMath.FormatPercent(EffectiveZoom());
    }

    private void SaveSettings()
    {
        try
        {
            var bounds = RestoreBounds;
            if (!bounds.IsEmpty)
            {
                _settings.SetActivePlacement(new WindowPlacement
                {
                    Left = bounds.Left,
                    Top = bounds.Top,
                    Width = bounds.Width,
                    Height = bounds.Height,
                    Maximized = WindowState == WindowState.Maximized,
                });
            }
            if (_webReady)
            {
                PersistZoom(Browser.ZoomFactor);
                var source = Browser.Source?.ToString();
                if (source is not null
                    && UrlPolicy.ClassifyNavigation(source) == LinkDisposition.InApp
                    && Uri.TryCreate(source, UriKind.Absolute, out var u)
                    && UrlPolicy.IsInAppHost(u.Host))
                {
                    // Per-profile restore (v0.3.1): never record one
                    // profile's page as another profile's start page.
                    _settings.LastUrlByProfile[_settings.ActiveProfileId] = source;
                }
            }
            SettingsStore.Save(AppPaths.SettingsPath, _settings);
            // S8 #79: saves are frequent (every navigation), so the
            // backup service applies its once-a-day throttle itself.
            BackupService.MaybeAutoBackup(_log);
        }
        catch (Exception ex)
        {
            _log.Warn($"Settings save failed: {ex.Message}");
        }
    }

    // ───────────────────────── WebView2 ─────────────────────────

    /// <summary>
    /// Build (or, for S8 #71 recovery, rebuild) the web view.
    /// <paramref name="restoreUrl"/> is the recovery path's last page —
    /// when given, it wins over the start-page resolution.
    /// </summary>
    private async Task InitWebViewAsync(string? restoreUrl = null)
    {
        _webInitFailed = false;
        try
        {
            Directory.CreateDirectory(_profileDir);
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _profileDir);
            await Browser.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _webInitFailed = true;
            _log.Error("WebView2 Runtime not found");
            ShowOverlay(
                "The WebView2 Runtime is missing",
                "MuseDesktop displays Muse with Microsoft's WebView2 Runtime. " +
                "It ships with Windows 11 and most Windows 10 PCs, but this PC does not have it yet. " +
                "Download the small Evergreen installer from Microsoft, run it, then retry.",
                "Download WebView2 Runtime",
                () => OpenExternal(WebView2BootstrapperUrl));
            SetStatus("WebView2 Runtime missing — see the message above");
            return;
        }
        catch (Exception ex)
        {
            _webInitFailed = true;
            _log.Error($"WebView2 init failed: {ex.GetType().Name}: {ex.Message}");
            ShowOverlay(
                "Muse could not start",
                $"Something went wrong starting the web view ({ex.Message}). " +
                $"The log at {AppPaths.LogPath} has the details.",
                "Retry", null);
            return;
        }

        var core = Browser.CoreWebView2;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        // Release builds: DevTools would let anyone script the live,
        // signed-in Muse session from the app's own window.
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        // S7 #68: password autosave / general autofill are the user's
        // choice now (both default to WebView2's defaults — on). The
        // value is shared per profile, so this one application covers
        // every web view this profile ever creates.
        core.Settings.IsGeneralAutofillEnabled = _settings.GeneralAutofillEnabled;
        core.Settings.IsPasswordAutosaveEnabled = _settings.PasswordAutosaveEnabled;

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        // S7 #69: certificate errors get the app's plain-language
        // dialog (ask every time; "continue" is per-host, session-only).
        core.ServerCertificateErrorDetected += OnServerCertificateErrorDetected;
        core.DownloadStarting += OnDownloadStarting;
        core.ContextMenuRequested += OnContextMenuRequested;
        core.DocumentTitleChanged += OnTitleChanged;
        core.SourceChanged += OnSourceChanged;
        core.HistoryChanged += (_, _) => UpdateNavButtons();
        try
        {
            // Find in page (v0.5.0, S1 #1): CoreWebView2.Find exists in the
            // pinned SDK 1.0.4258.31 (StartAsync/FindNext/FindPrevious/Stop,
            // MatchCount / ActiveMatchIndex + change events).
            core.Find.MatchCountChanged += (_, _) => UpdateFindCount();
            core.Find.ActiveMatchIndexChanged += (_, _) => UpdateFindCount();
        }
        catch (Exception ex) { _log.Warn($"Find wiring failed: {ex.Message}"); }
        core.ContainsFullScreenElementChanged += OnFullScreenElementChanged;
        core.ProcessFailed += OnProcessFailed;
        Browser.ZoomFactorChanged += (_, _) =>
        {
            ZoomLabel.Text = ZoomMath.FormatPercent(Browser.ZoomFactor);
        };

        Browser.ZoomFactor = EffectiveZoom();
        _webReady = true;
        // S8 #73: the second startup milestone.
        _startupClock?.MarkWebViewReady();
        // S8 #78: runtime health — version + channel facts, a warning
        // when the runtime is missing/ancient, and the fixed-version
        // folder note. Evergreen is what CreateAsync resolves with
        // browserExecutableFolder: null above.
        _runtimeHealth = RuntimeHealthCheck.EvaluateCurrent();
        if (_runtimeHealth.Status == RuntimeHealthStatus.Ok)
            _log.Info($"Runtime health: {_runtimeHealth.Message}");
        else
            _log.Warn($"Runtime health: {_runtimeHealth.Message}");
        _log.Info($"WebView2 {envVersion()} ready; profile {_profileDir}");
        // Start page (S5 #47): an invalid custom page falls back to
        // Home and says so — the stored value is logged redacted only.
        var (startupUrl, customFellBack) = _settings.ResolveStartupUrl();
        if (customFellBack)
        {
            var active = ActiveProfile();
            _log.Warn($"Profile {_settings.ActiveProfileId} has an unusable custom start page " +
                      $"({UrlPolicy.RedactForLog(active?.CustomStartUrl)}); starting at the Muse home page");
        }
        // S8 #72: safe mode ignores the start-page resolution entirely
        // and lands on Home; #71 recovery restores the crashed page.
        var navigateTo = restoreUrl ?? SafeModePolicy.StartupUrl(_safeMode, startupUrl);
        if (_safeMode && restoreUrl is null)
            _log.Info("Safe mode: starting at the Muse home page (no last-page restore, update check skipped)");
        core.Navigate(navigateTo);
        SetStatus(_safeMode ? "Safe mode — Loading Muse…" : "Loading Muse…");
        StartWatchdog();

        string envVersion()
        {
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { return "(unknown version)"; }
        }
    }

    /// <summary>
    /// For the --smoke-test mode: waits until WebView2 initialization has
    /// succeeded (the Loaded handler runs it) or failed, up to a timeout.
    /// Navigation success is deliberately not required — CI networks vary;
    /// what the smoke test proves is that the window constructs AND the
    /// WebView2 environment/controller come up on a real machine image.
    /// </summary>
    public async Task<bool> WaitForWebViewReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_webReady && Browser.CoreWebView2 is not null)
                return true;
            if (_webInitFailed)
                return false;
            await Task.Delay(250);
        }
        return false;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (UrlPolicy.ClassifyNavigation(e.Uri) == LinkDisposition.Block)
        {
            e.Cancel = true;
            _log.Warn($"Blocked navigation: {UrlPolicy.RedactForLog(e.Uri)}");
            RecordPrivacy(PrivacyEventCategory.BlockedNavigation, SchemeOf(e.Uri), HostOf(e.Uri));
            SetStatus("That link can't open inside MuseDesktop");
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        RecordFirstNavigation();
        if (e.IsSuccess)
        {
            Overlay.Visibility = Visibility.Collapsed;
            SetStatus("Ready");
            // S8 #71/#76: a successful load closes the recovery
            // incident and refills both automatic budgets.
            _recovery.OnPageLoaded();
            var src = Browser.Source?.ToString();
            if (src is not null)
                _lastGoodUrl = src;
            // S4 #35: a new document ends whatever media capture the
            // old one held, so the microphone-grant heuristic resets.
            _micGrantedThisSession = false;
            if (src is not null)
                _history.Record(src, Browser.CoreWebView2.DocumentTitle);
            SaveSettings();
        }
        else
        {
            // S8 #76: the coordinator now owns the failed state the
            // connectivity retry keys on (S1 #10 used a local flag).
            _recovery.OnLoadFailed();
            // Smarter load-failure overlay (v0.5.0, S1 #10): Core classifies
            // the WebErrorStatus into a plain-language category.
            _log.Warn($"Navigation failed: {e.WebErrorStatus} for {UrlPolicy.RedactForLog(Browser.Source?.ToString())}");
            _history.NoteClosedPage(Browser.Source?.ToString());
            ShowOverlay(
                "Muse couldn't be reached",
                LoadFailureClassifier.MessageFor(e.WebErrorStatus.ToString()),
                "Retry", null);
            SetStatus($"Load failed: {e.WebErrorStatus}");
        }
        UpdateNavButtons();
    }

    /// <summary>S8 #73: the first navigation of the launch completes the startup timings — record, persist, and log them once.</summary>
    private void RecordFirstNavigation()
    {
        if (_firstNavigationRecorded)
            return;
        _firstNavigationRecorded = true;
        if (_startupClock is null)
            return;
        _startupClock.MarkFirstNavigation();
        try
        {
            var runs = StartupTimingsStore.Load(AppPaths.StartupTimingsPath);
            StartupTimingsStore.Add(runs, _startupClock.Snapshot());
            StartupTimingsStore.Save(AppPaths.StartupTimingsPath, runs);
            _log.Info($"Startup timings: {_startupClock.Snapshot().Describe()}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Saving startup timings failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Connectivity is back (S1 #10, unified in S8 #76): network
    /// address/availability changes and resume-from-sleep all land
    /// here, and the recovery coordinator decides whether the failed
    /// page gets its one automatic reload — so a flapping network
    /// cannot cause a retry loop, and no second mechanism can reload
    /// behind the coordinator's back.
    /// </summary>
    private void OnNetworkChanged(object? sender, EventArgs e) =>
        OnConnectivitySignal("Network changed");

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            OnConnectivitySignal("PC resumed from sleep");
    }

    private void OnConnectivitySignal(string source)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_webReady)
                return;
            if (!NetworkInterface.GetIsNetworkAvailable())
                return;
            if (_recovery.OnConnectivityRestored(DateTimeOffset.UtcNow) != RecoveryDecision.ReloadPage)
                return;
            _log.Info($"{source} while a load failure was showing; auto-retrying once");
            OnReload(this, new RoutedEventArgs());
        });
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Audit (v0.5.0, S1 #11): middle-click and Ctrl+click on a link surface
        // here exactly like target=_blank / window.open — WebView2 exposes no
        // separate gesture path — so they inherit this same UrlPolicy routing.
        // Core tests (S1Tests.NewWindowPolicy_CoversMiddleAndCtrlClickHosts)
        // pin the classification for every host class. No gap found.
        e.Handled = true;
        switch (UrlPolicy.ClassifyNewWindow(e.Uri))
        {
            case LinkDisposition.InApp:
                Browser.CoreWebView2.Navigate(e.Uri);
                break;
            case LinkDisposition.ExternalBrowser:
                OpenExternal(e.Uri);
                SetStatus("Opened link in your browser");
                break;
            default:
                _log.Warn($"Blocked new-window request: {UrlPolicy.RedactForLog(e.Uri)}");
                RecordPrivacy(PrivacyEventCategory.BlockedNewWindow, SchemeOf(e.Uri), HostOf(e.Uri));
                SetStatus("That link was blocked");
                break;
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        var kind = e.PermissionKind switch
        {
            CoreWebView2PermissionKind.Notifications => "notifications",
            CoreWebView2PermissionKind.Microphone => "microphone",
            _ => "other",
        };
        if (UrlPolicy.IsAutoGrantedPermission(e.Uri, kind))
        {
            e.State = CoreWebView2PermissionState.Allow;
            e.Handled = true;
            _log.Info($"Granted {kind} to {UrlPolicy.RedactForLog(e.Uri)}");
            if (e.PermissionKind == CoreWebView2PermissionKind.Microphone && !_micGrantedThisSession)
            {
                // S4 #35: remember the grant for the session — the
                // pinned SDK has no capture-state signal, so tray
                // suspend treats this as "voice may be live" until the
                // page navigates (which ends any capture it held).
                _micGrantedThisSession = true;
                _log.Info("Microphone granted this session; tray suspend will stay out of the way until the page navigates");
            }
        }
        else
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
            _log.Info($"Denied {e.PermissionKind} for {UrlPolicy.RedactForLog(e.Uri)}");
            RecordPrivacy(PrivacyEventCategory.DeniedPermission, e.PermissionKind.ToString(), HostOf(e.Uri));
        }
    }

    /// <summary>Show the Save-As dialog for one download; null means the user cancelled.</summary>
    private string? ShowSaveDownloadDialog(string suggestedName, string initialDirectory)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedName,
            InitialDirectory = initialDirectory,
            Title = "Save download",
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // We report progress in the status bar, so suppress WebView2's own
        // download flyout and follow the operation ourselves. This handler
        // serves BOTH the main web view and the hidden retry view (S2 #15):
        // a retried download gets exactly the same destination policy.
        var suggestedName = Path.GetFileName(e.ResultFilePath);

        // S2 #20: the effective folder is the active profile's override,
        // else the shared downloads folder, else WebView2's default (the
        // system Downloads folder — e.ResultFilePath already points there).
        var effectiveFolder = _settings.EffectiveDownloadsFolder();

        if (_settings.AskWhereToSave)
        {
            // v0.2.0: ask where to save every download.
            var chosen = ShowSaveDownloadDialog(suggestedName, effectiveFolder ?? AppPaths.DownloadsHint);
            if (chosen is not null)
            {
                e.ResultFilePath = chosen;
            }
            else
            {
                e.Cancel = true;
                _log.Info($"Download cancelled at save prompt: {suggestedName}");
                SetStatus("Download cancelled");
                return;
            }
        }
        else
        {
            var candidate = e.ResultFilePath;
            if (!string.IsNullOrWhiteSpace(effectiveFolder))
            {
                try
                {
                    Directory.CreateDirectory(effectiveFolder);
                    candidate = Path.Combine(effectiveFolder, suggestedName);
                }
                catch (Exception ex)
                {
                    _log.Warn($"Downloads folder unusable, keeping default: {ex.Message}");
                }
            }

            // S2 #21: filename-collision policy when the target exists.
            try
            {
                if (File.Exists(candidate))
                {
                    if (_settings.CollisionPolicy == DownloadCollisionPolicy.AskWhereToSave)
                    {
                        var dir = Path.GetDirectoryName(candidate) ?? AppPaths.DownloadsHint;
                        var chosen = ShowSaveDownloadDialog(suggestedName, dir);
                        if (chosen is null)
                        {
                            e.Cancel = true;
                            _log.Info($"Download cancelled at collision save prompt: {suggestedName}");
                            SetStatus("Download cancelled");
                            return;
                        }
                        candidate = chosen;
                    }
                    else
                    {
                        candidate = UniqueFileName.ForPath(candidate);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Collision handling failed, keeping the original target: {ex.Message}");
            }
            e.ResultFilePath = candidate;
        }

        e.Handled = true;
        // Destination decided above; the tracker follows the operation
        // (progress, cancel, pause/resume, completion, history) from here.
        _downloads.Attach(e.DownloadOperation);
    }

    /// <summary>
    /// Retry a download that failed in this session (S2 #15): navigate a
    /// hidden WebView2 — created from the SAME environment/profile, so the
    /// sign-in applies — to the failed URI held in memory by the tracker.
    /// Its DownloadStarting is this class's own handler, which takes the
    /// operation over and applies the normal destination policy; the
    /// hidden view itself is never shown, and if the server answers with
    /// a page instead of a file that page loads invisibly and is dropped
    /// when the next retry (or app exit) disposes of it.
    /// </summary>
    private async Task RetryFailedDownloadAsync(string sessionFailedId)
    {
        var failed = _downloads.TakeSessionFailed(sessionFailedId);
        if (failed is null)
        {
            SetStatus("That download can't be retried anymore (retries last for this session only)");
            return;
        }
        if (!_webReady || Browser.CoreWebView2 is null)
        {
            SetStatus("Wait for Muse to load, then retry the download");
            return;
        }
        try
        {
            if (!_retryWebReady)
            {
                await RetryBrowser.EnsureCoreWebView2Async(Browser.CoreWebView2.Environment);
                RetryBrowser.CoreWebView2.DownloadStarting += OnDownloadStarting;
                _retryWebReady = true;
            }
            // Log host+path only — the full URI may carry a signed token.
            _log.Info($"Retrying download: {failed.FileName} from {UrlPolicy.RedactForLog(failed.Uri)}");
            SetStatus($"Retrying download: {failed.FileName}…");
            RetryBrowser.CoreWebView2.Navigate(failed.Uri);
        }
        catch (Exception ex)
        {
            _log.Warn($"Retry download failed to start: {ex.Message}");
            SetStatus($"Couldn't retry {failed.FileName}");
        }
    }

    /// <summary>S2 #18: opt-in completion balloon, only while the window is hidden in the tray.</summary>
    private void NotifyDownloadComplete(string fileName)
    {
        // S7 #64: while locked, no balloon naming the file — the
        // download itself finishes normally behind the lock.
        if (_isLocked)
            return;
        if (_settings.NotifyWhenDownloadCompletes && !IsVisible)
            _tray.ShowDownloadComplete(fileName);
    }

    /// <summary>Mirror the aggregate download state onto the taskbar button (S2 #19).</summary>
    private void UpdateTaskbarProgress()
    {
        if (_taskbar is null)
            return;
        if (_downloadError && !_downloads.IsTransferring)
        {
            _taskbar.SetError();
            return;
        }
        if (_downloads.IsTransferring)
        {
            if (_downloads.OverallFraction is { } fraction)
                _taskbar.Set(fraction);
            else
                _taskbar.SetIndeterminate();
        }
        else if (_downloads.AnyPaused)
        {
            _taskbar.SetPaused(_downloads.OverallFraction);
        }
        else
        {
            _taskbar.Clear();
        }
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e) => _downloads.Cancel();

    private void OnStatusTextClick(object sender, MouseButtonEventArgs e)
    {
        // While downloads are running, the status text opens the manager
        // (S2 #13); once one has completed, it keeps its click-to-open.
        if (_downloads.IsActive)
            OnDownloads(this, new RoutedEventArgs());
        else
            _downloads.TryOpenCompleted();
    }

    private void OnTitleChanged(object? sender, object e)
    {
        _pageTitle = Browser.CoreWebView2.DocumentTitle ?? "";
        TitleText.Text = string.IsNullOrWhiteSpace(_pageTitle) ? "Muse" : _pageTitle;
        ApplyWindowTitle();
        OnAttentionSignal(AttentionSignal.Parse(_pageTitle));
    }

    /// <summary>
    /// Attention (S4 #31/#39): the title's leading "(N)" count is the
    /// only signal trusted. It paints the tray badge + taskbar overlay,
    /// and — on the rising edge only, while the user isn't looking at
    /// the window — optionally flashes the taskbar button when the
    /// (default-off) setting allows and quiet hours permit.
    /// </summary>
    private void OnAttentionSignal(AttentionSignal signal)
    {
        _tray.SetAttention(signal);
        UpdateTaskbarOverlay(signal);
        var risingEdge = signal.HasAttention && !_lastAttention.HasAttention;
        _lastAttention = signal;
        if (!risingEdge)
            return;
        if (IsVisible && IsActive && WindowState != WindowState.Minimized)
            return; // the user is already looking at Muse
        if (!_settings.FlashTaskbarOnAttention)
            return;
        // Reduce motion (S6 #55): the flash is the one attention effect
        // the shell produces, and it is exactly the kind of effect the
        // setting exists to quiet.
        if (MotionPolicy.IsReduced(_settings.ReduceMotion, SystemMotion.AnimationsEnabled()))
        {
            _log.Info("Taskbar attention flash not shown: reduce motion is on");
            return;
        }
        var suppression = CurrentNotificationSuppression();
        if (suppression != NotificationSuppression.None)
        {
            _log.Info($"Taskbar attention flash not shown: {NotificationPolicy.ReasonText(suppression)}");
            return;
        }
        FlashTaskbarButton();
    }

    /// <summary>Mirror the attention badge onto the taskbar button (S4 #31) via WPF's TaskbarItemInfo overlay.</summary>
    private void UpdateTaskbarOverlay(AttentionSignal signal)
    {
        try
        {
            TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
            if (signal.HasAttention)
            {
                TaskbarItemInfo.Overlay = TrayIconRenderer.BadgeOverlay(
                    TrayIconPlanner.Plan(false, signal).BadgeText);
                TaskbarItemInfo.Description = signal.Kind == AttentionKind.Count
                    ? TrayIconPlanner.TooltipSuffix(signal).TrimStart(' ', '·')
                    : "MuseDesktop — attention";
            }
            else
            {
                TaskbarItemInfo.Overlay = null;
                TaskbarItemInfo.Description = "MuseDesktop";
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Taskbar attention overlay failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Gentle taskbar flash (S4 #39): three flashes of the taskbar
    /// button at the default rate. FLASHW_* never steals focus or
    /// raises the window; a window hidden in the tray has no taskbar
    /// button, so this only ever fires for a minimized/background one.
    /// </summary>
    private void FlashTaskbarButton()
    {
        try
        {
            if (!IsVisible)
                return;
            var info = new FlashWindowInfo
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FlashWindowInfo>(),
                Hwnd = new WindowInteropHelper(this).Handle,
                Flags = FlashwTray,
                Count = 3,
                Timeout = 0,
            };
            FlashWindowEx(ref info);
        }
        catch (Exception ex)
        {
            _log.Warn($"Taskbar flash failed: {ex.Message}");
        }
    }

    private const uint FlashwTray = 0x0002; // FLASHW_TRAY: flash the taskbar button only

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    /// <summary>OS window title (v0.5.0, S1 #12): composed in Core so the
    /// profile-name rule (only when more than one profile exists) is tested.</summary>
    private void ApplyWindowTitle()
    {
        var active = _settings.Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfileId);
        Title = WindowTitle.Compose(_pageTitle, active?.Name, _settings.Profiles.Count > 1);
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        var src = Browser.Source?.ToString() ?? "";
        AddressText.Text = Uri.TryCreate(src, UriKind.Absolute, out var u) ? u.Host : src;
        // S8 #71: the freshest in-app page is what crash recovery
        // restores — kept current here so it survives the web view.
        if (src.Length > 0 && UrlPolicy.ClassifyNavigation(src) == LinkDisposition.InApp)
            _lastGoodUrl = src;
        UpdateNavButtons();
    }

    private void OnFullScreenElementChanged(object? sender, object e)
    {
        // Page content (e.g. a video) asked for full screen.
        SetFullScreen(Browser.CoreWebView2.ContainsFullScreenElement);
    }

    /// <summary>
    /// Process failures (S8 #71): the recovery coordinator decides —
    /// one automatic recovery per incident (reload the page for a
    /// renderer exit, recreate the web view for a browser exit), then
    /// the overlay. Non-fatal kinds (GPU, utility…) are WebView2's own
    /// to restart and only get logged; RenderProcessUnresponsive is
    /// the watchdog's (#80) case, not an automatic reload's.
    /// </summary>
    private async void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var kind = Enum.TryParse<ProcessFailureKind>(e.ProcessFailedKind.ToString(), out var mapped)
            ? mapped
            : ProcessFailureKind.Other;
        _log.Error($"WebView2 process failed: {e.ProcessFailedKind} ({e.Reason})");
        var decision = _recovery.OnProcessFailed(kind, DateTimeOffset.UtcNow);
        switch (decision)
        {
            case RecoveryDecision.ReloadPage:
                _log.Info("Recovery: reloading the page automatically (one automatic recovery per incident)");
                SetStatus("The page stopped responding — reloading it");
                try { Browser.CoreWebView2.Navigate(_lastGoodUrl ?? UrlPolicy.HomeUrl); }
                catch (Exception ex)
                {
                    _log.Warn($"Recovery reload failed: {ex.Message}");
                    ShowCrashOverlay(e.ProcessFailedKind.ToString());
                }
                break;
            case RecoveryDecision.RecreateWebView:
                _log.Info("Recovery: recreating the web view automatically (one automatic recovery per incident)");
                SetStatus("The web view stopped — rebuilding it");
                await RecreateWebViewAsync();
                break;
            case RecoveryDecision.ShowFailureOverlay:
                _log.Warn("Recovery: automatic recovery already used for this incident — showing the failure overlay");
                ShowCrashOverlay(e.ProcessFailedKind.ToString());
                break;
            default:
                SetStatus($"Web view problem: {e.ProcessFailedKind}");
                break;
        }
    }

    private void ShowCrashOverlay(string kindName)
    {
        _history.NoteClosedPage(_lastGoodUrl);
        ShowOverlay(
            "The page stopped responding",
            "Muse's web view closed unexpectedly, and the automatic recovery didn't fix it. Reloading usually does, and your work is saved in the cloud.",
            "Reload Muse", null);
        SetStatus($"Web view problem: {kindName}");
    }

    /// <summary>
    /// Recreate the web view after a browser-process exit (S8 #71):
    /// the WPF controls bound to the dead environment are disposed and
    /// replaced with fresh ones (the XAML fields are reassigned — the
    /// rest of the window only ever talks to the fields), then the
    /// normal init runs against a new environment and restores the
    /// last page. The hidden retry view belonged to the dead
    /// environment too and is replaced alongside.
    /// </summary>
    private async Task RecreateWebViewAsync()
    {
        var restoreUrl = _lastGoodUrl ?? UrlPolicy.HomeUrl;
        try
        {
            _watchdogTimer?.Stop();
            var oldBrowser = Browser;
            WebFocusRing.Child = null;
            try { oldBrowser.Dispose(); } catch { /* already dead — that's why we're here */ }
            var fresh = new Microsoft.Web.WebView2.Wpf.WebView2
            {
                DefaultBackgroundColor = ThemeManager.BrowserBackground,
            };
            System.Windows.Automation.AutomationProperties.SetName(fresh, "Muse web content");
            fresh.GotKeyboardFocus += OnBrowserGotKeyboardFocus;
            fresh.LostKeyboardFocus += OnBrowserLostKeyboardFocus;
            Browser = fresh;
            WebFocusRing.Child = fresh;

            var oldRetry = RetryBrowser;
            ContentGrid.Children.Remove(oldRetry);
            try { oldRetry.Dispose(); } catch { /* same dead environment */ }
            var freshRetry = new Microsoft.Web.WebView2.Wpf.WebView2
            {
                Width = 1,
                Height = 1,
                Visibility = Visibility.Hidden,
                IsHitTestVisible = false,
                DefaultBackgroundColor = ThemeManager.BrowserBackground,
            };
            RetryBrowser = freshRetry;
            ContentGrid.Children.Add(freshRetry);

            _webReady = false;
            _retryWebReady = false;
            await InitWebViewAsync(restoreUrl);
            _log.Info($"Web view recreated; restored {UrlPolicy.RedactForLog(restoreUrl)}");
        }
        catch (Exception ex)
        {
            _log.Error($"Web view recreation failed: {ex}");
            ShowCrashOverlay("BrowserProcessExited");
        }
    }

    // ───────────────────── hung-page watchdog (S8 #80) ─────────────────────

    /// <summary>
    /// The watchdog timer (S8 #80): every 15 s a trivial script ping
    /// with a 5 s timeout, decided by Core's WatchdogPolicy. It never
    /// runs while the web view is suspended (a sleeping renderer
    /// can't answer — that's not a hang), while locked, or after the
    /// mic was granted this session / while the page plays audio — a
    /// busy voice page must not be misjudged. Two consecutive misses
    /// show the Reload/Wait banner; nothing is ever auto-reloaded.
    /// </summary>
    private void StartWatchdog()
    {
        _watchdogTimer?.Stop();
        _watchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _watchdogTimer.Tick += OnWatchdogTick;
        _watchdogTimer.Start();
    }

    private async void OnWatchdogTick(object? sender, EventArgs e)
    {
        if (!_webReady || _isLocked || _tray.IsSuspended)
            return;
        if (_micGrantedThisSession)
            return; // S4 #35's heuristic: a voice session may be live
        bool alive;
        try
        {
            if (Browser.CoreWebView2.IsDocumentPlayingAudio)
                return; // streaming/voice playback — not evidence of a hang
            var ping = Browser.CoreWebView2.ExecuteScriptAsync("1");
            var finished = await Task.WhenAny(ping, Task.Delay(TimeSpan.FromSeconds(5)));
            alive = finished == ping && ping.IsCompletedSuccessfully;
        }
        catch
        {
            alive = false;
        }
        var decision = alive
            ? _watchdog.OnPingSucceeded(DateTimeOffset.UtcNow)
            : _watchdog.OnPingMissed(DateTimeOffset.UtcNow);
        if (decision == WatchdogDecision.ShowHungPrompt)
        {
            _log.Warn("Watchdog: the page hasn't answered two checks in a row — offering Reload / Wait (nothing is reloaded automatically)");
            WatchdogBanner.Visibility = Visibility.Visible;
        }
        else if (decision == WatchdogDecision.HideHungPrompt)
        {
            _log.Info("Watchdog: the page is answering again — banner hidden");
            WatchdogBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void OnWatchdogReload(object sender, RoutedEventArgs e)
    {
        _watchdog.OnReloadChosen();
        WatchdogBanner.Visibility = Visibility.Collapsed;
        _log.Info("Watchdog: user chose Reload");
        OnReload(this, new RoutedEventArgs());
    }

    private void OnWatchdogWait(object sender, RoutedEventArgs e)
    {
        _watchdog.Snooze(DateTimeOffset.UtcNow);
        WatchdogBanner.Visibility = Visibility.Collapsed;
        _log.Info("Watchdog: user chose Wait — quiet for 2 minutes");
        SetStatus("Page left as-is — the responsiveness check is paused for 2 minutes");
    }

    // ───────────────────────── UI actions ─────────────────────────

    private void UpdateNavButtons()
    {
        if (!_webReady) return;
        BackButton.IsEnabled = Browser.CanGoBack;
        ForwardButton.IsEnabled = Browser.CanGoForward;
    }

    private void SetStatus(string text, string? right = null)
    {
        StatusText.Text = text;
        StatusRight.Text = right ?? "";
    }

    private void ShowOverlay(string title, string body, string buttonText, Action? buttonOverride)
    {
        OverlayTitle.Text = title;
        OverlayBody.Text = body;
        OverlayButton.Content = buttonText;
        OverlayButton.Visibility = Visibility.Visible;
        Overlay.Visibility = Visibility.Visible;
        _overlayAction = buttonOverride;
        OverlayButton.Tag = buttonOverride is null ? "reload" : "custom";
    }

    private Action? _overlayAction;

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_suppressNavClick) { _suppressNavClick = false; return; }
        if (_webReady && Browser.CanGoBack) Browser.GoBack();
    }

    private void OnForward(object sender, RoutedEventArgs e)
    {
        if (_suppressNavClick) { _suppressNavClick = false; return; }
        if (_webReady && Browser.CanGoForward) Browser.GoForward();
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (sender == OverlayButton && OverlayButton.Tag as string == "custom" && _overlayAction is not null)
        {
            _overlayAction();
            return;
        }
        Overlay.Visibility = Visibility.Collapsed;
        if (_webReady) Browser.Reload();
        else _ = InitWebViewAsync();
    }

    private void OnHome(object sender, RoutedEventArgs e)
    {
        if (_webReady) Browser.CoreWebView2.Navigate(UrlPolicy.HomeUrl);
    }

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        if (_webReady) Browser.ZoomFactor = ZoomMath.StepUp(Browser.ZoomFactor, _settings.ExtendedZoomRange);
    }

    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        if (_webReady) Browser.ZoomFactor = ZoomMath.StepDown(Browser.ZoomFactor, _settings.ExtendedZoomRange);
    }

    private void OnOpenInBrowser(object sender, RoutedEventArgs e)
    {
        var src = Browser.Source?.ToString();
        if (!string.IsNullOrEmpty(src)) OpenExternal(src);
    }

    private void OnMenu(object sender, RoutedEventArgs e)
    {
        // Pop-up menus live outside the window's tree, so S5 #46 scales
        // this one explicitly each time it opens.
        ShellTextScaling.Apply(MoreMenu, _settings.ShellFontScale);
        // S7 #64: "Lock now" exists only while a lock is set (and this
        // menu can't be open while locked — the window is hidden then).
        LockNowItem.Visibility = AppLock.IsLockActive(_settings) && !_isLocked
            ? Visibility.Visible
            : Visibility.Collapsed;
        MoreMenu.PlacementTarget = MenuButton;
        MoreMenu.IsOpen = true;
    }

    // ───────────────────────── settings (v0.2.0) ─────────────────────────

    /// <summary>
    /// The Settings window's test-notification button (S10 #92): the
    /// tray shows the balloon (sent even under quiet hours/DND because
    /// the user asked) and the Core line reports the outcome — naming
    /// the suppression automatic notifications are under, if any.
    /// </summary>
    private string RunTestNotification()
    {
        var (suppression, shown) = _tray.ShowTestNotification();
        return GuidedSelfTests.NotificationLine(suppression, shown);
    }

    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>
    /// Open Settings, optionally landing on a section (S10 #91: the
    /// Help pages point at the section their fix lives in — the clone
    /// carries the section id into the window's own restore-scroll, and
    /// Cancel discards it like every other clone edit).
    /// </summary>
    private void OpenSettings(string? sectionId = null)
    {
        var clone = _settings.Clone();
        if (sectionId is not null && SettingsSearch.IsKnownSection(sectionId))
            clone.SettingsLastSection = sectionId;
        var dialog = new SettingsWindow(clone, _log, _privacyGateway, SwitchToProfile,
            notificationTest: RunTestNotification) { Owner = this };
        // S6 #56: closing Settings returns focus to whatever invoked it
        // (usually the page itself).
        if (FocusReturn.ShowDialog(dialog) != true)
            return;

        var removedProfileIds = _settings.Profiles
            .Select(p => p.Id)
            .Where(id => clone.Profiles.All(p => p.Id != id))
            .ToList();

        _settings.CloseToTray = clone.CloseToTray;
        _settings.StartWithWindows = clone.StartWithWindows;
        _settings.StartMinimizedToTray = clone.StartMinimizedToTray;
        _settings.SuspendWhenInTray = clone.SuspendWhenInTray;
        _settings.TraySuspendTimeout = clone.TraySuspendTimeout;
        _settings.TraySingleClickRestore = clone.TraySingleClickRestore;
        _settings.FlashTaskbarOnAttention = clone.FlashTaskbarOnAttention;
        _settings.QuietHoursEnabled = clone.QuietHoursEnabled;
        _settings.QuietHoursStartMinutes = clone.QuietHoursStartMinutes;
        _settings.QuietHoursEndMinutes = clone.QuietHoursEndMinutes;
        _settings.CheckForUpdates = clone.CheckForUpdates;
        // S9: update channel/frequency/snooze + What's New opt-out.
        // (State records — last check, last seen, kept installers —
        // are NOT adopted: they live outside the Settings dialog.)
        _settings.UpdateChannel = clone.UpdateChannel;
        _settings.UpdateCheckFrequency = clone.UpdateCheckFrequency;
        _settings.UpdateSnoozedUntilUtc = clone.UpdateSnoozedUntilUtc;
        _settings.ShowWhatsNew = clone.ShowWhatsNew;
        _settings.AskWhereToSave = clone.AskWhereToSave;
        _settings.NotifyWhenDownloadCompletes = clone.NotifyWhenDownloadCompletes;
        _settings.DownloadHistoryCap = clone.DownloadHistoryCap;
        _settings.CollisionPolicy = clone.CollisionPolicy;
        _settings.DownloadsFolder = clone.DownloadsFolder;
        _settings.ZoomFactor = clone.ZoomFactor;
        _settings.Profiles = clone.Profiles;
        _settings.ActiveProfileId = clone.ActiveProfileId;
        _settings.LastUrlByProfile = clone.LastUrlByProfile;
        _settings.PendingProfileFolderDeletions = clone.PendingProfileFolderDeletions;
        _settings.AlwaysOnTop = clone.AlwaysOnTop;
        // S5: appearance + Settings-window memory ride the same adoption.
        _settings.Theme = clone.Theme;
        _settings.AccentColor = clone.AccentColor;
        _settings.ShellFontScale = clone.ShellFontScale;
        _settings.ToolbarOrder = clone.ToolbarOrder;
        _settings.ToolbarHidden = clone.ToolbarHidden;
        _settings.SettingsLastSection = clone.SettingsLastSection;
        _settings.SettingsWindowWidth = clone.SettingsWindowWidth;
        _settings.SettingsWindowHeight = clone.SettingsWindowHeight;
        _settings.FirstRunCompleted = clone.FirstRunCompleted;
        // S6: shortcut remaps, reduce motion, extended zoom range.
        _settings.ShortcutOverrides = clone.ShortcutOverrides;
        _settings.ReduceMotion = clone.ReduceMotion;
        _settings.ExtendedZoomRange = clone.ExtendedZoomRange;
        // S7: the autofill toggles (#68, applied live just below) and
        // the app-lock record + switches (#64 — write-through from the
        // Privacy section's own dialogs, adopted here like everything
        // else so Cancel in Settings discards a removal too).
        _settings.PasswordAutosaveEnabled = clone.PasswordAutosaveEnabled;
        _settings.GeneralAutofillEnabled = clone.GeneralAutofillEnabled;
        _settings.AppLockEnabled = clone.AppLockEnabled;
        _settings.AppLockSalt = clone.AppLockSalt;
        _settings.AppLockHash = clone.AppLockHash;
        _settings.AppLockIterations = clone.AppLockIterations;
        _settings.AppLockOnTrayHide = clone.AppLockOnTrayHide;
        _settings.Sanitized();
        if (_webReady)
        {
            // #68 live apply: the value is shared per WebView2
            // profile, so setting it on the running view covers the
            // profile (see DECISIONS).
            Browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = _settings.PasswordAutosaveEnabled;
            Browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = _settings.GeneralAutofillEnabled;
        }
        Topmost = _settings.AlwaysOnTop;
        PinItem.IsChecked = _settings.AlwaysOnTop;
        Autostart.Sync(_settings.StartWithWindows);
        // S5 #43/#44/#45/#46 take effect immediately on OK.
        ThemeManager.Apply(_settings);
        ApplyToolbarLayout();
        ShellTextScaling.Apply(this, _settings.ShellFontScale);
        if (_webReady)
            Browser.ZoomFactor = EffectiveZoom();
        if (!_settings.SuspendWhenInTray)
            _tray.CancelPendingSuspend();
        SaveSettings();
        RefreshProfileIndicator();

        // Delete the data folders of profiles removed in the dialog. When
        // a folder can't be deleted now — it's the one this process is
        // running (WebView2 holds it open), or the delete fails — the id
        // is queued and the NEXT launch deletes it before any WebView2
        // environment exists (S3 #28).
        var queuedAny = false;
        foreach (var id in removedProfileIds)
        {
            if (AppPaths.ProfileDir(id) == _profileDir)
            {
                _log.Info($"Profile {id} removed while running; folder deletion deferred to next launch");
                Profiles.QueuePendingDeletion(_settings, id);
                queuedAny = true;
                continue;
            }
            try
            {
                var dir = AppPaths.ProfileDir(id);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                _log.Info($"Removed profile {id}; deleted its data folder");
            }
            catch (Exception ex)
            {
                _log.Warn($"Removing profile {id} folder failed (deferred to next launch): {ex.Message}");
                Profiles.QueuePendingDeletion(_settings, id);
                queuedAny = true;
            }
        }
        if (queuedAny)
        {
            try { SettingsStore.Save(AppPaths.SettingsPath, _settings); }
            catch (Exception ex) { _log.Warn($"Saving deferred-deletion queue failed: {ex.Message}"); }
        }
        _log.Info("Settings updated");
    }

    /// <summary>
    /// Profile switching restarts the app: a WebView2 environment is bound to
    /// its data folder for the process lifetime. Persists the dialog's
    /// profile state, relaunches detached after a short delay (the single-
    /// instance mutex frees as this process exits), and exits.
    /// </summary>
    private void SwitchToProfile(AppSettings workingClone, string targetProfileId)
    {
        _settings.Profiles = workingClone.Profiles;
        _settings.ActiveProfileId = targetProfileId;
        _settings.Sanitized();
        SaveSettings();
        _log.Info($"Switching to profile {targetProfileId}; restarting");
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                Process.Start(new ProcessStartInfo(
                    "cmd.exe",
                    $"/c timeout /t 2 /nobreak > NUL & start \"\" \"{exe}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Profile-switch relaunch failed (start the app manually): {ex.Message}");
        }
        ExitApp();
    }

    /// <summary>
    /// The ⋯ menu / tray "Switch profile" action (S3 #25): confirms, then
    /// runs the exact same persist-and-restart flow as the Settings
    /// switch. The clone argument SwitchToProfile expects is the live
    /// settings here — it only reads the profile list from it.
    /// </summary>
    private void SwitchToProfileFromMenu(string targetProfileId)
    {
        var target = _settings.Profiles.FirstOrDefault(p => p.Id == targetProfileId);
        if (target is null || targetProfileId == _settings.ActiveProfileId)
            return;
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            $"Switch to profile \"{target.Name}\"?\n\n" +
            "MuseDesktop restarts now with that profile's sign-in.",
            "Switch profile", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        SwitchToProfile(_settings, targetProfileId);
    }

    /// <summary>Rebuild the ⋯ menu's Switch-profile submenu (S3 #25): active checked + disabled, accent dot as a real colored ellipse.</summary>
    private void RebuildSwitchProfileMenu()
    {
        SwitchProfileItem.Items.Clear();
        SwitchProfileItem.IsEnabled = _settings.Profiles.Count > 1;
        foreach (var p in _settings.Profiles)
        {
            var id = p.Id;
            var item = new MenuItem
            {
                Header = Profiles.MenuLabel(p),
                IsChecked = id == _settings.ActiveProfileId,
                IsEnabled = id != _settings.ActiveProfileId,
            };
            if (ProfileColors.HexFor(p.DisplayColor) is { } hex)
            {
                item.Icon = new System.Windows.Shapes.Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)),
                };
            }
            item.Click += (_, _) => SwitchToProfileFromMenu(id);
            SwitchProfileItem.Items.Add(item);
        }
    }

    /// <summary>Shows the active profile name in the ⋯ menu and tray tooltip when more than one profile exists.</summary>
    private void RefreshProfileIndicator()
    {
        var active = _settings.Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfileId);
        var multi = _settings.Profiles.Count > 1;
        ProfileItem.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        ProfileSeparator.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        if (multi && active is not null)
            ProfileItem.Header = $"Profile: {Profiles.MenuLabel(active)}";
        _tray.SetText(multi && active is not null ? $"MuseDesktop — {Profiles.MenuLabel(active)}" : "MuseDesktop");
        // S4 #37: the tray icon carries the active profile's tint.
        _tray.SetProfileColor(active?.DisplayColor);
        RebuildSwitchProfileMenu();
        ApplyWindowTitle();
        // S4 #33: the Jump List mirrors the profile list.
        JumpListBuilder.Apply(_settings, _log);
    }

    // ───────────────────────── context menu (v0.3.0) ─────────────────────────

    /// <summary>
    /// Adds link actions to WebView2's own context menu (which stays
    /// enabled): right-clicking a link gains "Open link in browser" —
    /// the same external-browser route the link policy uses — and
    /// "Copy link address".
    /// </summary>
    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        try
        {
            var target = e.ContextMenuTarget;
            if (!target.HasLinkUri || string.IsNullOrWhiteSpace(target.LinkUri))
                return;
            var linkUri = target.LinkUri;
            var env = Browser.CoreWebView2.Environment;

            var openItem = env.CreateContextMenuItem(
                "Open link in browser", null, CoreWebView2ContextMenuItemKind.Command);
            openItem.CustomItemSelected += (_, _) => OpenExternal(linkUri);

            var copyItem = env.CreateContextMenuItem(
                "Copy link address", null, CoreWebView2ContextMenuItemKind.Command);
            copyItem.CustomItemSelected += (_, _) =>
            {
                try { System.Windows.Clipboard.SetText(linkUri); }
                catch { /* clipboard busy — nothing sensible to do */ }
            };

            e.MenuItems.Insert(0, openItem);
            e.MenuItems.Insert(1, copyItem);
        }
        catch (Exception ex)
        {
            _log.Warn($"Context menu extension failed: {ex.Message}");
        }
    }

    // ─────────────────── v0.11.0 Slice 7 (privacy & security) ───────────────────

    /// <summary>The host of a URL for the privacy counters ("" when unparsable — the counter then reads as scheme-only).</summary>
    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    /// <summary>The scheme of a URL for the privacy counters ("unknown" when unparsable).</summary>
    private static string SchemeOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Scheme : "unknown";

    /// <summary>
    /// Count one blocked thing for the privacy report (#67) and persist
    /// immediately — the file is tiny and blocks are rare, so durability
    /// beats batching. Counts + host only, by construction of the model.
    /// </summary>
    private void RecordPrivacy(PrivacyEventCategory category, string kind, string? host)
    {
        try
        {
            _privacyStats.Record(category, kind, host, DateTimeOffset.UtcNow);
            PrivacyStatsStore.Save(AppPaths.PrivacyStatsPath, _privacyStats);
        }
        catch (Exception ex)
        {
            _log.Warn($"Privacy report save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The gateway's callback after the RUNNING profile was cleared
    /// (#61): a cookies/all clear signed this profile out, so land on
    /// the home page like the old all-or-nothing clear did; a cache
    /// clear needs no navigation.
    /// </summary>
    private void OnActiveProfileCleared(ClearDataChoice choice)
    {
        if (choice == ClearDataChoice.Cache || !_webReady)
            return;
        Browser.CoreWebView2.Navigate(UrlPolicy.HomeUrl);
        SetStatus("Browsing data cleared — sign in to Muse to continue");
    }

    /// <summary>
    /// Certificate errors (#69): ask, every time, in plain language.
    /// "Continue anyway" is remembered per host for THIS session only
    /// (WebView2 also caches an AlwaysAllow for the session; the app's
    /// own cache mirrors it so repeat errors don't re-ask) — nothing
    /// is persisted, so a restart forgets it. The default is Go back.
    /// </summary>
    private void OnServerCertificateErrorDetected(
        object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var host = HostOf(e.RequestUri) ?? "(unknown host)";
            if (_certDecisions.TryGetValue(host, out var remembered))
            {
                e.Action = remembered
                    ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
                    : CoreWebView2ServerCertificateErrorAction.Cancel;
                return;
            }
            _log.Warn($"Certificate error for {host}: {e.ErrorStatus}");
            var details = "";
            try
            {
                var cert = e.ServerCertificate;
                details = $"\n\nCertificate: {cert.Subject}\nIssued by: {cert.Issuer}\n" +
                          $"Valid: {cert.ValidFrom:yyyy-MM-dd} to {cert.ValidTo:yyyy-MM-dd}";
            }
            catch { /* the explainer + error name still carry the dialog */ }
            var invoker = FocusReturn.Capture();
            var answer = System.Windows.MessageBox.Show(this,
                $"A certificate problem was found for {host}.\n\n" +
                $"{ConnectionInfo.CertificateErrorExplainer}\n\n" +
                $"{ConnectionInfo.DescribeCertificateError(e.ErrorStatus.ToString())}{details}\n\n" +
                "Go back (recommended), or continue anyway? Continuing applies to this site " +
                "for the rest of this session only — restarting MuseDesktop forgets it.",
                "Certificate warning", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            FocusReturn.Restore(invoker);
            var proceed = answer == MessageBoxResult.Yes;
            _certDecisions[host] = proceed;
            _log.Info(proceed
                ? $"User continued past a certificate error for {host} (this session only)"
                : $"User went back at a certificate error for {host}");
            e.Action = proceed
                ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
                : CoreWebView2ServerCertificateErrorAction.Cancel;
        }
        catch (Exception ex)
        {
            _log.Warn($"Certificate dialog failed; blocking the page: {ex.Message}");
            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>The padlock dialog (#69): the current connection's facts, from the ⋯ menu.</summary>
    private async void OnConnectionSecurity(object sender, RoutedEventArgs e)
    {
        var url = _webReady ? Browser.Source?.ToString() : null;
        var lines = ConnectionInfo.SecurityLine(url);
        var version = _privacyGateway.RuntimeVersion;
        lines += $"\n\nWebView2 runtime: {version ?? "(unknown)"}";
        var continuedHosts = _certDecisions.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        if (continuedHosts.Count > 0)
        {
            lines += $"\n\nThis session, you chose to continue past certificate warnings for: " +
                     $"{string.Join(", ", continuedHosts)}. Restarting MuseDesktop forgets those choices.";
        }
        var canForget = continuedHosts.Count > 0 && _webReady;
        var invoker = FocusReturn.Capture();
        var answer = System.Windows.MessageBox.Show(this, lines, "Connection security",
            canForget ? MessageBoxButton.YesNo : MessageBoxButton.OK,
            MessageBoxImage.Information);
        FocusReturn.Restore(invoker);
        if (canForget && answer == MessageBoxResult.Yes)
        {
            try
            {
                await Browser.CoreWebView2.ClearServerCertificateErrorActionsAsync();
                _certDecisions.Clear();
                _log.Info("Session certificate-error choices forgotten from the connection dialog");
                SetStatus("Certificate choices forgotten — you'll be asked again");
            }
            catch (Exception ex)
            {
                _log.Warn($"Forgetting certificate choices failed: {ex.Message}");
            }
        }
    }

    /// <summary>The log viewer (#66): the log itself plus its redaction audit.</summary>
    private void OnViewLog(object sender, RoutedEventArgs e)
    {
        var dialog = new LogViewerWindow { Owner = this };
        dialog.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(dialog);
        ShellTextScaling.Apply(dialog, _settings.ShellFontScale);
        FocusReturn.ShowDialog(dialog);
    }

    // ─────────────────── #64 app lock ───────────────────
    //
    // The lock gates SURFACES, not the engine: the window hides, every
    // other window closes, the tray goes plain, and downloads in
    // flight keep running behind the lock. WebView2 is never touched
    // by a mid-session lock; at startup it isn't even created until
    // unlock (App's gate for visible launches; the deferred-init path
    // below for tray launches).

    /// <summary>"Lock now" (⋯ menu + tray menu): engage the lock and show the lock screen.</summary>
    private void LockNow()
    {
        if (_isLocked || !AppLock.IsLockActive(_settings))
            return;
        EngageLock(presentUi: true);
    }

    private void OnLockNow(object sender, RoutedEventArgs e) => LockNow();

    /// <summary>
    /// Engage the lock: hide the window (through the tray path, so the
    /// suspend timer arms as usual), close every other window (a
    /// secondary window or the Downloads window must not stay readable
    /// behind the lock), and flip the tray to its locked presentation.
    /// </summary>
    private void EngageLock(bool presentUi)
    {
        if (_isLocked)
            return;
        _isLocked = true;
        _log.Info("App locked");
        foreach (var other in System.Windows.Application.Current.Windows.OfType<Window>().ToList())
        {
            if (other == this || other is LockWindow)
                continue;
            try { other.Close(); }
            catch (Exception ex) { _log.Warn($"Closing a window for the app lock failed: {ex.Message}"); }
        }
        _tray.SetLocked(true);
        if (IsVisible)
        {
            Hide();
            _tray.OnHidden(showBalloon: false);
        }
        if (presentUi)
            PresentLockWindow();
    }

    /// <summary>Show (or re-focus) the lock screen. Closing it without unlocking leaves the app locked in the tray.</summary>
    private void PresentLockWindow()
    {
        if (_lockWindow is not null)
        {
            _lockWindow.Activate();
            return;
        }
        if (!AppLock.TryGetRecord(_settings, out var record) || record is null)
        {
            // The record vanished under us (settings edited while
            // running): fail open rather than strand the user.
            _log.Warn("App lock record missing while locked — unlocking (fail-open)");
            _isLocked = false;
            _tray.SetLocked(false);
            ShowFromTray();
            return;
        }
        var window = new LockWindow(record, _lockThrottle, exitOnClose: false) { Owner = this };
        ShellTextScaling.Apply(window, _settings.ShellFontScale);
        _lockWindow = window;
        window.Closed += (_, _) =>
        {
            _lockWindow = null;
            if (window.Unlocked)
                OnAppUnlocked();
            // Not unlocked: the app stays in the tray, locked — the
            // tray icon reopens this screen (ShowFromTray routes here).
        };
        window.Show();
        window.Activate();
    }

    /// <summary>Unlock: restore the tray presentation, run the deferred startup init if this was a locked launch, and show the window.</summary>
    private async void OnAppUnlocked()
    {
        _isLocked = false;
        _tray.SetLocked(false);
        _log.Info("App unlocked");
        if (_initDeferred)
        {
            _initDeferred = false;
            await InitWebViewAsync();
            MaybeAutoCheckForUpdates();
        }
        ShowFromTray();
    }

    // ───────────────────────── update check (v0.2.0) ─────────────────────────

    private void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        // S9 #81: once an update is known, the ⋯ item opens the in-app
        // update window (download + verify + install, or snooze). The
        // release page remains a button inside that window.
        if (_updateRelease is not null)
        {
            var invoker = FocusReturn.Capture();
            var window = new UpdateWindow(_updateRelease, _settings, _log, SaveSettings, ExitApp)
            { Owner = this };
            ShellTextScaling.Apply(window, _settings.ShellFontScale);
            window.ShowDialog();
            FocusReturn.Restore(invoker);
            return;
        }
        _ = CheckForUpdatesAsync(manual: true);
    }

    /// <summary>
    /// The automatic check's gate (v0.13.0, S9 #83): safe mode and the
    /// master switch first (unchanged), then Core's schedule — the
    /// frequency since the last check plus any active snooze. Manual
    /// checks never pass through here: the user asked, so they run.
    /// </summary>
    private void MaybeAutoCheckForUpdates()
    {
        if (!SafeModePolicy.ShouldCheckForUpdates(_safeMode, _settings.CheckForUpdates))
            return;
        if (!UpdateSchedule.ShouldAutoCheck(
                DateTimeOffset.UtcNow, updatesEnabled: true, _settings.UpdateCheckFrequency,
                _settings.LastUpdateCheckUtc, _settings.UpdateSnoozedUntilUtc))
            return;
        _ = CheckForUpdatesAsync(manual: false);
    }

    /// <summary>
    /// Runs the update check (UpdateService) and applies the outcome to
    /// the menu and status bar. Manual checks report every outcome;
    /// the automatic launch check only speaks up when an update exists.
    /// Every run stamps LastUpdateCheckUtc (the frequency gate's input).
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingUpdate)
            return;
        _checkingUpdate = true;
        try
        {
            if (manual)
                SetStatus("Checking for updates…");
            _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
            var outcome = await UpdateService.CheckAsync(version, _settings.UpdateChannel, _log);
            switch (outcome.Kind)
            {
                case UpdateOutcomeKind.Available:
                    _updateTag = outcome.Tag;
                    _updateUrl = outcome.Url;
                    _updateRelease = outcome.Release;
                    UpdateItem.Header = $"⬆ Update available: {outcome.Tag}";
                    UpdateItem.ToolTip = "Download and install the update inside MuseDesktop";
                    SetStatus($"MuseDesktop {outcome.Tag} is available — see the ⋯ menu");
                    // S6 #54: screen readers hear the update notice too.
                    Announcer.Polite(AnnouncementText.UpdateAvailable(outcome.Tag ?? "new version"));
                    break;
                case UpdateOutcomeKind.UpToDate:
                    if (manual) SetStatus($"You're up to date (v{version.ToString(3)})");
                    break;
                case UpdateOutcomeKind.Unreachable:
                    if (manual) SetStatus("Couldn't check for updates — GitHub didn't answer");
                    break;
                case UpdateOutcomeKind.BadData:
                    if (manual) SetStatus("Couldn't read the update information");
                    break;
                default:
                    if (manual) SetStatus("Couldn't check for updates — are you offline?");
                    break;
            }
            // Persist the check stamp (and anything the update window
            // changed) — SaveSettings is the window's ordinary path.
            SaveSettings();
        }
        finally
        {
            _checkingUpdate = false;
        }
    }


    // ─────────────────── v0.5.0 Slice 1 (items 1–12) ───────────────────

    // #1 Find in page — CoreWebView2.Find (pinned SDK 1.0.4258.31).
    /// <summary>
    /// Run a remappable action (S6 #53): if the pressed gesture matches
    /// an action's effective gesture (the user's override or the
    /// default), run it and report handled. Settings writes overrides
    /// in Core's canonical text and effective gestures are parsed back,
    /// so this is a plain equality check per action.
    /// </summary>
    private bool TryRemappedShortcut(Key key, ModifierKeys modifiers)
    {
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            return false;
        var gesture = new ShortcutGesture(ToGestureModifiers(modifiers), key.ToString());
        foreach (var action in ShortcutCatalog.Remappable)
        {
            if (!ShortcutBindings.EffectiveGesture(action.Id, _settings.ShortcutOverrides).Equals(gesture))
                continue;
            switch (action.Id)
            {
                case ShortcutCatalog.SettingsId: OnSettings(this, new RoutedEventArgs()); break;
                case ShortcutCatalog.DownloadsId: OnDownloads(this, new RoutedEventArgs()); break;
                case ShortcutCatalog.HomeId: OnHome(this, new RoutedEventArgs()); break;
                default: break;
            }
            return true;
        }
        return false;
    }

    private static GestureModifiers ToGestureModifiers(ModifierKeys modifiers)
    {
        var result = GestureModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= GestureModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= GestureModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= GestureModifiers.Shift;
        return result;
    }

    private void OnFind(object sender, RoutedEventArgs e) => ShowFindBar();

    private void ShowFindBar()
    {
        FindBar.Visibility = Visibility.Visible;
        FindTextBox.Focus();
        FindTextBox.SelectAll();
        if (!string.IsNullOrEmpty(FindTextBox.Text))
            _ = StartFindAsync();
    }

    private async Task StartFindAsync()
    {
        if (!_webReady) return;
        try
        {
            var find = Browser.CoreWebView2.Find;
            var term = FindTextBox.Text;
            if (string.IsNullOrEmpty(term))
            {
                find.Stop();
                FindCountText.Text = "";
                return;
            }
            var options = Browser.CoreWebView2.Environment.CreateFindOptions();
            options.FindTerm = term;
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = true;
            await find.StartAsync(options);
            UpdateFindCount();
        }
        catch (Exception ex) { _log.Warn($"Find failed: {ex.Message}"); }
    }

    private void UpdateFindCount()
    {
        if (!_webReady || Browser.CoreWebView2 is null) return;
        var find = Browser.CoreWebView2.Find;
        if (string.IsNullOrEmpty(FindTextBox.Text)) { FindCountText.Text = ""; return; }
        if (find.MatchCount == 0) { FindCountText.Text = "No results"; return; }
        FindCountText.Text = find.ActiveMatchIndex >= 0
            ? $"{find.ActiveMatchIndex + 1} of {find.MatchCount}"
            : $"{find.MatchCount} found";
    }

    private void OnFindTextChanged(object sender, TextChangedEventArgs e) => _ = StartFindAsync();

    private void OnFindTextKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) OnFindPrevious(this, e);
            else OnFindNext(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFindBar();
            e.Handled = true;
        }
    }

    private void OnFindNext(object sender, RoutedEventArgs e)
    { if (_webReady) Browser.CoreWebView2.Find.FindNext(); }

    private void OnFindPrevious(object sender, RoutedEventArgs e)
    { if (_webReady) Browser.CoreWebView2.Find.FindPrevious(); }

    private void OnFindClose(object sender, RoutedEventArgs e) => CloseFindBar();

    private void CloseFindBar()
    {
        try { if (_webReady) Browser.CoreWebView2.Find.Stop(); } catch { /* cosmetic */ }
        FindBar.Visibility = Visibility.Collapsed;
        Browser.Focus();
    }

    // #2 Print / Save as PDF.
    private void OnPrint(object sender, RoutedEventArgs e)
    {
        if (!_webReady) return;
        try { Browser.CoreWebView2.ShowPrintUI(); }
        catch (Exception ex) { _log.Warn($"Print failed: {ex.Message}"); SetStatus("Printing isn't available right now"); }
    }

    private async void OnSaveAsPdf(object sender, RoutedEventArgs e)
    {
        if (!_webReady) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PDF file (*.pdf)|*.pdf",
            FileName = "muse-page.pdf",
            Title = "Save page as PDF",
        };
        // S6 #56: even a common dialog returns focus to the invoker.
        var invoker = FocusReturn.Capture();
        var chosen = dialog.ShowDialog(this) == true;
        FocusReturn.Restore(invoker);
        if (!chosen) return;
        try
        {
            var printSettings = Browser.CoreWebView2.Environment.CreatePrintSettings();
            var ok = await Browser.CoreWebView2.PrintToPdfAsync(dialog.FileName, printSettings);
            SetStatus(ok ? $"Saved PDF to {Path.GetFileName(dialog.FileName)}" : "Saving the PDF failed");
            if (!ok) _log.Warn("PrintToPdfAsync reported failure");
        }
        catch (Exception ex)
        {
            _log.Warn($"Save as PDF failed: {ex.Message}");
            SetStatus("Saving the PDF failed");
        }
    }

    // #4 Copy page address (Open in browser already exists as OnOpenInBrowser).
    private void OnCopyPageAddress(object sender, RoutedEventArgs e)
    {
        var src = Browser.Source?.ToString();
        if (string.IsNullOrEmpty(src)) return;
        try { System.Windows.Clipboard.SetText(src); SetStatus("Page address copied"); }
        catch { SetStatus("Couldn't copy the page address"); }
    }

    // #7 Open in new window — a secondary window on the SAME environment.
    private void OnOpenInNewWindow(object sender, RoutedEventArgs e)
    {
        if (!_webReady) return;
        var src = Browser.Source?.ToString();
        if (string.IsNullOrEmpty(src)
            || UrlPolicy.ClassifyNavigation(src) != LinkDisposition.InApp)
        {
            SetStatus("There's no in-app page to open in a new window");
            return;
        }
        try
        {
            new SecondaryWindow(Browser.CoreWebView2.Environment, _log, src) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            _log.Warn($"Open in new window failed: {ex.Message}");
            SetStatus("Couldn't open a second window");
        }
    }

    // #5 Always-on-top pin.
    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = sender is MenuItem ? PinItem.IsChecked : !_settings.AlwaysOnTop;
        PinItem.IsChecked = _settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        SetStatus(_settings.AlwaysOnTop ? "Window will stay on top" : "Window no longer stays on top");
        SaveSettings();
    }

    // #6 Focus / compact mode (F8). Session-only, never persisted.
    private void OnToggleFocusMode(object sender, RoutedEventArgs e) =>
        SetFocusMode(sender is MenuItem ? FocusItem.IsChecked : !_focusMode);

    private void SetFocusMode(bool on)
    {
        _focusMode = on;
        FocusItem.IsChecked = on;
        Toolbar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StatusBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on) SetStatus("Focus mode — press F8 or Esc to exit");
    }

    // #3 Back/Forward history dropdowns (right-click or long-press).
    private void OnBackHistoryMenu(object sender, MouseButtonEventArgs e)
    { ShowHistoryMenu(back: true, BackButton); e.Handled = true; }

    private void OnForwardHistoryMenu(object sender, MouseButtonEventArgs e)
    { ShowHistoryMenu(back: false, ForwardButton); e.Handled = true; }

    private void OnNavButtonPress(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var back = sender == BackButton;
        _navPressTimer?.Stop();
        _navPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _navPressTimer.Tick += (_, _) =>
        {
            _navPressTimer?.Stop();
            _suppressNavClick = true;
            ShowHistoryMenu(back, back ? BackButton : ForwardButton);
        };
        _navPressTimer.Start();
    }

    private void OnNavButtonRelease(object sender, MouseButtonEventArgs e) => _navPressTimer?.Stop();

    private void ShowHistoryMenu(bool back, System.Windows.FrameworkElement target)
    {
        var entries = back ? _history.BackEntries() : _history.ForwardEntries();
        if (entries.Count == 0)
        {
            SetStatus(back ? "No earlier pages in this session" : "No forward pages in this session");
            return;
        }
        var menu = new ContextMenu { PlacementTarget = target, IsOpen = true };
        foreach (var entry in entries)
        {
            var captured = entry;
            var header = !string.IsNullOrWhiteSpace(captured.Title)
                ? captured.Title
                : UrlPolicy.RedactForLog(captured.Url);
            var item = new MenuItem { Header = header, ToolTip = UrlPolicy.RedactForLog(captured.Url) };
            item.Click += (_, _) =>
            {
                _history.JumpTo(captured);
                if (_webReady) Browser.CoreWebView2.Navigate(captured.Url);
            };
            menu.Items.Add(item);
        }
    }

    // #9 Restore the last closed page (Ctrl+Shift+T).
    private void OnRestoreClosedPage(object sender, RoutedEventArgs e)
    {
        var url = _history.LastClosedPageUrl;
        if (!_webReady || string.IsNullOrEmpty(url)
            || UrlPolicy.ClassifyNavigation(url) != LinkDisposition.InApp)
        {
            SetStatus("No closed page to restore");
            return;
        }
        Browser.CoreWebView2.Navigate(url);
        SetStatus("Restored the last closed page");
    }

    private void OnDownloads(object sender, RoutedEventArgs e)
    {
        var dialog = new DownloadsWindow(_log, _downloads, RetryFailedDownloadAsync, _settings.DownloadHistoryCap)
            { Owner = this };
        // S5: transient shell windows follow the theme + text scale too
        // (their brushes resolve at parse; title bar and fonts are ours).
        dialog.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(dialog);
        ShellTextScaling.Apply(dialog, _settings.ShellFontScale);
        // S6 #56: focus returns to the invoker when the manager closes.
        FocusReturn.ShowDialog(dialog);
    }

    /// <summary>The shortcuts window (S6 #52): the Core catalog, rendered with the user's remaps applied.</summary>
    private void OnShortcuts(object sender, RoutedEventArgs e)
    {
        var dialog = new ShortcutsWindow(_settings) { Owner = this };
        FocusReturn.ShowDialog(dialog);
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.LogDir) { UseShellExecute = true });
        }
        catch (Exception ex) { _log.Warn($"Open log folder failed: {ex.Message}"); }
    }

    /// <summary>
    /// About (S10 #94): the full panel — version, channel, arch,
    /// Windows/.NET/WebView2 versions, data paths, Check for updates
    /// (routes into the S9 flow), and the licenses view. Replaces the
    /// pre-S10 MessageBox, whose text nobody could copy.
    /// </summary>
    private void OnAbout(object sender, RoutedEventArgs e)
    {
        string webViewVersion;
        try { webViewVersion = CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "not found"; }
        catch { webViewVersion = "couldn't be read"; }
        var facts = new AboutFacts(
            Version: $"{typeof(App).Assembly.GetName().Version}",
            ChannelLabel: UpdateChannelText.Label(_settings.UpdateChannel),
            Architecture: $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
            WindowsVersion: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            DotNetVersion: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            WebViewVersion: webViewVersion,
            SettingsPath: AppPaths.SettingsPath,
            LogPath: AppPaths.LogPath,
            ProfileDir: _profileDir);
        var invoker = FocusReturn.Capture();
        var dialog = new AboutWindow(facts,
            () => OnCheckForUpdates(this, new RoutedEventArgs())) { Owner = this };
        ThemeManager.ApplyTitleBar(dialog);
        ShellTextScaling.Apply(dialog, _settings.ShellFontScale);
        FocusReturn.ShowDialog(dialog);
        FocusReturn.Restore(invoker);
    }

    /// <summary>
    /// Help &amp; troubleshooting (S10 #91): the offline pages shipped
    /// as Help.md beside the exe. A page's action routes back here —
    /// open Settings at the section the fix lives in, open
    /// Diagnostics, or open the log viewer — and the Help window
    /// stays open behind the target so the steps remain readable
    /// afterwards where focus allows.
    /// </summary>
    private void OnHelp(object sender, RoutedEventArgs e)
    {
        string markdown;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Help.md");
            markdown = File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception ex)
        {
            _log.Warn($"Help file couldn't be read: {ex.Message}");
            markdown = "";
        }
        var invoker = FocusReturn.Capture();
        var dialog = new HelpWindow(markdown, HandleHelpAction) { Owner = this };
        ThemeManager.ApplyTitleBar(dialog);
        ShellTextScaling.Apply(dialog, _settings.ShellFontScale);
        dialog.ShowDialog();
        FocusReturn.Restore(invoker);
    }

    private void HandleHelpAction(HelpPage page)
    {
        switch (page.Action)
        {
            case HelpAction.OpenSettings:
                OpenSettings(page.SettingsSectionId);
                break;
            case HelpAction.OpenDiagnostics:
                OnDiagnostics(this, new RoutedEventArgs());
                break;
            case HelpAction.OpenLogViewer:
                OnViewLog(this, new RoutedEventArgs());
                break;
        }
    }

    /// <summary>The current log file's text ("" when missing/unreadable) — the one read every support surface shares (S10 #89/#90/#93).</summary>
    private string ReadLogText()
    {
        try { return File.Exists(AppPaths.LogPath) ? File.ReadAllText(AppPaths.LogPath) : ""; }
        catch (Exception ex)
        {
            _log.Warn($"Log read failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>Open the log file itself in the default text editor (S10 #93) — the Diagnostics window's "Open today's log".</summary>
    private void OpenTodaysLog()
    {
        try
        {
            if (!File.Exists(AppPaths.LogPath))
            {
                SetStatus("There's no log file yet");
                return;
            }
            Process.Start(new ProcessStartInfo(AppPaths.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex) { _log.Warn($"Open today's log failed: {ex.Message}"); }
    }

    /// <summary>
    /// Diagnostics (S8): the lite readout surface — runtime health
    /// (#78), Windows proxy (#77), memory (#74 incl. the suspend
    /// pair), and startup timings (#73) — composed from the Core
    /// models each time it opens or refreshes, so S10's fuller
    /// diagnostics can reuse the same pieces.
    /// </summary>
    private void OnDiagnostics(object sender, RoutedEventArgs e)
    {
        var invoker = FocusReturn.Capture();
        // S9 #84: the rollback offer rides along when the updater has
        // a usable previous installer (kept, on-disk, this arch).
        var rollbackPath = RollbackOffer.UsableInstallerPath(
            _settings.PreviousInstaller, AppPaths.UpdatesDir, UpdateService.ArchToken, File.Exists);
        var rollbackLabel = rollbackPath is not null && _settings.PreviousInstaller is not null
            ? RollbackOffer.Describe(_settings.PreviousInstaller)
            : null;
        DiagnosticsWindow window = null!;
        window = new DiagnosticsWindow(
            () => DiagnosticsReport.Compose(BuildDiagnosticsData()),
            rollbackLabel,
            rollbackPath is not null
                ? () => ReinstallPreviousVersion(rollbackPath, _settings.PreviousInstaller!.Version)
                : null,
            // S10 #90/#93: the support actions.
            onExportBundle: () => StartSupportBundleExport(window),
            onOpenLog: OpenTodaysLog,
            lastErrorProvider: () => LogReading.LastError(ReadLogText()))
        { Owner = this };
        ThemeManager.ApplyTitleBar(window);
        ShellTextScaling.Apply(window, _settings.ShellFontScale);
        window.ShowDialog();
        FocusReturn.Restore(invoker);
    }

    /// <summary>
    /// Roll back (S9 #84): run the kept previous installer after a
    /// plain confirm — it reinstalls that version in place (sign-in
    /// and settings carry over, as with any in-place install), then
    /// the app exits so setup can take over. The kept-installer
    /// records are NOT rotated by a rollback: the newer installer
    /// stays in the current slot.
    /// </summary>
    private void ReinstallPreviousVersion(string installerPath, string version)
    {
        var answer = System.Windows.MessageBox.Show(this,
            $"Reinstall MuseDesktop {version}?\n\n" +
            "The installer for the previous version will run and replace this version in place. " +
            "Your sign-in, profiles, and settings are kept (the same as any in-place install). " +
            "MuseDesktop will close while it installs.",
            "Reinstall previous version", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
            _log.Info($"Rollback: previous-version installer launched ({version}) — exiting so setup can take over");
        }
        catch (Exception ex)
        {
            _log.Warn($"Rollback installer launch failed: {ex.Message}");
            System.Windows.MessageBox.Show(this,
                "The previous version's installer couldn't be started. It is kept in the updates folder:\n" +
                installerPath,
                "Reinstall previous version", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ExitApp();
    }

    private DiagnosticsData BuildDiagnosticsData()
    {
        MemorySnapshot? currentMemory = null;
        if (_webReady)
        {
            try { currentMemory = MemoryCollector.Collect(Browser.CoreWebView2.Environment); }
            catch (Exception ex) { _log.Warn($"Diagnostics memory collection failed: {ex.Message}"); }
        }
        List<StartupTimings> runs = new();
        try { runs = StartupTimingsStore.Load(AppPaths.StartupTimingsPath); }
        catch (Exception ex) { _log.Warn($"Diagnostics startup-timings read failed: {ex.Message}"); }
        var logText = ReadLogText();
        return new DiagnosticsData
        {
            AppVersion = $"{typeof(App).Assembly.GetName().Version}",
            SafeMode = _safeMode,
            SettingsPath = AppPaths.SettingsPath,
            LogPath = AppPaths.LogPath,
            ProfileDir = _profileDir,
            Runtime = _runtimeHealth ?? RuntimeHealthCheck.EvaluateCurrent(),
            Proxy = ProxyReader.Read(),
            Memory = currentMemory,
            MemoryBeforeSuspend = _memBeforeSuspend,
            MemoryAfterResume = _memAfterResume,
            StartupRuns = runs,
            // S10 #89: the full-panel facts.
            Architecture = $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
            WindowsVersion = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            DotNetVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ActiveProfileName = _settings.Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfileId)?.Name
                                ?? _settings.ActiveProfileId,
            UpdateChannelLabel = UpdateChannelText.Label(_settings.UpdateChannel),
            AppLockOn = AppLock.IsLockActive(_settings),
            DataSizes = new (string, long?)[]
            {
                ("Active profile data", ProfileDataInfo.MeasureFolderBytes(_profileDir)),
                ("Settings & app data", ProfileDataInfo.MeasureFolderBytes(AppPaths.RootDir)),
            },
            RecentErrors = LogReading.RecentErrors(logText, 5),
        };
    }

    // ───────────────────────── support bundle (S10 #90/#95) ─────────────────────────

    private void OnExportSupportBundle(object sender, RoutedEventArgs e) =>
        StartSupportBundleExport(this);

    /// <summary>
    /// Export the support bundle: preview first (exactly what's in
    /// it, plus the opt-in screenshot offer), then a save dialog,
    /// then the zip. The zip's contents are Core's
    /// <see cref="SupportBundle"/> set: manifest, diagnostics, the
    /// REDACTED log tail, bundle-sanitized settings (no lock record,
    /// no full URLs), and download history. Nothing is uploaded; the
    /// file lands where the user saves it.
    /// </summary>
    private void StartSupportBundleExport(Window owner)
    {
        // The screenshot renders THIS window's own visual tree, so it
        // needs the window visible and unlocked — a locked window's
        // content never enters a bundle, by construction.
        var screenshotAvailable = IsVisible && !_isLocked && ActualWidth > 0 && ActualHeight > 0;
        var unavailableReason = _isLocked
            ? "No screenshot while the app is locked — unlock first if you want one."
            : "The MuseDesktop window isn't visible right now, so there's nothing to capture.";
        var preview = new SupportBundlePreviewWindow(
            screenshotAvailable,
            screenshotAvailable ? null : unavailableReason,
            CaptureMainWindowPng)
        { Owner = owner };
        ThemeManager.ApplyTitleBar(preview);
        ShellTextScaling.Apply(preview, _settings.ShellFontScale);
        if (preview.ShowDialog() != true || !preview.Confirmed)
            return;

        var save = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save support bundle",
            FileName = SupportBundle.DefaultFileName(DateTimeOffset.Now),
            Filter = "Zip archive (*.zip)|*.zip",
            InitialDirectory = AppPaths.DownloadsHint,
        };
        if (save.ShowDialog(owner) != true)
            return;

        var includeScreenshot = preview.IncludeScreenshot;
        try
        {
            var version = $"{typeof(App).Assembly.GetName().Version}";
            var downloadsJson = File.Exists(AppPaths.DownloadsHistoryPath)
                ? File.ReadAllText(AppPaths.DownloadsHistoryPath)
                : "[]";
            using (var stream = new FileStream(save.FileName, FileMode.Create, FileAccess.Write))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                WriteBundleEntry(zip, SupportBundle.ManifestFile,
                    SupportBundle.ComposeManifest(version, DateTimeOffset.Now, includeScreenshot));
                WriteBundleEntry(zip, SupportBundle.DiagnosticsFile,
                    DiagnosticsReport.Compose(BuildDiagnosticsData()));
                WriteBundleEntry(zip, SupportBundle.LogFile,
                    SupportBundle.LogExcerpt(ReadLogText()));
                WriteBundleEntry(zip, SupportBundle.SettingsFile,
                    SupportBundle.SettingsJson(_settings));
                WriteBundleEntry(zip, SupportBundle.DownloadsFile, downloadsJson);
                if (includeScreenshot && preview.ScreenshotPng is { } png)
                {
                    var entry = zip.CreateEntry(SupportBundle.ScreenshotFile);
                    using var entryStream = entry.Open();
                    entryStream.Write(png, 0, png.Length);
                }
            }
            var fileCount = SupportBundle.Contents(includeScreenshot).Count;
            _log.Info($"Support bundle exported: {Path.GetFileName(save.FileName)} ({fileCount} files, screenshot {(includeScreenshot ? "included" : "not included")})");
            SetStatus("Support bundle saved");
            Announcer.Polite("Support bundle saved.");
        }
        catch (Exception ex)
        {
            _log.Error($"Support bundle export failed: {ex.Message}");
            System.Windows.MessageBox.Show(owner,
                "The support bundle couldn't be written. The log has the details; the Diagnostics report's Copy button is the fallback.",
                "Export support bundle", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void WriteBundleEntry(System.IO.Compression.ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    /// <summary>
    /// Render THIS window's own visual tree to a PNG (S10 #95).
    /// RenderTargetBitmap draws the tree itself, not the screen, so
    /// other windows and the desktop cannot appear — and the caller
    /// shows the result for review before it enters any bundle.
    /// </summary>
    private byte[]? CaptureMainWindowPng()
    {
        try
        {
            var width = (int)Math.Ceiling(ActualWidth);
            var height = (int)Math.Ceiling(ActualHeight);
            if (width <= 0 || height <= 0)
                return null;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception ex)
        {
            _log.Warn($"Window screenshot capture failed: {ex.Message}");
            return null;
        }
    }

    private static void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* no default browser configured etc. — nothing sensible to do */ }
    }

    // ───────────────────────── keyboard & full screen ─────────────────────────

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FindBar.Visibility == Visibility.Visible)
        {
            CloseFindBar();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F8)
        {
            SetFocusMode(!_focusMode);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _focusMode)
        {
            SetFocusMode(false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F11)
        {
            SetFullScreen(!_isFullScreen);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _isFullScreen)
        {
            SetFullScreen(false);
            e.Handled = true;
            return;
        }
        // S6 #52/#53: normalized key (Alt-held keys arrive as Key.System
        // with the real key in SystemKey) and the remappable actions.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        // "?" alone opens the shortcuts list too — but never while the
        // user is typing (in the page or in a shell text box).
        if (key == Key.OemQuestion && modifiers == ModifierKeys.Shift
            && !Browser.IsKeyboardFocusWithin
            && Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            OnShortcuts(this, e);
            e.Handled = true;
            return;
        }
        // Remappable actions dispatch on their EFFECTIVE gesture before
        // the fixed bindings. Core guarantees an override can never
        // equal a fixed gesture, so this cannot shadow a built-in; and
        // every effective gesture carries Ctrl or Alt, so plain typing
        // in the find box can't trigger one.
        if (TryRemappedShortcut(key, modifiers))
        {
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.R: OnReload(this, e); e.Handled = true; break;
                case Key.F: ShowFindBar(); e.Handled = true; break;
                case Key.P: OnPrint(this, e); e.Handled = true; break;
                case Key.T when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                    OnRestoreClosedPage(this, e); e.Handled = true; break;
                case Key.OemQuestion: OnShortcuts(this, e); e.Handled = true; break;
                case Key.OemPlus or Key.Add: OnZoomIn(this, e); e.Handled = true; break;
                case Key.OemMinus or Key.Subtract: OnZoomOut(this, e); e.Handled = true; break;
                case Key.D0 or Key.NumPad0:
                    if (_webReady) Browser.ZoomFactor = 1.0;
                    e.Handled = true; break;
            }
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            if (e.Key == Key.Left) { OnBack(this, e); e.Handled = true; }
            if (e.Key == Key.Right) { OnForward(this, e); e.Handled = true; }
        }
        if (e.Key == Key.F5) { OnReload(this, e); e.Handled = true; }
    }

    private void SetFullScreen(bool on)
    {
        if (on == _isFullScreen) return;
        if (on)
        {
            _preFullScreenState = WindowState;
            _preFullScreenStyle = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            Toolbar.Visibility = Visibility.Collapsed;
        }
        else
        {
            WindowStyle = _preFullScreenStyle;
            WindowState = _preFullScreenState;
            Toolbar.Visibility = _focusMode ? Visibility.Collapsed : Visibility.Visible;
        }
        _isFullScreen = on;
    }

    // ───────────────────────── title bar ─────────────────────────
    // Since S5 the title bar follows the shell theme (dark OR light);
    // the DWM call itself lives in ThemeManager with the other theming.

    private void ApplyDarkTitleBar() => ThemeManager.ApplyTitleBar(this);
}
