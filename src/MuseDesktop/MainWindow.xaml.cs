using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    // R6 #154/#156: the mini window's state (open strip + a flag so
    // its Closed handler can tell "user closed the strip" from "the
    // app closed it"), the page favicon's PNG bytes mirrored for the
    // strip, and this window's session-only opacity (never persisted;
    // Core's policy decides what may actually render).
    private MiniWindow? _miniWindow;
    private bool _miniMode;
    private byte[]? _faviconPng;
    private double _windowOpacity = 1.0;
    private bool _webInitFailed;
    // R5B #202: the snippet a bookmark jump is hunting, set when the
    // jump navigates and consumed by the next completed navigation.
    private string? _pendingBookmarkSnippet;
    // R7B: the token ring's current reading (null = no reading yet),
    // its refresh timer, and the in-flight guard (a probe is a page
    // round-trip; overlapping probes would only race the renderer).
    private TokenUsage? _tokenUsage;
    private DispatcherTimer? _tokenTimer;
    private bool _tokenRefreshBusy;
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

    // v1.4.0 (R4) — downloads round 2: downloads held by the quiet-hours
    // gate (#132; URIs in memory only, the S2 #15 discipline — exiting
    // forgets them), the batch queue (#134), auto-retry attempt counts
    // per target path (#139), the bandwidth governor (#132), and the
    // 5-second pump that services all of it.
    private readonly List<(string Uri, string FileName, DateTimeOffset NextChanceUtc)> _heldDownloads = new();
    private readonly Queue<BatchLink> _batchQueue = new();
    private readonly Dictionary<string, int> _autoRetryAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly BandwidthGovernor _bandwidthGovernor = new();
    private DispatcherTimer? _downloadPumpTimer;
    private bool _downloadPumpBusy;

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

    // R3 #122/#123 — the per-profile PIN gate for hidden launches
    // (App hands over the launch profile's record; the first real
    // show presents it, after the global lock) and the profile
    // schedule's minute timer. The profile throttle is separate from
    // the app lock's: they are different credentials.
    private AppLockRecord? _pendingProfileLockRecord;
    private readonly AppLockThrottle _profileLockThrottle = new();
    private LockWindow? _profileLockWindow;
    private DispatcherTimer? _scheduleTimer;
    private bool _schedulePromptOpen;

    // v1.0.0 (S11) — the global show/hide hotkey (#96: its manager
    // owns the RegisterHotKey registration on this window's handle),
    // the launch intent queued behind the lock (#99/#100: one slot,
    // last wins), and the drop-hint/focus state of the web frame ring
    // (#98 + the S6 #58 focus ring share the one border).
    private GlobalHotkeyManager? _hotkeyManager;
    private readonly PendingIntentQueue _pendingIntents = new();

    // v1.5.0 (R5) — focus & productivity (#141–#150): the focus timer
    // and the per-profile session timer (both Core state machines,
    // ticked from _productivityTimer), the session-only window groups
    // (#144), the command scheduler's bookkeeping (#147: startup rules
    // stay pending until the first unlocked tick), and the quick-note
    // hotkey's manager (#148 — a second registration on this handle).
    private readonly FocusTimer _focusTimer = new();
    private SessionTimer? _sessionTimer;
    private readonly WindowGroupSet _windowGroups = new();
    private GlobalHotkeyManager? _noteHotkeyManager;
    private DispatcherTimer? _productivityTimer;
    private bool _schedulerStartupPending = true;
    private DateTimeOffset _lastSchedulerCheckUtc = DateTimeOffset.MinValue;
    private int _focusMinutesDrained;
    private int _sessionMinutesDrained;
    private WindowState _focusPrevState = WindowState.Normal;
    private string _lastTimerTrayText = "";
    private bool _browserFocused;
    private bool _dropHintOn;

    // v1.1.0 (R1) — the command palette (#101), the window switcher
    // (#103), and the window tracking both stand on: secondary
    // windows register in _trackedWindows so the palette and the
    // switcher see every open window, and the MRU list remembers the
    // order the windows were last used in.
    private readonly WindowMruList _windowMru = new();
    private readonly Dictionary<Window, string> _trackedWindows = new();
    private int _secondarySeq;

    // v1.2.0 (R2) — workspaces & sessions (#111–#120): the session
    // journal this launch maintains (rewritten as the arrangement
    // changes; its CleanExit flag is the next launch's crash
    // evidence), the one-shot startup arrangement (pending apply /
    // crash restore / auto-save / startup workspace — deferred while
    // the window starts hidden, like the S11 intent queue), and the
    // active workspace's accent override (#115: session-only, applied
    // over the settings accent via a clone, never written into it).
    private SessionJournal? _journal;
    private bool _startupArrangementHandled;
    private bool _startupArrangementDeferred;
    private string? _workspaceAccentKey;

    // v0.12.0 (S8) — reliability: ONE recovery coordinator (#71/#76)
    // decides every automatic reload/recreate (the S1 network retry
    // and the ProcessFailed handler used to keep separate flags); the
    // hung-page watchdog (#80); safe mode (#72); startup timings
    // (#73); the tray-suspend memory pair (#74); runtime health (#78).
    private AnnouncementHistory _announcementHistory = new();
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
        // R6 #153: chrome density rides with the text scale (Core's
        // DensityMetrics owns the numbers).
        ApplyDensity();
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
            // R4: completion follow-ups (auto-sort #131, quota #140).
            if (record.State == DownloadRecordState.Completed)
                OnDownloadCompletedPost(record);
        }, NotifyDownloadComplete);
        _downloads.ActiveChanged += active =>
        {
            CancelDownloadButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (active) StatusText.ToolTip = "";
        };
        _downloads.Completed += () => StatusText.ToolTip = Strings.Get("ui.click_to_open_the_file");
        // S2 #19: taskbar progress mirror. A failure latches the Error
        // state until the next download starts.
        _downloads.Started += () => { _downloadError = false; UpdateTaskbarProgress(); };
        _downloads.Failed += () => { _downloadError = true; UpdateTaskbarProgress(); };
        _downloads.Changed += UpdateTaskbarProgress;
        // R4: failures feed the bounded auto-retry (#139); completions
        // and failures both free room under the batch cap (#134); the
        // pump timer services held downloads (#132), the batch queue,
        // and the bandwidth governor.
        _downloads.Failed += OnDownloadFailedAutoRetry;
        _downloads.Completed += PumpBatchQueue;
        _downloads.Failed += PumpBatchQueue;
        _downloadPumpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _downloadPumpTimer.Tick += (_, _) =>
        {
            PumpHeldDownloads();
            PumpBatchQueue();
            ApplyBandwidthGovernor();
        };
        _downloadPumpTimer.Start();

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
            id => SwitchToProfileFromMenu(id),
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
            LockNow,
            quickDictate: QuickDictate,
            openPalette: () => OpenPalette(compact: true),
            workspaces: () => _settings.Workspaces,
            activeWorkspaceId: () => _settings.ActiveWorkspaceId,
            applyWorkspace: id => ApplyWorkspaceById(id),
            profileGroups: () => _settings.ProfileGroups,
            trayIconStyle: () => _settings.TrayIconStyle,
            trayOverlayStyle: () => _settings.TrayCountOverlay);

        // S6 #54: live regions for assistive-tech announcements, and
        // the download events that feed them (the wording is Core's).
        Announcer.Register(LiveRegionPolite, LiveRegionAssertive);
        // R7 #169: the announcement center — the per-type switches
        // and the history live here now; download news is kinded so
        // the Downloads switch governs it. The wording still mirrors
        // Core's AnnouncementText (pinned against the catalog).
        _announcementHistory = AnnouncementHistoryStore.Load(AppPaths.AnnouncementHistoryPath);
        Announcer.Configure(() => _settings, _announcementHistory);
        _downloads.Completed += () =>
        {
            if (_downloads.CompletedPath is { } path)
                Announcer.Kind(AnnouncementKind.Downloads,
                    Strings.Format("announce.download.completed", Path.GetFileName(path)));
        };
        _downloads.Failed += () =>
            Announcer.Kind(AnnouncementKind.Downloads,
                _downloads.LastFailedReason is { } reason
                    ? Strings.Format("announce.download.failed_reason",
                        _downloads.LastFailedName ?? Strings.Get("announce.a_download"), reason)
                    : Strings.Format("announce.download.failed",
                        _downloads.LastFailedName ?? Strings.Get("announce.a_download")),
                assertive: true);

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
        // R2 #115/#119: the ⋯ menu's workspace list, and the active
        // workspace's accent override if the last session left one.
        RebuildWorkspacesMenu();
        // R5B #206: the ⋯ menu's snippet list, from the active profile's store.
        RebuildSnippetsMenu();
        ApplyWorkspaceAccent();
        // R3 #123: the profile schedule is re-checked once a minute
        // while the app runs — Core decides, this window only asks
        // (or, in auto mode, switches). While the app isn't running
        // nothing is evaluated; the next launch's first minute covers it.
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _scheduleTimer.Tick += (_, _) => EvaluateSchedule();
        _scheduleTimer.Start();

        // R5: the one-second productivity tick — both Core timers and
        // (about every 15 s) the command scheduler ride it.
        _productivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _productivityTimer.Tick += (_, _) => OnProductivityTick();
        _productivityTimer.Start();

        // R7B: the token ring re-reads on a slow tick (a conversation
        // grows while it sits open); event-driven refreshes (landing
        // on a conversation, sending a message) ride the same probe.
        _tokenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _tokenTimer.Tick += (_, _) => { if (IsVisible) _ = RefreshTokenUsageAsync(); };
        _tokenTimer.Start();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => _hotkeyManager?.Dispose();
        Closed += (_, _) => _noteHotkeyManager?.Dispose();
        PreviewKeyDown += OnPreviewKeyDown;
        // R1 #103: the main window is the MRU list's permanent first
        // citizen — touched here so the switcher always knows it.
        _windowMru.Touch("main");
        Activated += (_, _) => _windowMru.Touch("main");
    }

    /// <summary>
    /// S11 #96: the global hotkey hooks this window's handle — the
    /// handle that lives for the whole process (closing hides to the
    /// tray; it doesn't destroy the window), so the registration
    /// survives every hide/show cycle and is re-applied here and after
    /// each Settings OK.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hotkeyManager = new GlobalHotkeyManager(_log, OnGlobalHotkeyPressed,
            featureName: Strings.Get("hotkey.feature.global"));
        _hotkeyManager.Attach(this);
        _hotkeyManager.Apply(_settings);
        // R5 #148: the quick-note hotkey — a second registration on
        // the same handle, resolved by Core's QuickNoteHotkey (its own
        // id, its own gesture, never the show/hide combination).
        _noteHotkeyManager = new GlobalHotkeyManager(_log, OnQuickNoteHotkeyPressed,
            HotkeyNative.RegistrationIdQuickNote,
            settings => QuickNoteHotkey.Resolve(
                settings.QuickNoteHotkeyEnabled, settings.QuickNoteHotkeyGesture,
                settings.ShortcutOverrides, settings.GlobalHotkeyGesture),
            gesture => Strings.Format("hotkey.active_quick_note", gesture.DisplayText()),
            Strings.Get("hotkey.feature.quick_note"));
        _noteHotkeyManager.Attach(this);
        _noteHotkeyManager.Apply(_settings);
    }

    // S6 #58 focus-ring handlers (named so S8 #71's recreated web view
    // control gets the same pair). S11 #98: the same ring doubles as
    // the file-drag highlight — RefreshWebFocusRing owns the brush so
    // the two signals compose instead of fighting over it.
    private void OnBrowserGotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        _browserFocused = true;
        RefreshWebFocusRing();
    }

    private void OnBrowserLostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        _browserFocused = false;
        RefreshWebFocusRing();
    }

    private void RefreshWebFocusRing() =>
        WebFocusRing.BorderBrush = _dropHintOn || _browserFocused
            ? (System.Windows.Media.Brush)FindResource("AccentBrush")
            : System.Windows.Media.Brushes.Transparent;

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
        // R6: a theme (re-)application is also when the backdrop is
        // re-decided, the chrome adapts to it, density re-lands, and
        // the window opacity re-checks its safety rules.
        ThemeManager.ApplyBackdrop(this);
        ApplyChromeBackdrop();
        ApplyDensity();
        ApplyWindowOpacity();
    }

    // ───────────────────── appearance round 2 (R6) ─────────────────────

    /// <summary>
    /// R6 #153: apply Core's density measurements to the chrome —
    /// the toolbar/status row heights and their inner insets. The
    /// root grid's rows are fixed by the XAML layout: 0 = toolbar,
    /// 3 = status bar.
    /// </summary>
    private void ApplyDensity()
    {
        var metrics = DensityMetrics.For(_settings.Density);
        if (Content is System.Windows.Controls.Grid root && root.RowDefinitions.Count == 4)
        {
            root.RowDefinitions[0].Height = new GridLength(metrics.ToolbarHeight);
            root.RowDefinitions[3].Height = new GridLength(metrics.StatusBarHeight);
        }
        if (Toolbar.Child is System.Windows.Controls.DockPanel toolbarPanel)
            toolbarPanel.Margin = new Thickness(metrics.ToolbarInset, 0, metrics.ToolbarInset, 0);
        if (StatusBar.Child is System.Windows.Controls.DockPanel statusPanel)
            statusPanel.Margin = new Thickness(metrics.StatusInset, 0, metrics.StatusInset, 0);
    }

    /// <summary>
    /// R6 #151: adapt the chrome to the effective backdrop. With a
    /// material, the window background goes transparent so Mica /
    /// Acrylic shows through; Mica wants fully clear chrome, Acrylic
    /// (busier, blurrier) keeps a translucent surface tint so text
    /// stays calm. Without a material, the shared theme brushes come
    /// straight back.
    /// </summary>
    private void ApplyChromeBackdrop()
    {
        var material = ThemeManager.CurrentBackdrop;
        if (material == BackdropMaterial.None)
        {
            Background = (System.Windows.Media.Brush)FindResource("BgBrush");
            Toolbar.Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush");
            StatusBar.Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush");
            return;
        }
        Background = System.Windows.Media.Brushes.Transparent;
        var baseColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
            .ConvertFromString(ThemeManager.CurrentPalette.Surface);
        var alpha = material == BackdropMaterial.Acrylic ? (byte)165 : (byte)0;
        var chrome = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        Toolbar.Background = chrome;
        StatusBar.Background = chrome;
    }

    /// <summary>
    /// R6 #156: render this window at the opacity Core's policy
    /// allows right now (the slider's wish, vetoed by High Contrast
    /// and by the app lock), and keep the menu slider honest about
    /// when it can do anything.
    /// </summary>
    private void ApplyWindowOpacity()
    {
        Opacity = WindowOpacityPolicy.Effective(_windowOpacity, ThemeManager.HighContrastActive, _isLocked);
        if (OpacitySlider is not null)
            OpacitySlider.IsEnabled = !ThemeManager.HighContrastActive;
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _windowOpacity = e.NewValue / 100.0;
        if (OpacityLabel is not null)
            OpacityLabel.Text = $"{(int)Math.Round(e.NewValue)}%";
        ApplyWindowOpacity();
    }

    /// <summary>
    /// R6 #159: the per-minute schedule tick also minds the theme
    /// schedule — when FollowSchedule is on and the resolved light /
    /// dark has flipped since the last application, re-apply. Silent
    /// by design (no prompt, unlike the profile schedule below), so
    /// it runs before that method's guards.
    /// </summary>
    private void EvaluateThemeSchedule()
    {
        if (_settings.Theme != Core.ThemeMode.FollowSchedule)
            return;
        var light = ThemeScheduleMath.IsLightNow(_settings, DateTimeOffset.Now);
        if (light == ThemeManager.IsLight)
            return;
        _log.Info($"Theme schedule: the shell is now {(light ? "light" : "dark")}.");
        ThemeManager.Apply(_settings);
    }

    // ───────────────────────── mini mode (R6 #154) ─────────────────────────

    private void OnToggleMiniMode(object sender, RoutedEventArgs e) => ToggleMiniMode();

    /// <summary>
    /// R6 #154: shrink to the mini strip (or back). The strip is
    /// shell chrome only — no second web view; the page keeps living
    /// in this window, which hides while the strip is up. Entering
    /// mini mode while locked would leak the page title and icon
    /// past the lock, so the lock wins and nothing happens.
    /// </summary>
    public void ToggleMiniMode()
    {
        if (_isLocked)
            return;
        if (_miniMode)
        {
            ExitMiniMode(showMain: true);
            return;
        }
        _miniMode = true;
        var active = _settings.Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfileId);
        var mini = new MiniWindow(
            () => string.IsNullOrWhiteSpace(_pageTitle) ? "Muse" : _pageTitle,
            () => _faviconPng,
            () => ProfileColors.HexFor(active?.DisplayColor),
            () => MiniDownloadSummary.FromDownloads(_downloads.Snapshot()),
            PauseAllDownloads,
            ResumeAllDownloads,
            () => ExitMiniMode(showMain: true));
        _miniWindow = mini;
        mini.Closed += (_, _) =>
        {
            _miniWindow = null;
            if (!_miniMode)
                return;
            // The strip was closed directly (not via Expand): mini
            // mode ends and the main window comes back — unless the
            // app locked meanwhile (EngageLock closes the strip as
            // one of "every other window"; the lock must win).
            _miniMode = false;
            try
            {
                if (!_isLocked)
                {
                    Show();
                    Activate();
                }
            }
            catch { /* shutting down — nothing to restore */ }
        };
        mini.Show();
        Hide();
    }

    private void ExitMiniMode(bool showMain)
    {
        _miniMode = false;
        var mini = _miniWindow;
        _miniWindow = null;
        mini?.Close();
        if (showMain)
        {
            Show();
            Activate();
        }
    }

    /// <summary>R6 #154: pause everything currently transferring (the tracker's per-download pause, applied to the live set).</summary>
    private void PauseAllDownloads()
    {
        foreach (var download in _downloads.Snapshot())
        {
            if (download.State == DownloadActivityState.InProgress)
                _downloads.Pause(download.Id);
        }
    }

    /// <summary>R6 #154: resume everything paused that the browser says can resume (requests for the rest are answered NotResumable by the tracker and change nothing).</summary>
    private void ResumeAllDownloads()
    {
        foreach (var download in _downloads.Snapshot())
        {
            if (download.State == DownloadActivityState.Paused)
                _downloads.Resume(download.Id);
        }
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
        // R6 #154: showing the main window ends mini mode — the
        // strip's whole job was to stand in for it.
        if (_miniMode)
            ExitMiniMode(showMain: false);
        // R3 #122: a profile PIN deferred from a hidden launch gates
        // the first real show — after the global lock above, so the
        // global credential is always asked first.
        if (_pendingProfileLockRecord is not null)
        {
            PresentProfileLockWindow();
            return;
        }
        _tray.OnShown();
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Focus();
        // S11: an intent that queued (behind the lock, or behind a
        // hidden cold start) applies once the window is actually up.
        DrainPendingIntent();
        // R2: a hidden start's deferred startup arrangement runs at
        // the first real show, once the web view exists to hold it.
        if (_startupArrangementDeferred && _webReady)
        {
            _startupArrangementDeferred = false;
            RunStartupArrangement();
        }
    }

    /// <summary>
    /// Apply the queued launch intent, if any (S11 #99/#100). The
    /// queue is single-slot last-wins and Take clears it first, so
    /// the re-entry through ApplyLaunchIntentNow → ShowFromTray
    /// finds an empty queue and stops.
    /// </summary>
    private void DrainPendingIntent()
    {
        if (_pendingIntents.Take() is not { } queued)
            return;
        _log.Info($"Applying the queued launch intent: {DescribeIntent(queued)}");
        ApplyLaunchIntentNow(queued);
    }

    /// <summary>
    /// File a launch intent away without acting on it or showing
    /// anything (S11): a cold start that begins hidden in the tray
    /// must not pop the lock screen or a window at sign-in (the S7
    /// hidden-start contract) — the intent applies at the first real
    /// show (or right after unlock, via the same drain).
    /// </summary>
    public void QueueLaunchIntent(LaunchIntent intent)
    {
        _pendingIntents.Offer(intent);
        _log.Info($"Launch intent queued for the first show: {DescribeIntent(intent)}");
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
        // R2 #117/#118: a graceful exit marks the journal clean — the
        // next launch tells it apart from a crash by this flag alone.
        // (A hide-to-tray above is not an exit: the app is still
        // running, and the journal stays live.)
        UpdateSessionJournal(cleanExit: true);
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
            // R2 #117/#118: the session journal rides the same
            // cadence — the arrangement it records is never more than
            // one navigation stale.
            UpdateSessionJournal();
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
                Strings.Get("main.webview2_missing_title"),
                Strings.Get("main.webview2_missing_body"),
                Strings.Get("main.webview2_download"),
                () => OpenExternal(WebView2BootstrapperUrl));
            SetStatus(Strings.Get("status.webview2_runtime_missing_see_the"));
            return;
        }
        catch (Exception ex)
        {
            _webInitFailed = true;
            _log.Error($"WebView2 init failed: {ex.GetType().Name}: {ex.Message}");
            ShowOverlay(
                Strings.Get("main.could_not_start"),
                Strings.Format("main.start_failed_body", ex.Message, AppPaths.LogPath),
                Strings.Get("retry"), null);
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
        // R6 #154: the mini strip mirrors the page's favicon.
        core.FaviconChanged += OnFaviconChanged;
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
        // R5B: the page bridge (PageScripts) — the conversation
        // tools' one channel into the hosted page. Registered per
        // web view; every later document in it gets the script. A
        // failure here loses the R5B page features and nothing else:
        // the page itself is untouched either way.
        core.WebMessageReceived += OnPageWebMessage;
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.DocumentScript);
        }
        catch (Exception ex) { _log.Warn($"Page bridge install failed: {ex.Message}"); }
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
        SetStatus(_safeMode ? Strings.Get("status.safe_mode_loading_claude") : Strings.Get("status.loading_claude"));
        StartWatchdog();
        // R2: the startup arrangement (pending workspace apply /
        // crash-journal restore / startup workspace), once, on the
        // first real init — the recovery recreate passes a restore
        // URL and skips it. Dispatched so the window is fully up
        // before anything opens windows or asks a question; a hidden
        // (tray) start defers it to the first real show, so nothing
        // pops at sign-in (the S7/S11 hidden-start contract).
        if (restoreUrl is null && !_startupArrangementHandled)
        {
            if (IsVisible)
                _ = Dispatcher.BeginInvoke(new Action(RunStartupArrangement));
            else
                _startupArrangementDeferred = true;
        }

        string envVersion()
        {
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { return Strings.Get("main.unknown_version"); }
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
            SetStatus(Strings.Get("status.that_link_can_t_open_inside_musedesktop"));
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        RecordFirstNavigation();
        if (e.IsSuccess)
        {
            Overlay.Visibility = Visibility.Collapsed;
            SetStatus(Strings.Get("status.ready"));
            // S8 #71/#76: a successful load closes the recovery
            // incident and refills both automatic budgets.
            _recovery.OnPageLoaded();
            // R2 #117: the first successful load makes this session
            // "healthy" in the journal — the fact that keeps a later
            // crash restore from being suppressed as a restore loop.
            if (_journal is { BecameHealthy: false })
            {
                _journal.BecameHealthy = true;
                UpdateSessionJournal();
            }
            var src = Browser.Source?.ToString();
            if (src is not null)
                _lastGoodUrl = src;
            // S4 #35: a new document ends whatever media capture the
            // old one held, so the microphone-grant heuristic resets.
            _micGrantedThisSession = false;
            if (src is not null)
                _history.Record(src, Browser.CoreWebView2.DocumentTitle);
            SaveSettings();
            // R5B: re-assert the profile's page style on the new
            // document (the bridge's ready message is the primary
            // path; this covers a missed one), and land a pending
            // bookmark jump now that its conversation is in.
            _ = ApplyPageStyleAsync();
            // R7B: a landed page is a fresh conversation (or the
            // same one reloaded) — re-read the token ring once the
            // page has had a beat to render its messages.
            _ = RefreshTokenUsageDelayedAsync(1200);
            if (_pendingBookmarkSnippet is not null)
            {
                var snippet = _pendingBookmarkSnippet;
                _pendingBookmarkSnippet = null;
                _ = FindBookmarkedMessageAsync(snippet);
            }
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
                Strings.Get("main.couldnt_be_reached"),
                LoadFailureClassifier.MessageFor(e.WebErrorStatus.ToString()),
                Strings.Get("retry"), null);
            SetStatus(Strings.Format("status.load_failed", e.WebErrorStatus));
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
                SetStatus(Strings.Get("status.opened_link_in_your_browser"));
                break;
            default:
                _log.Warn($"Blocked new-window request: {UrlPolicy.RedactForLog(e.Uri)}");
                RecordPrivacy(PrivacyEventCategory.BlockedNewWindow, SchemeOf(e.Uri), HostOf(e.Uri));
                SetStatus(Strings.Get("status.that_link_was_blocked"));
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
            Title = Strings.Get("ui.save_download"),
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

        // R4 #132: the download quiet-hours gate runs before any
        // destination decision — a held download never starts, so there
        // is nothing to pause or move. The URI is kept in memory only
        // (the S2 #15 discipline) and the pump re-issues it when the
        // window ends; exiting the app forgets held downloads.
        var totalBytes = e.DownloadOperation.TotalBytesToReceive is { } reported ? (long)reported : (long?)null;
        var schedule = DownloadSchedule.Evaluate(
            _settings.DownloadQuietHoursEnabled,
            _settings.DownloadQuietStartMinutes,
            _settings.DownloadQuietEndMinutes,
            (long)_settings.DownloadQuietLargeFileMB * 1024L * 1024L,
            DateTimeOffset.Now,
            totalBytes);
        if (!schedule.Allowed)
        {
            string? heldUri = null;
            try { heldUri = e.DownloadOperation.Uri; }
            catch { /* an unreadable URI just means this one can't be held */ }
            e.Cancel = true;
            if (!string.IsNullOrWhiteSpace(heldUri))
            {
                _heldDownloads.Add((heldUri, suggestedName,
                    schedule.NextChance ?? DateTimeOffset.UtcNow.AddMinutes(5)));
                _log.Info($"Download held by quiet hours until {schedule.NextChance?.ToLocalTime():HH:mm}: {suggestedName}");
                SetStatus(Strings.Format("status.download_held_until", schedule.NextChance?.ToLocalTime(), suggestedName));
            }
            else
            {
                SetStatus(Strings.Format("status.download_skipped_quiet_hours", suggestedName));
            }
            return;
        }

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
                SetStatus(Strings.Get("status.download_cancelled"));
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
                            SetStatus(Strings.Get("status.download_cancelled"));
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

            // R4 #133: duplicate guard — a completed history entry with
            // the same name (and size, where known) whose file is still
            // on disk means this almost certainly downloaded before.
            // "Yes" keeps both (the collision step already renamed, or
            // the unique-name pass below does); "No" skips the download.
            // The Ask-where-to-save flow skips the guard: the user is
            // already choosing the destination consciously.
            if (_settings.DownloadDuplicateGuard)
            {
                try
                {
                    var history = DownloadHistoryStore.Load(AppPaths.DownloadsHistoryPath, _settings.DownloadHistoryCap);
                    var dupe = DuplicateDownloadGuard.FindInHistory(suggestedName, totalBytes, history);
                    if (dupe is not null && File.Exists(dupe.FullPath))
                    {
                        var answer = System.Windows.MessageBox.Show(this,
                            Strings.Format("msg.already_downloaded_body", suggestedName,
                                dupe.CompletedUtc.ToLocalTime(), DownloadText.FormatBytes(dupe.SizeBytes)),
                            Strings.Get("msg.already_downloaded"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (answer != MessageBoxResult.Yes)
                        {
                            e.Cancel = true;
                            _log.Info($"Duplicate download skipped: {suggestedName}");
                            SetStatus(Strings.Format("status.skipped_already_downloaded", suggestedName));
                            return;
                        }
                        if (string.Equals(candidate, dupe.FullPath, StringComparison.OrdinalIgnoreCase))
                            candidate = UniqueFileName.ForPath(candidate);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"Duplicate check failed, continuing: {ex.Message}");
                }
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
            SetStatus(Strings.Get("status.that_download_can_t_be_retried_anymore"));
            return;
        }
        await StartHiddenDownloadAsync(failed.Uri, failed.FileName, Strings.Get("status.verb_retrying"));
    }

    /// <summary>
    /// Navigate the hidden retry view at a URI so its DownloadStarting
    /// (this class's own handler) takes the download over with the
    /// normal destination policy — the shared engine behind session
    /// retries (S2 #15), held downloads (R4 #132), batch capture
    /// (R4 #134), and automatic retries (R4 #139). The URI is logged
    /// host+path only; it may carry a signed token.
    /// </summary>
    private async Task<bool> StartHiddenDownloadAsync(string uri, string fileName, string verb)
    {
        if (!_webReady || Browser.CoreWebView2 is null)
        {
            SetStatus(Strings.Get("status.wait_for_muse_to_load_then_try_the"));
            return false;
        }
        try
        {
            if (!_retryWebReady)
            {
                await RetryBrowser.EnsureCoreWebView2Async(Browser.CoreWebView2.Environment);
                RetryBrowser.CoreWebView2.DownloadStarting += OnDownloadStarting;
                _retryWebReady = true;
            }
            _log.Info($"{verb} download: {fileName} from {UrlPolicy.RedactForLog(uri)}");
            SetStatus(Strings.Format("status.verb_download", verb, fileName));
            RetryBrowser.CoreWebView2.Navigate(uri);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"{verb} download failed to start: {ex.Message}");
            SetStatus(Strings.Format("status.couldnt_start", fileName));
            return false;
        }
    }

    /// <summary>
    /// R4 #132: re-issue held downloads whose quiet-hours window has
    /// ended. Each is re-checked against the CURRENT schedule first —
    /// the user may have changed the hours while it waited.
    /// </summary>
    private async void PumpHeldDownloads()
    {
        if (_heldDownloads.Count == 0 || _downloadPumpBusy)
            return;
        _downloadPumpBusy = true;
        try
        {
            var due = _heldDownloads
                .Where(h => h.NextChanceUtc <= DateTimeOffset.UtcNow)
                .ToList();
            foreach (var held in due)
            {
                _heldDownloads.Remove(held);
                var decision = DownloadSchedule.Evaluate(
                    _settings.DownloadQuietHoursEnabled,
                    _settings.DownloadQuietStartMinutes,
                    _settings.DownloadQuietEndMinutes,
                    (long)_settings.DownloadQuietLargeFileMB * 1024L * 1024L,
                    DateTimeOffset.Now,
                    sizeBytes: null);
                if (!decision.Allowed)
                {
                    _heldDownloads.Add((held.Uri, held.FileName,
                        decision.NextChance ?? DateTimeOffset.UtcNow.AddMinutes(5)));
                    continue;
                }
                await StartHiddenDownloadAsync(held.Uri, held.FileName, Strings.Get("status.verb_starting_held"));
            }
        }
        finally
        {
            _downloadPumpBusy = false;
        }
    }

    /// <summary>
    /// R4 #134: the Downloads window's batch capture hands its validated
    /// links here. They queue in memory and start under the in-flight
    /// cap; the queue is this session's only (like every other
    /// URI-holding structure in the app).
    /// </summary>
    public Task StartBatchDownloadsAsync(IReadOnlyList<BatchLink> links)
    {
        foreach (var link in links)
            _batchQueue.Enqueue(link);
        SetStatus(Strings.Format("status.batch_download_queued", links.Count));
        PumpBatchQueue();
        return Task.CompletedTask;
    }

    private async void PumpBatchQueue()
    {
        if (_batchQueue.Count == 0 || _downloadPumpBusy)
            return;
        if (!_webReady || Browser.CoreWebView2 is null)
            return; // the pump timer retries once the view is up
        _downloadPumpBusy = true;
        try
        {
            while (_batchQueue.Count > 0)
            {
                var active = _downloads.Snapshot().Count(d =>
                    d.State is DownloadActivityState.InProgress or DownloadActivityState.Paused);
                if (DownloadConcurrency.StartableNow(active, _batchQueue.Count) <= 0)
                    return;
                var link = _batchQueue.Dequeue();
                if (!await StartHiddenDownloadAsync(link.Url, link.FileName, Strings.Get("status.verb_batch")))
                    return;
            }
        }
        finally
        {
            _downloadPumpBusy = false;
        }
    }

    /// <summary>
    /// R4 #132: one bandwidth-governor step over the live transfers.
    /// The policy lives in Core (<see cref="BandwidthGovernor"/>); this
    /// only applies its verdicts through the tracker's pause/resume.
    /// </summary>
    private void ApplyBandwidthGovernor()
    {
        var limit = (long)_settings.DownloadBandwidthLimitKBps * 1024L;
        var (toPause, toResume) = _bandwidthGovernor.Evaluate(_downloads.Snapshot(), limit);
        foreach (var id in toPause)
        {
            var item = _downloads.Snapshot().FirstOrDefault(d => d.Id == id);
            _downloads.Pause(id);
            _log.Info($"Bandwidth limit reached: paused {item?.FileName}");
            if (item is not null)
                SetStatus(Strings.Format("status.bandwidth_limit_reached_paused", item.FileName));
        }
        foreach (var id in toResume)
        {
            var item = _downloads.Snapshot().FirstOrDefault(d => d.Id == id);
            _downloads.Resume(id);
            _log.Info($"Bandwidth headroom: resumed {item?.FileName}");
        }
    }

    /// <summary>
    /// R4 #139: bounded automatic retry for this session's failures.
    /// Attempt counts key on the target path (a completion clears the
    /// count in <see cref="OnDownloadCompletedPost"/>); the wait is the
    /// Core policy's backoff; the retry rides the session-failed record,
    /// so if the user retried by hand first, the record is gone and the
    /// automatic attempt quietly stands down.
    /// </summary>
    private async void OnDownloadFailedAutoRetry()
    {
        var name = _downloads.LastFailedName;
        var reason = _downloads.LastFailedReason;
        if (name is null || reason is null)
            return;
        var failed = _downloads.SessionFailed.LastOrDefault(f => f.FileName == name);
        if (failed is null)
            return;
        var attempts = _autoRetryAttempts.TryGetValue(failed.FullPath, out var soFar) ? soFar : 0;
        if (!DownloadRetryPolicy.ShouldRetry(
                _settings.DownloadAutoRetryEnabled, reason, attempts, _settings.DownloadAutoRetryMaxAttempts))
            return;
        attempts++;
        _autoRetryAttempts[failed.FullPath] = attempts;
        var delay = DownloadRetryPolicy.DelayForAttempt(attempts);
        var max = DownloadRetryPolicy.SanitizeMaxAttempts(_settings.DownloadAutoRetryMaxAttempts);
        _log.Info($"Auto-retry {attempts} of {max} in {delay.TotalSeconds:0}s: {name} ({reason})");
        SetStatus(Strings.Format("status.download_failed_retrying", name, delay.TotalSeconds, attempts, max));
        var id = failed.Id;
        await Task.Delay(delay);
        if (_downloads.SessionFailed.Any(f => f.Id == id))
            await RetryFailedDownloadAsync(id);
    }

    /// <summary>
    /// R4 completion follow-ups: clear the auto-retry count, auto-sort
    /// the finished file when the user opted in (#131, journaled so the
    /// Downloads window's Undo covers it), then the per-profile quota
    /// heads-up (#140 — warning only, never a block, never a delete;
    /// deferred a dispatcher turn because the tracker writes its own
    /// completion status right after this callback returns).
    /// </summary>
    private void OnDownloadCompletedPost(DownloadRecord record)
    {
        _autoRetryAttempts.Remove(record.FullPath);

        // R5 #150: count the completion in the weekly stats (the
        // dashboard + digest read the same tally). Best-effort.
        try
        {
            var stats = WeeklyStatsStore.Load(AppPaths.WeeklyStatsPath);
            stats.RecordDownload(_settings.ActiveProfileId, DateTimeOffset.UtcNow);
            WeeklyStatsStore.Save(AppPaths.WeeklyStatsPath, stats);
        }
        catch (Exception ex) { _log.Warn($"Weekly download tally failed: {ex.Message}"); }

        if (_settings.AutoSortDownloads)
        {
            try
            {
                var folder = Path.GetDirectoryName(record.FullPath);
                if (!string.IsNullOrEmpty(folder))
                {
                    var moves = DownloadSortPlanner.Plan(
                        new[] { record }, folder, _settings.DownloadSortRules,
                        _settings.DownloadSortUseCategories, File.Exists);
                    foreach (var move in moves)
                    {
                        var dir = Path.GetDirectoryName(move.DestinationPath);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);
                        File.Move(move.SourcePath, move.DestinationPath);
                        DownloadSortJournalStore.Append(AppPaths.DownloadSortJournalPath, new DownloadSortBatch
                        {
                            AppliedUtc = DateTimeOffset.UtcNow,
                            Moves = new List<DownloadMove> { move },
                        });
                        DownloadHistoryStore.RewritePaths(AppPaths.DownloadsHistoryPath,
                            new Dictionary<string, string> { [move.SourcePath] = move.DestinationPath },
                            _settings.DownloadHistoryCap);
                        _downloads.UpdateCompletedPath(move.SourcePath, move.DestinationPath);
                        _log.Info($"Download auto-sorted ({move.RuleName}): {Path.GetFileName(move.SourcePath)}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Auto-sort failed for {record.FileName}: {ex.Message}");
            }
        }

        var quota = _settings.Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfileId)?.DownloadQuotaBytes ?? 0;
        if (quota > 0)
        {
            try
            {
                var folder = Path.GetDirectoryName(record.FullPath);
                if (!string.IsNullOrEmpty(folder) &&
                    DownloadQuota.Describe(DownloadQuota.ForFolder(folder, quota)) is { } warning)
                {
                    _log.Warn($"Download quota: {warning}");
                    Dispatcher.BeginInvoke(() => SetStatus(warning));
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Download quota check failed: {ex.Message}");
            }
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
    /// R6 #154: cache the page favicon as PNG bytes for the mini
    /// strip. Best-effort — a page without one (or a fetch failure)
    /// just leaves the strip's placeholder glyph. Never logged: the
    /// favicon URI is a full page URL.
    /// </summary>
    private async void OnFaviconChanged(object? sender, object e)
    {
        try
        {
            var core = Browser.CoreWebView2;
            if (core is null)
                return;
            await using var stream = await core.GetFaviconAsync(
                Microsoft.Web.WebView2.Core.CoreWebView2FaviconImageFormat.Png);
            if (stream is null)
            {
                _faviconPng = null;
                return;
            }
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            _faviconPng = memory.ToArray();
        }
        catch (Exception ex)
        {
            _log.Warn($"Favicon fetch for the mini strip failed: {ex.Message}");
        }
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
                    : Strings.Get("main.attention");
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

    /// <summary>OS window title: the app name and nothing else
    /// (SEAN, 2026-10-27 — supersedes the S1 #12 composition of page
    /// title + profile name). Kept as a method because callers refresh
    /// it on navigation/profile changes; the value never changes.</summary>
    private void ApplyWindowTitle()
    {
        Title = WindowTitle.Text;
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
                SetStatus(Strings.Get("status.the_page_stopped_responding_reloading_it"));
                try { Browser.CoreWebView2.Navigate(_lastGoodUrl ?? UrlPolicy.HomeUrl); }
                catch (Exception ex)
                {
                    _log.Warn($"Recovery reload failed: {ex.Message}");
                    ShowCrashOverlay(e.ProcessFailedKind.ToString());
                }
                break;
            case RecoveryDecision.RecreateWebView:
                _log.Info("Recovery: recreating the web view automatically (one automatic recovery per incident)");
                SetStatus(Strings.Get("status.the_web_view_stopped_rebuilding_it"));
                await RecreateWebViewAsync();
                break;
            case RecoveryDecision.ShowFailureOverlay:
                _log.Warn("Recovery: automatic recovery already used for this incident — showing the failure overlay");
                ShowCrashOverlay(e.ProcessFailedKind.ToString());
                break;
            default:
                SetStatus(Strings.Format("status.web_view_problem", e.ProcessFailedKind));
                break;
        }
    }

    private void ShowCrashOverlay(string kindName)
    {
        _history.NoteClosedPage(_lastGoodUrl);
        ShowOverlay(
            Strings.Get("main.page_stopped_title"),
            Strings.Get("main.page_stopped_body"),
            Strings.Get("main.reload_claude"), null);
        SetStatus(Strings.Format("status.web_view_problem", kindName));
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
            System.Windows.Automation.AutomationProperties.SetName(fresh, Strings.Get("muse_web_content"));
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
        SetStatus(Strings.Get("status.page_left_as_is_the_responsiveness"));
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
        return SelfTestLines.Notification(suppression, shown);
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
        var dialog = new SettingsWindow(clone, _log, _privacyGateway,
            (workingClone, id) => SwitchToProfile(workingClone, id),
            notificationTest: RunTestNotification,
            globalHotkeyStatus: () => _hotkeyManager?.StatusText ?? "",
            applyWorkspace: ApplyWorkspaceFromSettings,
            quickNoteHotkeyStatus: () => _noteHotkeyManager?.StatusText ?? "",
            openWeeklyDigest: () => OpenProductivity(showStats: false),
            openStatsDashboard: () => OpenProductivity(showStats: true)) { Owner = this };
        // S6 #56: closing Settings returns focus to whatever invoked it
        // (usually the page itself).
        var settingsAccepted = FocusReturn.ShowDialog(dialog) == true;
        // R5B #206: snippet edits inside the dialog are live (their
        // own store, like the Privacy section's actions), so the ⋯
        // list follows them even when the dialog itself is cancelled.
        RebuildSnippetsMenu();
        if (!settingsAccepted)
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
        // R2: workspaces + their two choices ride the same adoption.
        // ActiveWorkspaceId / PendingWorkspaceApplyId are session
        // state (like the S9 records) and are NOT adopted.
        _settings.Workspaces = clone.Workspaces;
        _settings.StartupWorkspaceId = clone.StartupWorkspaceId;
        _settings.AutoSaveSession = clone.AutoSaveSession;
        // R3: profile groups, schedule rules, and the ask-vs-auto
        // choice ride the same adoption. The schedule HOLD is state
        // (like the S9 records) and is NOT adopted.
        _settings.ProfileGroups = clone.ProfileGroups;
        _settings.ProfileSwitchRules = clone.ProfileSwitchRules;
        _settings.ProfileScheduleAutoSwitch = clone.ProfileScheduleAutoSwitch;
        // R5B: the drafts switch (the per-profile conversation-view
        // fields ride the Profiles adoption above, like the zoom).
        _settings.DraftsAutosaveEnabled = clone.DraftsAutosaveEnabled;
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
        // S11: the global hotkey (#96) and the musedesktop:// links
        // toggle (#99) — both take effect on OK below, not at the
        // next launch: the hotkey re-registers (freeing the old
        // combination) and the registration syncs to the toggle.
        _settings.GlobalHotkeyEnabled = clone.GlobalHotkeyEnabled;
        _settings.GlobalHotkeyGesture = clone.GlobalHotkeyGesture;
        _settings.RegisterProtocolLinks = clone.RegisterProtocolLinks;
        // R7: the language preference, the announcement switches, and
        // the text-spacing scale ride the same adoption. (Language
        // PACKS are files, written by the section's own Import button
        // like the Privacy section's actions — they are not clone
        // state, so they survive a Cancel, also like Privacy's.)
        _settings.UiLanguage = clone.UiLanguage;
        _settings.AnnouncementKindEnabled = clone.AnnouncementKindEnabled;
        _settings.ShellTextSpacingScale = clone.ShellTextSpacingScale;
        // R7B: the token ring's estimated limit — adopted here, and
        // the ring re-reads at once so the new limit shows immediately.
        _settings.EstimatedContextLimitTokens = clone.EstimatedContextLimitTokens;
        _settings.Sanitized();
        _ = RefreshTokenUsageAsync();
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
        _tray.RefreshAppearance();
        ApplyToolbarLayout();
        // R7 live apply: the localizer rebuilds from the adopted
        // settings — windows opened from now on speak the new
        // language, and the text-spacing half of ShellTextScaling's
        // pass below picks up the new scale for THIS window at once.
        // (This window's own strings were resolved when it was built;
        // they follow at the next launch — the Settings note and
        // DECISIONS say exactly that.)
        Strings.Initialize(_settings);
        ShellTextScaling.Apply(this, _settings.ShellFontScale);
        if (_webReady)
            Browser.ZoomFactor = EffectiveZoom();
        if (!_settings.SuspendWhenInTray)
            _tray.CancelPendingSuspend();
        SaveSettings();
        RefreshProfileIndicator();
        // R2 #115: the workspace accent override re-applies over the
        // (possibly new) settings accent, and the ⋯ menu's list
        // follows the adopted workspaces.
        _workspaceAccentKey = null;
        ApplyWorkspaceAccent();
        RebuildWorkspacesMenu();
        // R5B #201/#205 live apply: the page takes the (possibly
        // changed) profile's CSS right away.
        _ = ApplyPageStyleAsync();
        // S11 #96/#99 live apply (see the adoption above).
        _hotkeyManager?.Apply(_settings);
        // R5 #148: the quick-note hotkey re-registers on the same pass.
        _noteHotkeyManager?.Apply(_settings);
        ProtocolRegistration.Sync(_settings, _log);

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
    /// Settings' "Apply now" for a workspace (R2 #120): the same
    /// adoption shape as the profile switch — take the dialog's
    /// workspace state into the live settings, persist, then apply.
    /// The dialog closes itself afterwards, like the switch does.
    /// </summary>
    private void ApplyWorkspaceFromSettings(AppSettings workingClone, string workspaceId)
    {
        _settings.Workspaces = workingClone.Workspaces;
        _settings.StartupWorkspaceId = workingClone.StartupWorkspaceId;
        _settings.AutoSaveSession = workingClone.AutoSaveSession;
        _settings.Sanitized();
        SaveSettings();
        if (Workspaces.Find(_settings, workspaceId) is { } ws)
            ApplyWorkspace(ws, activate: true);
    }

    /// <summary>
    /// Profile switching restarts the app: a WebView2 environment is bound to
    /// its data folder for the process lifetime. Persists the dialog's
    /// profile state, relaunches detached after a short delay (the single-
    /// instance mutex frees as this process exits), and exits.
    /// </summary>
    private void SwitchToProfile(AppSettings workingClone, string targetProfileId, bool fromSchedule = false)
    {
        _settings.Profiles = workingClone.Profiles;
        // R3 #123/#127: a switch launched from the Settings dialog
        // also adopts its group + schedule edits (for menu/tray/palette
        // switches the "clone" IS the live settings — a no-op there).
        _settings.ProfileGroups = workingClone.ProfileGroups;
        _settings.ProfileSwitchRules = workingClone.ProfileSwitchRules;
        _settings.ProfileScheduleAutoSwitch = workingClone.ProfileScheduleAutoSwitch;
        _settings.ActiveProfileId = targetProfileId;
        if (!fromSchedule)
        {
            // R3 #123: a switch the user chose by hand holds the
            // schedule off the chosen profile until the current
            // window ends (or the next one begins) — manual wins.
            ProfileSchedule.RecordManualSwitch(_settings, targetProfileId, DateTimeOffset.Now);
        }
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
    private void SwitchToProfileFromMenu(string targetProfileId, bool fromSchedule = false)
    {
        var target = _settings.Profiles.FirstOrDefault(p => p.Id == targetProfileId);
        if (target is null || targetProfileId == _settings.ActiveProfileId)
            return;
        if (fromSchedule)
        {
            // The schedule already asked (or auto mode is on): no
            // second confirmation, and no manual hold is recorded.
            SwitchToProfile(_settings, targetProfileId, fromSchedule: true);
            return;
        }
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Format("msg.switch_profile_body", target.Name),
            Strings.Get("msg.switch_profile"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        SwitchToProfile(_settings, targetProfileId);
    }

    /// <summary>
    /// One schedule tick (R3 #123): Core's verdict for right now. Ask
    /// first (the default) proposes the switch in a dialog and records
    /// the manual hold on No; auto mode switches straight away.
    /// Locked, safe-mode, a dialog already open, or a prompt already
    /// showing all skip the tick — the next minute asks again.
    /// </summary>
    private void EvaluateSchedule()
    {
        // R6 #159: the theme schedule flips silently, so it is
        // evaluated before the profile schedule's guards (a dialog
        // being open must not hold the shell's light/dark hostage).
        EvaluateThemeSchedule();
        if (_isLocked || _safeMode || _schedulePromptOpen || _pendingProfileLockRecord is not null)
            return;
        if (System.Windows.Application.Current.Windows.OfType<Window>()
                .Any(w => w != this && w is not LockWindow && w.IsVisible))
            return; // a dialog or second window is up; don't stack a prompt on it
        var decision = ProfileSchedule.Evaluate(_settings, DateTimeOffset.Now, _settings.ActiveProfileId);
        if (!decision.ShouldSwitch || decision.ProfileId is not { } targetId)
            return;
        var target = _settings.Profiles.FirstOrDefault(p => p.Id == targetId);
        if (target is null)
            return;
        var rule = ProfileSchedule.ActiveRule(_settings, DateTimeOffset.Now);
        var when = rule is null ? "" : $" ({ProfileSchedule.Describe(rule)})";
        if (_settings.ProfileScheduleAutoSwitch)
        {
            _log.Info($"Profile schedule: auto-switching to profile {targetId}{when}");
            SwitchToProfileFromMenu(targetId, fromSchedule: true);
            return;
        }
        _schedulePromptOpen = true;
        try
        {
            var invoker = FocusReturn.Capture();
            var answer = System.Windows.MessageBox.Show(this,
                Strings.Format("msg.scheduled_switch_body", target.Name, when),
                Strings.Get("msg.scheduled_switch"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            FocusReturn.Restore(invoker);
            if (answer == MessageBoxResult.Yes)
            {
                SwitchToProfileFromMenu(targetId, fromSchedule: true);
            }
            else
            {
                // Declining is a manual choice: the hold keeps the
                // schedule quiet until this window ends.
                ProfileSchedule.RecordManualSwitch(_settings, _settings.ActiveProfileId, DateTimeOffset.Now);
                SaveSettings();
                _log.Info("Profile schedule: suggestion declined — the manual choice holds until the window ends");
            }
        }
        finally
        {
            _schedulePromptOpen = false;
        }
    }

    /// <summary>
    /// App hands over the launch profile's PIN record for a hidden
    /// (tray) launch (R3 #122): the first real show presents it via
    /// <see cref="PresentProfileLockWindow"/>, after the global lock.
    /// </summary>
    public void RequireProfileUnlock(AppLockRecord record) =>
        _pendingProfileLockRecord = record;

    /// <summary>The deferred profile-PIN screen (R3 #122): the global lock window's shape, with the profile's own record and throttle.</summary>
    private void PresentProfileLockWindow()
    {
        if (_pendingProfileLockRecord is not { } record)
            return;
        if (_profileLockWindow is not null)
        {
            _profileLockWindow.Activate();
            return;
        }
        var window = new LockWindow(record, _profileLockThrottle, exitOnClose: false) { Owner = this };
        ShellTextScaling.Apply(window, _settings.ShellFontScale);
        _profileLockWindow = window;
        window.Closed += (_, _) =>
        {
            _profileLockWindow = null;
            if (window.Unlocked)
            {
                _pendingProfileLockRecord = null;
                _log.Info("Profile PIN accepted at first show");
                ShowFromTray();
            }
            // Not unlocked: the app stays in the tray — the tray icon
            // reopens this screen (ShowFromTray routes here).
        };
        window.Show();
        window.Activate();
    }

    /// <summary>Rebuild the ⋯ menu's Switch-profile submenu (S3 #25): active checked + disabled, accent dot as a real colored ellipse. R3: archived profiles are hidden (#130), profiles with avatars lead with their glyph (#121), and groups gather their members under a heading (#127) when any exist.</summary>
    private void RebuildSwitchProfileMenu()
    {
        SwitchProfileItem.Items.Clear();
        var visible = Profiles.VisibleProfiles(_settings);
        SwitchProfileItem.IsEnabled = visible.Count > 1;
        var placed = new HashSet<string>();
        foreach (var group in _settings.ProfileGroups)
        {
            var members = ProfileGroups.VisibleMembers(_settings, group.Id);
            if (members.Count == 0)
                continue;
            var header = new MenuItem { Header = group.Name };
            foreach (var member in members)
            {
                header.Items.Add(BuildSwitchProfileMenuItem(member));
                placed.Add(member.Id);
            }
            SwitchProfileItem.Items.Add(header);
        }
        foreach (var p in visible)
        {
            if (placed.Contains(p.Id))
                continue;
            SwitchProfileItem.Items.Add(BuildSwitchProfileMenuItem(p));
        }
    }

    /// <summary>One switchable profile row (S3 #25 shape, R3 label): glyph-or-dot label, accent ellipse, click switches with the usual confirm.</summary>
    private MenuItem BuildSwitchProfileMenuItem(AccountProfile p)
    {
        var id = p.Id;
        var item = new MenuItem
        {
            Header = Profiles.DisplayLabel(p),
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
        return item;
    }

    /// <summary>Shows the active profile name in the ⋯ menu and tray tooltip when more than one switchable profile exists.</summary>
    private void RefreshProfileIndicator()
    {
        var active = _settings.Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfileId);
        var multi = Profiles.VisibleProfiles(_settings).Count > 1;
        ProfileItem.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        ProfileSeparator.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        if (multi && active is not null)
            ProfileItem.Header = Strings.Format("ui.profile_prefix", Profiles.DisplayLabel(active));
        _tray.SetText(multi && active is not null ? $"MuseDesktop — {Profiles.DisplayLabel(active)}" : "MuseDesktop");
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
                Strings.Get("main.open_link_browser"), null, CoreWebView2ContextMenuItemKind.Command);
            openItem.CustomItemSelected += (_, _) => OpenExternal(linkUri);

            var copyItem = env.CreateContextMenuItem(
                Strings.Get("main.copy_link"), null, CoreWebView2ContextMenuItemKind.Command);
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
        SetStatus(Strings.Get("status.browsing_data_cleared_sign_in_to_muse"));
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
            var host = HostOf(e.RequestUri) ?? Strings.Get("main.unknown_host");
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
                details = Strings.Format("msg.cert_details", cert.Subject, cert.Issuer, cert.ValidFrom, cert.ValidTo);
            }
            catch { /* the explainer + error name still carry the dialog */ }
            var invoker = FocusReturn.Capture();
            var answer = System.Windows.MessageBox.Show(this,
                Strings.Format("msg.cert_problem_body", host,
                    ConnectionInfo.CertificateErrorExplainer,
                    ConnectionInfo.DescribeCertificateError(e.ErrorStatus.ToString()) + details),
                Strings.Get("msg.certificate_warning"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
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
        lines += Strings.Format("msg.webview2_runtime_line", version ?? Strings.Get("msg.unknown"));
        var continuedHosts = _certDecisions.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        if (continuedHosts.Count > 0)
        {
            lines += Strings.Format("msg.cert_continued_hosts", string.Join(", ", continuedHosts));
        }
        var canForget = continuedHosts.Count > 0 && _webReady;
        var invoker = FocusReturn.Capture();
        var answer = System.Windows.MessageBox.Show(this, lines, Strings.Get("msg.connection_security"),
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
                SetStatus(Strings.Get("status.certificate_choices_forgotten_you_ll_be"));
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
        // R6 #156: a locked window is never see-through — the policy
        // forces full opacity from the moment the lock engages (the
        // mini strip, if up, is closed below with every other window).
        ApplyWindowOpacity();
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
        // R6 #156: the window's chosen opacity may render again.
        ApplyWindowOpacity();
        _log.Info("App unlocked");
        if (_initDeferred)
        {
            _initDeferred = false;
            await InitWebViewAsync();
            MaybeAutoCheckForUpdates();
        }
        // ShowFromTray drains whatever queued behind the lock (S11
        // #99/#100) — one intent, the newest: the queue is last-wins
        // by design, so an unlock never replays a backlog.
        ShowFromTray();
    }

    // ─────────────────── S11: hotkey, intents, dictate, drop ───────────────────

    /// <summary>
    /// One press of the global hotkey (S11 #96). Core's DecideToggle
    /// owns the matrix (lock > modal > foreground toggle); this only
    /// reads the live state and runs the outcome. "Bring forward" and
    /// "show" are both ShowFromTray — it activates without ever
    /// hiding, which is exactly what the modal case requires.
    /// </summary>
    private void OnGlobalHotkeyPressed()
    {
        var modalOpen = System.Windows.Application.Current.Windows.OfType<Window>()
            .Any(w => w != this && w is not LockWindow && w.IsVisible);
        var foreground = _hotkeyManager?.IsWindowForeground ?? false;
        switch (GlobalHotkey.DecideToggle(_isLocked, modalOpen, IsVisible, foreground))
        {
            case HotkeyToggleAction.ShowLockScreen:
                PresentLockWindow();
                break;
            case HotkeyToggleAction.HideToTray:
                _log.Info("Global hotkey: hiding MuseDesktop to the tray");
                HideToTray(showBalloon: false);
                break;
            case HotkeyToggleAction.BringForwardOnly:
            case HotkeyToggleAction.ShowWindow:
                ShowFromTray();
                break;
        }
    }

    /// <summary>
    /// Apply a launch intent (S11 #99/#100) — the one entry every
    /// outside ask funnels through: a second launch's CLI or link
    /// (via App's wake handler) and this launch's own leftover intent.
    /// While locked, Core's rule queues it (last wins) and the lock
    /// screen is the only answer; OnAppUnlocked applies what queued.
    /// </summary>
    public void ApplyLaunchIntent(LaunchIntent intent)
    {
        if (LaunchIntentRouting.Decide(intent, _isLocked) == IntentDisposition.QueueBehindLock)
        {
            _pendingIntents.Offer(intent);
            _log.Info($"Launch intent queued behind the app lock: {DescribeIntent(intent)}");
            PresentLockWindow();
            return;
        }
        ApplyLaunchIntentNow(intent);
    }

    private static string DescribeIntent(LaunchIntent intent) =>
        intent.Kind == LaunchIntentKind.SwitchProfile
            ? $"switch profile '{intent.ProfileId}'"
            : intent.Kind.ToString();

    private void ApplyLaunchIntentNow(LaunchIntent intent)
    {
        switch (intent.Kind)
        {
            case LaunchIntentKind.ShowWindow:
                ShowFromTray();
                break;
            case LaunchIntentKind.GoHome:
                ShowFromTray();
                OnHome(this, new RoutedEventArgs());
                break;
            case LaunchIntentKind.OpenSettings:
                OpenSettingsWindow();
                break;
            case LaunchIntentKind.OpenDownloads:
                OpenDownloadsFromTray();
                break;
            case LaunchIntentKind.ShowHelp:
                ShowFromTray();
                OnHelp(this, new RoutedEventArgs());
                break;
            case LaunchIntentKind.QuickDictate:
                QuickDictate();
                break;
            case LaunchIntentKind.SwitchProfile:
                if (intent.ProfileId is null || intent.ProfileId == _settings.ActiveProfileId)
                {
                    ShowFromTray();
                }
                else if (_settings.Profiles.All(p => p.Id != intent.ProfileId))
                {
                    // Validated upstream (cold start / link parse), but
                    // a profile can be removed between the two — never
                    // crash on a stale id, just show the window.
                    _log.Warn($"Launch intent named unknown profile '{intent.ProfileId}' — showing the window instead");
                    ShowFromTray();
                }
                else
                {
                    ShowFromTray();
                    SwitchToProfileFromMenu(intent.ProfileId);
                }
                break;
        }
    }

    /// <summary>
    /// Quick dictate (S11 #97): the tray item, the Jump List task, the
    /// --dictate flag, and the dictate intent all land here. Show the
    /// window, give the Muse PAGE the focus, and state the honest
    /// next step in the status bar. The boundary is deliberate:
    /// MuseDesktop never injects script into muse.ai, so it cannot
    /// press Muse's voice button or move the page's own focus onto
    /// the composer — focus enters the page here (WebView2's Focus),
    /// and Muse's own UI takes it from there.
    /// </summary>
    public void QuickDictate()
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        ShowFromTray();
        if (!_webReady)
        {
            SetStatus(Strings.Get("status.muse_isn_t_ready_yet_quick_dictate_will"));
            return;
        }
        try { Browser.Focus(); }
        catch (Exception ex) { _log.Warn($"Quick dictate focus failed: {ex.Message}"); }
        if (_micGrantedThisSession)
        {
            SetStatus(Strings.Get("dictate.ready_note"));
            return;
        }
        // Not granted yet this session — but Muse's saved choice (the
        // S7 permission record) may already say Allow, in which case
        // nothing needs granting. Check before sending the user off to
        // grant anything; the provisional line shows meanwhile.
        SetStatus(Strings.Get("dictate.grant_needed_note"));
        _ = RefreshQuickDictateNoteAsync();
    }

    private async Task RefreshQuickDictateNoteAsync()
    {
        try
        {
            var rows = await _privacyGateway.GetPermissionsAsync();
            var saved = rows.FirstOrDefault(r =>
                r.Kind.Equals("Microphone", StringComparison.OrdinalIgnoreCase)
                && r.Host.Equals("muse.ai", StringComparison.OrdinalIgnoreCase));
            if (saved?.State == "Allow")
                SetStatus(Strings.Get("dictate.ready_note"));
        }
        catch (Exception ex) { _log.Warn($"Quick dictate permission check failed: {ex.Message}"); }
    }

    // ── #98 drag & drop ──
    // Verified against the pinned SDK (1.0.4258.31): the windowed
    // WebView2 controller registers its own OLE drop target on its
    // child window and exposes NO drop events or settings to the host
    // (the SDK's only drag member is on the composition controller,
    // which this app doesn't use). Dropping a file on the Muse page
    // therefore already works end-to-end — the page's own upload UI
    // receives it — and nothing in the shell ever blocked it. What the
    // shell adds is guidance: while a file drag is over the app's own
    // chrome (the part WPF can see), the frame ring lights and the
    // status bar points at the page. The shell never reads, accepts,
    // or forwards dropped files.

    // (Media types fully qualified throughout: the WinForms global
    // usings make DragEventArgs/DataFormats ambiguous — the codebase's
    // standing pattern for this collision.)
    private void OnDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            SetDropHint(true);
    }

    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        // Over the shell chrome a drop has nowhere honest to go (the
        // files belong to the page), so the cursor says no while the
        // hint points at the page. Over the page itself WebView2 owns
        // the cursor — WPF never sees that part of the drag.
        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, System.Windows.DragEventArgs e) => SetDropHint(false);

    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        SetDropHint(false);
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Handled = true;
            SetStatus(Strings.Get("status.files_attach_on_the_muse_page_drop_them"));
        }
    }

    private void SetDropHint(bool on)
    {
        if (_dropHintOn == on)
            return;
        _dropHintOn = on;
        RefreshWebFocusRing();
        if (on)
            SetStatus(Strings.Get("status.drop_on_the_muse_page_to_attach_the"));
        else if (_webReady && !_downloads.IsTransferring)
            SetStatus(Strings.Get("status.ready"));
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
                SetStatus(Strings.Get("status.checking_for_updates"));
            _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
            var outcome = await UpdateService.CheckAsync(version, _settings.UpdateChannel, _log);
            switch (outcome.Kind)
            {
                case UpdateOutcomeKind.Available:
                    _updateTag = outcome.Tag;
                    _updateUrl = outcome.Url;
                    _updateRelease = outcome.Release;
                    UpdateItem.Header = Strings.Format("ui.update_available", outcome.Tag);
                    UpdateItem.ToolTip = Strings.Get("ui.download_and_install_the_update_inside");
                    SetStatus(Strings.Format("announce.update.available", outcome.Tag));
                    // S6 #54: screen readers hear the update notice too.
                    Announcer.Kind(AnnouncementKind.Updates,
                        Strings.Format("announce.update.available", outcome.Tag ?? Strings.Get("announce.new_version")));
                    break;
                case UpdateOutcomeKind.UpToDate:
                    if (manual) SetStatus(Strings.Format("status.youre_up_to_date", version.ToString(3)));
                    break;
                case UpdateOutcomeKind.Unreachable:
                    if (manual) SetStatus(Strings.Get("status.couldn_t_check_for_updates_github_didn"));
                    break;
                case UpdateOutcomeKind.BadData:
                    if (manual) SetStatus(Strings.Get("status.couldn_t_read_the_update_information"));
                    break;
                default:
                    if (manual) SetStatus(Strings.Get("status.couldn_t_check_for_updates_are_you"));
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
        if (find.MatchCount == 0) { FindCountText.Text = Strings.Get("ui.no_results"); return; }
        FindCountText.Text = find.ActiveMatchIndex >= 0
            ? Strings.Format("main.find_of", find.ActiveMatchIndex + 1, find.MatchCount)
            : Strings.Format("main.find_found", find.MatchCount);
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
        catch (Exception ex) { _log.Warn($"Print failed: {ex.Message}"); SetStatus(Strings.Get("status.printing_isn_t_available_right_now")); }
    }

    private async void OnSaveAsPdf(object sender, RoutedEventArgs e)
    {
        if (!_webReady) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Strings.Get("dlg.filter_pdf"),
            FileName = "muse-page.pdf",
            Title = Strings.Get("ui.save_page_as_pdf"),
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
            SetStatus(ok ? Strings.Format("status.saved_pdf_to", Path.GetFileName(dialog.FileName)) : Strings.Get("status.saving_the_pdf_failed"));
            if (!ok) _log.Warn("PrintToPdfAsync reported failure");
        }
        catch (Exception ex)
        {
            _log.Warn($"Save as PDF failed: {ex.Message}");
            SetStatus(Strings.Get("status.saving_the_pdf_failed"));
        }
    }

    // #4 Copy page address (Open in browser already exists as OnOpenInBrowser).
    private void OnCopyPageAddress(object sender, RoutedEventArgs e)
    {
        var src = Browser.Source?.ToString();
        if (string.IsNullOrEmpty(src)) return;
        try { System.Windows.Clipboard.SetText(src); SetStatus(Strings.Get("status.page_address_copied")); }
        catch { SetStatus(Strings.Get("status.couldn_t_copy_the_page_address")); }
    }

    // #7 Open in new window — a secondary window on the SAME environment.
    private void OnOpenInNewWindow(object sender, RoutedEventArgs e)
    {
        if (!_webReady) return;
        var src = Browser.Source?.ToString();
        if (string.IsNullOrEmpty(src)
            || UrlPolicy.ClassifyNavigation(src) != LinkDisposition.InApp)
        {
            SetStatus(Strings.Get("status.there_s_no_in_app_page_to_open_in_a_new"));
            return;
        }
        try
        {
            var window = new SecondaryWindow(Browser.CoreWebView2.Environment, _log, src) { Owner = this };
            TrackSecondaryWindow(window);
            window.Show();
        }
        catch (Exception ex)
        {
            _log.Warn($"Open in new window failed: {ex.Message}");
            SetStatus(Strings.Get("status.couldn_t_open_a_second_window"));
        }
    }

    /// <summary>
    /// Register a secondary window for the palette/switcher (R1
    /// #102/#103): it gets a stable id for this session, its
    /// activations feed the MRU list, its Ctrl+K asks for the palette
    /// owned by itself, and closing it unregisters it everywhere.
    /// </summary>
    private void TrackSecondaryWindow(SecondaryWindow window)
    {
        var id = $"secondary-{++_secondarySeq}";
        _trackedWindows[window] = id;
        window.PaletteRequested = () => OpenPalette(window);
        window.Activated += (_, _) => _windowMru.Touch(id);
        window.Closed += (_, _) =>
        {
            _trackedWindows.Remove(window);
            _windowMru.Remove(id);
            // R2 #117/#118: the journal follows the arrangement.
            UpdateSessionJournal();
        };
        _windowMru.Touch(id);
        UpdateSessionJournal();
    }

    // #5 Always-on-top pin.
    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        // A menu click arrives with the check already flipped; the
        // toolbar pin button (and every other caller) means "toggle".
        if (sender is MenuItem)
            SetAlwaysOnTop(PinItem.IsChecked);
        else
            SetAlwaysOnTop(!_settings.AlwaysOnTop);
    }

    /// <summary>The one always-on-top setter (S1 #5): the setting, the menu check, the window, and the status line move together — the palette's window action (R1 #104) lands here too.</summary>
    private void SetAlwaysOnTop(bool on)
    {
        _settings.AlwaysOnTop = on;
        PinItem.IsChecked = on;
        Topmost = on;
        SetStatus(on ? Strings.Get("status.window_will_stay_on_top") : Strings.Get("status.window_no_longer_stays_on_top"));
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
        if (on)
        {
            // R5 #141: focus mode fills the screen — the window
            // maximizes (its prior state is restored on exit) on top
            // of the chrome hiding. Full screen (F11) manages its own
            // state and is left alone.
            _focusPrevState = WindowState;
            if (!_isFullScreen && WindowState != WindowState.Maximized)
                WindowState = WindowState.Maximized;
            if (_settings.FocusTimerAutoStart)
                StartFocusTimer();
            SetStatus(Strings.Get("status.focus_mode_press_f8_or_esc_to_exit"));
            Announcer.Kind(AnnouncementKind.Focus,
                Strings.Get("status.focus_mode_press_f8_or_esc_to_exit"));
        }
        else
        {
            // Leaving focus mode stops the focus timer; the minutes it
            // banked still count toward the weekly stats.
            if (_focusTimer.State != FocusTimerState.Idle)
            {
                _focusTimer.Stop(DateTimeOffset.Now);
                DrainTimerMinutes();
            }
            if (!_isFullScreen && _focusPrevState != WindowState.Maximized
                && WindowState == WindowState.Maximized)
                WindowState = _focusPrevState;
        }
    }

    // ─────────────────────── R5: focus & productivity ───────────────────────

    /// <summary>Start (or restart) a focus block with the saved length (R5 #141). Focus mode comes on with it — a focus timer without the focused window is just a clock.</summary>
    private void StartFocusTimer()
    {
        if (!_focusMode)
            SetFocusMode(true);
        _focusTimer.Start(DateTimeOffset.Now, _settings.FocusTimerMinutes);
        SetStatus(Strings.Format("status.timer_started", TimerLines.Focus(_focusTimer, DateTimeOffset.Now)));
    }

    /// <summary>The session timer toggle (R5 #142): start a fresh Pomodoro session for the active profile, or stop the running one (banking its elapsed work minutes).</summary>
    private void ToggleSessionTimer()
    {
        var now = DateTimeOffset.Now;
        if (_sessionTimer is { IsRunning: true })
        {
            _sessionTimer.Stop(now);
            DrainTimerMinutes();
            _sessionTimer = null;
            _tray.SetTimerText(null);
            SetStatus(Strings.Get("status.session_timer_stopped"));
            return;
        }
        _sessionTimer = new SessionTimer(new SessionTimerConfig(
            _settings.SessionWorkMinutes, _settings.SessionBreakMinutes,
            _settings.SessionLongBreakMinutes, _settings.SessionCyclesBeforeLongBreak));
        _sessionMinutesDrained = 0;
        _sessionTimer.Start(now);
        SetStatus(Strings.Format("status.session_timer_prefix", TimerLines.Session(_sessionTimer, now)));
    }

    /// <summary>
    /// The one-second productivity tick (R5): advance both Core
    /// timers, surface their events (the break reminder is a tray
    /// balloon + status line — gentle, dismissible, quiet-hours
    /// gated by the tray's own policy, never a dialog), keep the tray
    /// tooltip's countdown current, drain banked focus minutes into
    /// the weekly stats, and run the command scheduler (R5 #147)
    /// about every 15 seconds. While the app is locked nothing fires
    /// and the startup rules stay pending: a locked app runs no
    /// commands.
    /// </summary>
    private void OnProductivityTick()
    {
        var now = DateTimeOffset.Now;

        var focus = _focusTimer.Tick(now);
        if (focus.FocusJustCompleted)
        {
            // Gentle by construction (R5 #141): the balloon fires on
            // this tick only (completion is reported exactly once),
            // the suggestion then waits in the status line until the
            // user takes the break (⋯ menu), dismisses it by starting
            // something else, or leaves focus mode. Nothing blocks.
            if (_settings.FocusBreakReminders)
                _tray.ShowReminder(Strings.Get("ui.focus_block_done"),
                    Strings.Format("tray.focus_done_body", _settings.FocusTimerMinutes));
            SetStatus(Strings.Format("status.focus_block_done_take_break", _settings.FocusBreakMinutes));
            Announcer.Kind(AnnouncementKind.Timers,
                Strings.Format("status.focus_block_done_take_break", _settings.FocusBreakMinutes));
        }
        if (focus.BreakJustCompleted)
        {
            SetStatus(Strings.Get("status.break_over_start_a_focus_block_whenever"));
            Announcer.Kind(AnnouncementKind.Timers,
                Strings.Get("status.break_over_start_a_focus_block_whenever"));
        }

        if (_sessionTimer is { IsRunning: true } session)
        {
            var tick = session.Tick(now);
            if (tick.PhaseChanged)
            {
                var line = TimerLines.Session(session, now);
                SetStatus(Strings.Format("status.session_timer_prefix", line));
                Announcer.Kind(AnnouncementKind.Timers,
                    Strings.Format("status.session_timer_prefix", line));
                _tray.ShowReminder(Strings.Get("ui.session_timer"),
                    tick.EnteredPhase == SessionPhase.Work
                        ? Strings.Get("tray.session_back_to_work")
                        : Strings.Get("tray.session_take_break"));
            }
        }

        DrainTimerMinutes();
        UpdateTimerTrayText(now);

        if (!_isLocked && now - _lastSchedulerCheckUtc >= TimeSpan.FromSeconds(15))
        {
            _lastSchedulerCheckUtc = now;
            RunDueSchedules(now);
        }
    }

    /// <summary>The tray tooltip's countdown (R5 #141/#142): the session timer wins when both run — it's the one with cycles to report.</summary>
    private void UpdateTimerTrayText(DateTimeOffset now)
    {
        var text = _sessionTimer is { IsRunning: true } session
            ? TimerLines.Session(session, now)
            : _focusTimer.State != FocusTimerState.Idle
                ? TimerLines.Focus(_focusTimer, now)
                : "";
        if (text == _lastTimerTrayText)
            return;
        _lastTimerTrayText = text;
        _tray.SetTimerText(string.IsNullOrEmpty(text) ? null : text);
    }

    /// <summary>Move newly banked focus minutes from both timers into the weekly stats file (R5 #150). Best-effort: a failed tally never disturbs a timer.</summary>
    private void DrainTimerMinutes()
    {
        var focusDelta = _focusTimer.FocusMinutesBanked - _focusMinutesDrained;
        var sessionDelta = _sessionTimer is null
            ? 0
            : _sessionTimer.FocusMinutesBanked - _sessionMinutesDrained;
        if (focusDelta <= 0 && sessionDelta <= 0)
            return;
        _focusMinutesDrained += Math.Max(0, focusDelta);
        _sessionMinutesDrained += Math.Max(0, sessionDelta);
        try
        {
            var stats = WeeklyStatsStore.Load(AppPaths.WeeklyStatsPath);
            if (focusDelta > 0)
                stats.RecordFocusMinutes(_settings.ActiveProfileId, focusDelta, DateTimeOffset.UtcNow);
            if (sessionDelta > 0)
                stats.RecordFocusMinutes(_settings.ActiveProfileId, sessionDelta, DateTimeOffset.UtcNow);
            WeeklyStatsStore.Save(AppPaths.WeeklyStatsPath, stats);
        }
        catch (Exception ex) { _log.Warn($"Weekly focus tally failed: {ex.Message}"); }
    }

    /// <summary>Run the scheduled commands that are due (R5 #147). Core stamps each due rule before it runs, so the settings are saved FIRST — a command that fails can't refire in a loop.</summary>
    private void RunDueSchedules(DateTimeOffset now)
    {
        List<ScheduledCommand> due;
        try
        {
            due = CommandScheduler.Evaluate(_settings.CommandSchedules, now, _schedulerStartupPending).ToList();
        }
        catch (Exception ex)
        {
            _log.Warn($"Command schedule check failed: {ex.Message}");
            return;
        }
        _schedulerStartupPending = false;
        if (due.Count == 0)
            return;
        SaveSettings();
        foreach (var rule in due)
        {
            _log.Info($"Scheduled command running: {rule.CommandId} ({rule.Trigger})");
            ExecutePaletteAction(rule.CommandId);
        }
    }

    /// <summary>Take the suggested break (R5 #141): offered when a focus block has just finished; the break counts down in the status bar and the tray.</summary>
    private void StartFocusBreak()
    {
        var now = DateTimeOffset.Now;
        if (_focusTimer.StartBreak(now, _settings.FocusBreakMinutes))
            SetStatus(TimerLines.Focus(_focusTimer, now));
        else
            SetStatus(Strings.Get("status.no_break_is_waiting_one_is_offered_when"));
    }

    /// <summary>Save the current page to the active profile's reading list (R5 #143). Only Muse pages save — Core's AppRoute gate decides, and the status line says so when it refuses.</summary>
    private void SavePageToReadingList()
    {
        var url = _webReady ? Browser.Source?.ToString() : null;
        try
        {
            var lists = ReadingListStore.Load(AppPaths.ReadingListPath);
            var entry = lists.Add(_settings.ActiveProfileId, _pageTitle, url, DateTimeOffset.UtcNow);
            if (entry is null)
            {
                SetStatus(Strings.Get("status.the_reading_list_only_holds_muse_pages"));
                return;
            }
            ReadingListStore.Save(AppPaths.ReadingListPath, lists);
            SetStatus(Strings.Format("status.saved_to_reading_list", entry.Title));
        }
        catch (Exception ex)
        {
            _log.Warn($"Reading list save failed: {ex.Message}");
            SetStatus(Strings.Get("status.couldn_t_save_to_the_reading_list_see"));
        }
    }

    /// <summary>The reading-list window (R5 #143): store work rides delegates so the window always shows the store's current truth.</summary>
    private void OpenReadingList()
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        var profileId = _settings.ActiveProfileId;
        var dialog = new ReadingListWindow(
            refresh: () => ReadingListStore.Load(AppPaths.ReadingListPath).EntriesFor(profileId),
            openRoute: route =>
            {
                if (_webReady)
                    Browser.CoreWebView2.Navigate("https://muse.ai" + route);
            },
            setRead: (id, read) =>
            {
                var lists = ReadingListStore.Load(AppPaths.ReadingListPath);
                if (lists.SetRead(profileId, id, read))
                    ReadingListStore.Save(AppPaths.ReadingListPath, lists);
            },
            remove: id =>
            {
                var lists = ReadingListStore.Load(AppPaths.ReadingListPath);
                if (lists.Remove(profileId, id))
                    ReadingListStore.Save(AppPaths.ReadingListPath, lists);
            })
            { Owner = this };
        dialog.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(dialog);
        ShellTextScaling.Apply(dialog, _settings.ShellFontScale);
        FocusReturn.ShowDialog(dialog);
    }

    // ─────────────────── R5B: conversation tools (#201–#206) ───────────────────

    /// <summary>
    /// The page bridge's other end (R5B). Everything the page posts
    /// is untrusted input: Core's parser decides the shape, and a
    /// null parse is simply ignored — a changed page can at worst
    /// make these features stop, never fault the shell. Nothing the
    /// page sends is ever logged (a draft is the user's own words; a
    /// snippet is a piece of a conversation).
    /// </summary>
    private async void OnPageWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.WebMessageAsJson; }
        catch { return; }
        if (PageBridge.TryParse(json) is not { } message)
            return;
        switch (message.Kind)
        {
            case PageBridgeKind.Ready:
                await PushPageStateAsync();
                break;
            case PageBridgeKind.Bookmark:
                if (!_isLocked)
                    PromptAndSaveBookmark(message.Text);
                break;
            case PageBridgeKind.Draft:
                OnPageDraftChanged(message.Text);
                break;
            case PageBridgeKind.DraftSent:
                OnPageDraftSent();
                // R7B: a sent message grows the conversation — re-read
                // the ring after the reply has had time to land.
                _ = RefreshTokenUsageDelayedAsync(4000);
                break;
        }
    }

    /// <summary>Feed the page everything the profile asks for (R5B #201/#205 style, then the route's saved draft for #204) — called on the bridge's ready handshake, when the document the handshake came from is the one on screen.</summary>
    private async Task PushPageStateAsync()
    {
        await ApplyPageStyleAsync();
        if (!_settings.DraftsAutosaveEnabled || _isLocked || !_webReady)
            return;
        var route = AppRoute.FromUrl(Browser.Source?.ToString());
        if (route is null)
            return;
        string? draft;
        try { draft = ConversationDraftStore.Load(AppPaths.DraftsPath).Get(_settings.ActiveProfileId, route); }
        catch { draft = null; }
        if (string.IsNullOrEmpty(draft))
            return;
        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync(PageScripts.RestoreDraft(draft));
        }
        catch (Exception ex) { _log.Warn($"Draft restore failed: {ex.Message}"); }
    }

    /// <summary>Apply the active profile's conversation CSS (#201/#205) to the current page — the style element is replaced, never stacked, and an all-default profile clears it back to the page as shipped.</summary>
    private async Task ApplyPageStyleAsync()
    {
        if (!_webReady)
            return;
        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync(
                PageScripts.ApplyStyle(PageStyleCss.Build(ActiveProfile())));
        }
        catch (Exception ex) { _log.Warn($"Page style apply failed: {ex.Message}"); }
    }

    /// <summary>The bookmark save flow (R5B #202, SEAN's design): the nickname comes FIRST and is required — the prompt loops until a usable name is given or the user cancels; there is no path that saves an unnamed bookmark.</summary>
    private void PromptAndSaveBookmark(string snippet)
    {
        if (AppRoute.FromUrl(Browser.Source?.ToString()) is null)
        {
            SetStatus(Strings.Get("status.bookmarks_only_work_on_muse_pages"));
            return;
        }
        while (true)
        {
            var prompt = new TextPromptWindow(Strings.Get("main.bookmark_title"),
                Strings.Get("main.bookmark_body"),
                "", okText: Strings.Get("main.bookmark_ok")) { Owner = this };
            ShellTextScaling.Apply(prompt, _settings.ShellFontScale);
            if (FocusReturn.ShowDialog(prompt) != true)
                return;
            if (MessageBookmarks.CleanNickname(prompt.Text) is null)
            {
                var invoker = FocusReturn.Capture();
                System.Windows.MessageBox.Show(this,
                    Strings.Get("msg.nickname_required_body"),
                    Strings.Get("msg.nickname_required"), MessageBoxButton.OK, MessageBoxImage.Information);
                FocusReturn.Restore(invoker);
                continue;
            }
            try
            {
                var all = MessageBookmarkStore.Load(AppPaths.BookmarksPath);
                var saved = all.Add(_settings.ActiveProfileId, prompt.Text, _pageTitle,
                    Browser.Source?.ToString(), snippet, DateTimeOffset.UtcNow);
                if (saved is null)
                {
                    SetStatus(Strings.Get("status.that_message_couldn_t_be_bookmarked"));
                    return;
                }
                MessageBookmarkStore.Save(AppPaths.BookmarksPath, all);
                SetStatus(Strings.Format("status.bookmarked_as", saved.Nickname));
            }
            catch (Exception ex)
            {
                _log.Warn($"Bookmark save failed: {ex.Message}");
                SetStatus(Strings.Get("status.couldn_t_save_the_bookmark_see_the_log"));
            }
            return;
        }
    }

    /// <summary>The bookmarks window (R5B #202): store work rides delegates, like the reading list's.</summary>
    private void OpenBookmarks()
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        if (!IsVisible)
            ShowFromTray();
        var profileId = _settings.ActiveProfileId;
        var dialog = new BookmarksWindow(
            refresh: () => MessageBookmarkStore.Load(AppPaths.BookmarksPath).EntriesFor(profileId),
            jump: JumpToBookmark,
            rename: (id, name) =>
            {
                var all = MessageBookmarkStore.Load(AppPaths.BookmarksPath);
                if (!all.Rename(profileId, id, name))
                    return false;
                MessageBookmarkStore.Save(AppPaths.BookmarksPath, all);
                return true;
            },
            remove: id =>
            {
                var all = MessageBookmarkStore.Load(AppPaths.BookmarksPath);
                if (all.Remove(profileId, id))
                    MessageBookmarkStore.Save(AppPaths.BookmarksPath, all);
            })
            { Owner = this };
        dialog.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(dialog);
        ShellTextScaling.Apply(dialog, _settings.ShellFontScale);
        FocusReturn.ShowDialog(dialog);
    }

    private void OnBookmarks(object sender, RoutedEventArgs e) => OpenBookmarks();

    // ─────────────────── R7B: the token-usage ring ───────────────────

    /// <summary>Wait a beat, then refresh the ring (R7B) — the delays let a landed page render its messages and a sent message's reply arrive before the probe reads.</summary>
    private async Task RefreshTokenUsageDelayedAsync(int delayMs)
    {
        try { await Task.Delay(delayMs); }
        catch { return; }
        await RefreshTokenUsageAsync();
    }

    /// <summary>
    /// Read the page's usage probe and re-render the ring (R7B).
    /// Core decides what the probe means — the page's own counts,
    /// the page's own percent, or the local estimate — and a failed
    /// probe (page changed shape, view not ready, locked) keeps the
    /// last reading rather than blanking the ring. Nothing the probe
    /// returns is logged: it carries counts and a text LENGTH, never
    /// the conversation's text.
    /// </summary>
    private async Task RefreshTokenUsageAsync()
    {
        if (!_webReady || _isLocked || _tokenRefreshBusy)
            return;
        _tokenRefreshBusy = true;
        try
        {
            var raw = await Browser.CoreWebView2.ExecuteScriptAsync(PageScripts.UsageProbe);
            var json = raw is not null && raw != "null" ? JsonSerializer.Deserialize<string>(raw) : null;
            if (TokenUsageProbe.TryParse(json) is not { } probe)
                return;
            _tokenUsage = TokenUsage.Resolve(probe, _settings.EstimatedContextLimitTokens);
            RenderTokenRing();
        }
        catch (Exception ex) { _log.Warn($"Token usage probe failed: {ex.Message}"); }
        finally { _tokenRefreshBusy = false; }
    }

    /// <summary>
    /// Paint the ring from the current reading (R7B): the arc, the
    /// centered percent (with the ~ honesty mark in estimate mode),
    /// and the tooltip naming the numbers and their source. No
    /// reading yet = an empty ring and a "no reading yet" tooltip.
    /// </summary>
    private void RenderTokenRing()
    {
        var usage = _tokenUsage;
        if (usage is null)
        {
            TokenRingArc.Data = null;
            TokenRingPercent.Text = "";
            TokenRingButton.ToolTip = Strings.Get("token.ring_tooltip_unknown");
            return;
        }
        TokenRingPercent.Text = TokenUsageText.FormatRingPercent(usage);
        TokenRingArc.Data = BuildRingGeometry(usage.Percent);
        TokenRingButton.ToolTip = usage switch
        {
            { UsedTokens: { } used, LimitTokens: { } limit } when usage.IsEstimate =>
                Strings.Format("token.ring_tooltip_estimate",
                    TokenUsageText.FormatCount(used), TokenUsageText.FormatCount(limit), usage.PercentRounded),
            { UsedTokens: { } used, LimitTokens: { } limit } =>
                Strings.Format("token.ring_tooltip_page",
                    TokenUsageText.FormatCount(used), TokenUsageText.FormatCount(limit), usage.PercentRounded),
            _ => Strings.Format("token.ring_tooltip_page_percent_only", usage.PercentRounded),
        };
    }

    /// <summary>The ring's arc for a percent (R7B): Core's geometry decides the points; this only transcribes them into a path. A full ring is two half arcs — a single arc segment cannot close a circle.</summary>
    private static System.Windows.Media.PathGeometry BuildRingGeometry(double percent)
    {
        const double center = 13.0, radius = 11.5;
        var geometry = new System.Windows.Media.PathGeometry();
        if (percent <= 0)
            return geometry;
        var figure = new System.Windows.Media.PathFigure
        {
            StartPoint = PointOf(TokenRingGeometry.Point(0, center, radius)),
            IsClosed = false,
            IsFilled = false,
        };
        if (percent >= 100)
        {
            figure.Segments.Add(ArcTo(50, center, radius, largeArc: false));
            figure.Segments.Add(ArcTo(100, center, radius, largeArc: false));
        }
        else
        {
            figure.Segments.Add(ArcTo(percent, center, radius, TokenRingGeometry.IsLargeArc(percent)));
        }
        geometry.Figures.Add(figure);
        return geometry;

        static System.Windows.Point PointOf((double X, double Y) p) => new(p.X, p.Y);
        static System.Windows.Media.ArcSegment ArcTo(double pct, double c, double r, bool largeArc) => new()
        {
            Point = PointOf(TokenRingGeometry.Point(pct, c, r)),
            Size = new System.Windows.Size(r * 2, r * 2),
            SweepDirection = System.Windows.Media.SweepDirection.Clockwise,
            IsLargeArc = largeArc,
        };
    }

    /// <summary>Open the token-usage panel (R7B): the reading's breakdown in words, its source stated plainly, and a Refresh that re-probes the page.</summary>
    private void OnTokenRing(object sender, RoutedEventArgs e)
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        var dialog = new TokenUsageWindow(_tokenUsage, _settings.EstimatedContextLimitTokens,
            RefreshTokenUsageForPanelAsync) { Owner = this };
        dialog.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(dialog);
        ShellTextScaling.Apply(dialog, _settings.ShellFontScale);
        FocusReturn.ShowDialog(dialog);
    }

    private async Task<TokenUsage?> RefreshTokenUsageForPanelAsync()
    {
        await RefreshTokenUsageAsync();
        return _tokenUsage;
    }

    /// <summary>Jump to a bookmark (R5B #202): open its conversation, then find + highlight the message by its snippet. Already there = find right away; anywhere else = navigate and let the completed navigation run the find.</summary>
    private void JumpToBookmark(MessageBookmark bookmark)
    {
        if (!_webReady)
        {
            SetStatus(Strings.Get("status.the_page_isn_t_ready_yet_try_the"));
            return;
        }
        if (!IsVisible)
            ShowFromTray();
        var current = AppRoute.FromUrl(Browser.Source?.ToString());
        if (current == bookmark.Route)
        {
            _ = FindBookmarkedMessageAsync(bookmark.Snippet);
            return;
        }
        _pendingBookmarkSnippet = bookmark.Snippet;
        Browser.CoreWebView2.Navigate(AppRoute.ToUrl(bookmark.Route));
        SetStatus(Strings.Format("status.opening_bookmark", bookmark.Nickname));
    }

    /// <summary>Run the find for a landed bookmark jump (R5B #202) and report honestly: found + highlighted, or landed in the conversation without a match — the message may have been edited since it was marked.</summary>
    private async Task FindBookmarkedMessageAsync(string snippet)
    {
        // The conversation is a live app page: give its renderer a
        // beat to put the messages in before searching for one.
        await Task.Delay(600);
        if (!_webReady)
            return;
        var found = false;
        try
        {
            found = await Browser.CoreWebView2.ExecuteScriptAsync(PageScripts.FindSnippet(snippet)) == "true";
        }
        catch (Exception ex) { _log.Warn($"Bookmark find failed: {ex.Message}"); }
        SetStatus(found
            ? Strings.Get("status.found_bookmarked_message")
            : Strings.Get("status.couldnt_find_bookmarked_message"));
    }

    /// <summary>A draft changed in the page (R5B #204): file it under the current conversation for the active profile. Empty text clears the draft — that IS the sent/cleared signal from the composer's side.</summary>
    private void OnPageDraftChanged(string text)
    {
        if (!_settings.DraftsAutosaveEnabled || _isLocked)
            return;
        var route = AppRoute.FromUrl(Browser.Source?.ToString());
        if (route is null)
            return;
        try
        {
            var drafts = ConversationDraftStore.Load(AppPaths.DraftsPath);
            if (drafts.Set(_settings.ActiveProfileId, route, text, DateTimeOffset.UtcNow))
                ConversationDraftStore.Save(AppPaths.DraftsPath, drafts);
        }
        catch (Exception ex) { _log.Warn($"Draft save failed: {ex.Message}"); }
    }

    /// <summary>A send was attempted in the page (R5B #204): the draft for this conversation has done its job — clear it.</summary>
    private void OnPageDraftSent()
    {
        var route = AppRoute.FromUrl(Browser.Source?.ToString());
        if (route is null)
            return;
        try
        {
            var drafts = ConversationDraftStore.Load(AppPaths.DraftsPath);
            if (drafts.Remove(_settings.ActiveProfileId, route))
                ConversationDraftStore.Save(AppPaths.DraftsPath, drafts);
        }
        catch (Exception ex) { _log.Warn($"Draft clear failed: {ex.Message}"); }
    }

    private void OnExportConversationMarkdown(object sender, RoutedEventArgs e) =>
        _ = ExportConversationAsync(pdf: false);

    private void OnExportConversationPdf(object sender, RoutedEventArgs e) =>
        _ = ExportConversationAsync(pdf: true);

    /// <summary>
    /// Export the current conversation (R5B #203). Markdown: the
    /// page's own messages (read by the bridge's extractor, shaped by
    /// Core) written to the file the user picks. PDF: the pinned
    /// SDK's PrintToPdfAsync over the conversation page as it appears
    /// — the same engine as Save as PDF, offered under the export
    /// name with the conversation's own file name.
    /// </summary>
    private async Task ExportConversationAsync(bool pdf)
    {
        if (!_webReady)
        {
            SetStatus(Strings.Get("status.the_page_isn_t_ready_yet"));
            return;
        }
        if (pdf)
        {
            var pdfDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = Strings.Get("dlg.filter_pdf"),
                FileName = ConversationExport.SuggestFileName(_pageTitle, DateTimeOffset.Now, ".pdf"),
                Title = Strings.Get("ui.export_conversation_as_pdf"),
            };
            var pdfInvoker = FocusReturn.Capture();
            var pdfChosen = pdfDialog.ShowDialog(this) == true;
            FocusReturn.Restore(pdfInvoker);
            if (!pdfChosen) return;
            try
            {
                var printSettings = Browser.CoreWebView2.Environment.CreatePrintSettings();
                var ok = await Browser.CoreWebView2.PrintToPdfAsync(pdfDialog.FileName, printSettings);
                SetStatus(ok
                    ? Strings.Format("status.exported_conversation_to", Path.GetFileName(pdfDialog.FileName))
                    : Strings.Get("status.exporting_the_pdf_failed"));
                if (!ok) _log.Warn("Conversation PDF export: PrintToPdfAsync reported failure");
            }
            catch (Exception ex)
            {
                _log.Warn($"Conversation PDF export failed: {ex.Message}");
                SetStatus(Strings.Get("status.exporting_the_pdf_failed"));
            }
            return;
        }

        string? extractionJson = null;
        try
        {
            var raw = await Browser.CoreWebView2.ExecuteScriptAsync(PageScripts.ExtractConversation);
            if (raw is not null && raw != "null")
                extractionJson = JsonSerializer.Deserialize<string>(raw);
        }
        catch (Exception ex) { _log.Warn($"Conversation extraction failed: {ex.Message}"); }
        var messages = ConversationExport.TryParseExtraction(extractionJson);
        if (messages is null || messages.Count == 0)
        {
            SetStatus(Strings.Get("status.couldn_t_read_this_conversation_from"));
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Strings.Get("dlg.filter_md"),
            FileName = ConversationExport.SuggestFileName(_pageTitle, DateTimeOffset.Now, ".md"),
            Title = Strings.Get("ui.export_conversation_as_markdown"),
        };
        var invoker = FocusReturn.Capture();
        var chosen = dialog.ShowDialog(this) == true;
        FocusReturn.Restore(invoker);
        if (!chosen) return;
        try
        {
            File.WriteAllText(dialog.FileName,
                ConversationExport.RenderMarkdown(_pageTitle, DateTimeOffset.Now, messages));
            SetStatus(Strings.Format("status.exported_messages_to", messages.Count, Path.GetFileName(dialog.FileName)));
        }
        catch (Exception ex)
        {
            _log.Warn($"Conversation export failed: {ex.Message}");
            SetStatus(Strings.Get("status.exporting_the_conversation_failed"));
        }
    }

    /// <summary>Insert a snippet into the composer (R5B #206): the text lands in the message box for the user to edit — inserting is all this does; sending stays the user's act.</summary>
    private async void InsertSnippetIntoComposer(PromptSnippet snippet)
    {
        if (!_webReady)
        {
            SetStatus(Strings.Get("status.the_page_isn_t_ready_yet"));
            return;
        }
        if (!IsVisible)
            ShowFromTray();
        var inserted = false;
        try
        {
            inserted = await Browser.CoreWebView2.ExecuteScriptAsync(
                PageScripts.InsertSnippet(snippet.Body)) == "true";
        }
        catch (Exception ex) { _log.Warn($"Snippet insert failed: {ex.Message}"); }
        SetStatus(inserted
            ? Strings.Format("status.inserted_snippet", snippet.Name)
            : Strings.Get("status.couldnt_find_message_box"));
    }

    /// <summary>The ⋯ menu's snippet submenu (R5B #206): one item per snippet in the active profile's library, rebuilt wherever the store may have changed (startup, Settings OK).</summary>
    private void RebuildSnippetsMenu()
    {
        SnippetsItem.Items.Clear();
        IReadOnlyList<PromptSnippet> snippets;
        try { snippets = PromptSnippetStore.Load(AppPaths.SnippetsPath).EntriesFor(_settings.ActiveProfileId); }
        catch { snippets = Array.Empty<PromptSnippet>(); }
        if (snippets.Count == 0)
        {
            SnippetsItem.Items.Add(new MenuItem
            {
                Header = Strings.Get("ui.no_snippets_yet_add_them_in_settings"),
                IsEnabled = false,
            });
            return;
        }
        foreach (var snippet in snippets)
        {
            var captured = snippet;
            var item = new MenuItem { Header = captured.Name };
            item.Click += (_, _) => InsertSnippetIntoComposer(captured);
            SnippetsItem.Items.Add(item);
        }
    }

    /// <summary>Quick note capture (R5 #148) — one dialog for the menu, the palette, and the global hotkey, filed under the active profile.</summary>
    private void ShowQuickNote()
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        if (!IsVisible)
            ShowFromTray();
        var dialog = new QuickNoteWindow { Owner = this };
        dialog.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(dialog);
        ShellTextScaling.Apply(dialog, _settings.ShellFontScale);
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var notes = ProfileNotesStore.Load(AppPaths.NotesPath);
            if (notes.Add(_settings.ActiveProfileId, dialog.Text, DateTimeOffset.UtcNow) is not null)
            {
                ProfileNotesStore.Save(AppPaths.NotesPath, notes);
                SetStatus(Strings.Get("status.note_saved"));
            }
        }
        catch (Exception ex) { _log.Warn($"Quick note save failed: {ex.Message}"); }
    }

    /// <summary>The quick-note hotkey press (R5 #148): bring the app forward and open the capture — while locked, the lock screen is the only answer, exactly like the show/hide hotkey.</summary>
    private void OnQuickNoteHotkeyPressed()
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        ShowQuickNote();
    }

    /// <summary>The notebook window (R5 #148): the active profile's notes, with capture and delete riding delegates.</summary>
    private void OpenNotes()
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        var profileId = _settings.ActiveProfileId;
        var dialog = new NotesWindow(
            refresh: () => ProfileNotesStore.Load(AppPaths.NotesPath).NotesFor(profileId),
            newNote: ShowQuickNote,
            delete: id =>
            {
                var notes = ProfileNotesStore.Load(AppPaths.NotesPath);
                if (notes.Delete(profileId, id))
                    ProfileNotesStore.Save(AppPaths.NotesPath, notes);
            })
            { Owner = this };
        dialog.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(dialog);
        ShellTextScaling.Apply(dialog, _settings.ShellFontScale);
        FocusReturn.ShowDialog(dialog);
    }

    /// <summary>The digest + dashboard window (R5 #146/#150): both reports build fresh from the local stores on every open — the digest is on-demand by definition, never cached, never scheduled.</summary>
    private void OpenProductivity(bool showStats)
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        var digest = WeeklyDigest.Build(
            WeeklyStatsStore.Load(AppPaths.WeeklyStatsPath), _settings,
            ReadingListStore.Load(AppPaths.ReadingListPath),
            ProfileNotesStore.Load(AppPaths.NotesPath),
            DownloadHistoryStore.Load(AppPaths.DownloadsHistoryPath, _settings.DownloadHistoryCap),
            CurrentWindowDescriptors().Count, DateTimeOffset.UtcNow);
        var dialog = new ProductivityWindow(showStats, digest.RenderText(),
            statsSource: () => StatsReport.Build(
                WeeklyStatsStore.Load(AppPaths.WeeklyStatsPath), _settings, DateTimeOffset.UtcNow),
            clearStats: () =>
            {
                try { File.Delete(AppPaths.WeeklyStatsPath); }
                catch (Exception ex) { _log.Warn($"Clearing weekly stats failed: {ex.Message}"); }
            })
            { Owner = this };
        dialog.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(dialog);
        ShellTextScaling.Apply(dialog, _settings.ShellFontScale);
        FocusReturn.ShowDialog(dialog);
    }

    // ─────────────────────── R5: menu handlers ───────────────────────

    private void OnQuickNote(object sender, RoutedEventArgs e) => ShowQuickNote();
    private void OnNotes(object sender, RoutedEventArgs e) => OpenNotes();
    private void OnSaveToReadingList(object sender, RoutedEventArgs e) => SavePageToReadingList();
    private void OnReadingList(object sender, RoutedEventArgs e) => OpenReadingList();
    private void OnStartFocusTimer(object sender, RoutedEventArgs e) => StartFocusTimer();
    private void OnTakeBreak(object sender, RoutedEventArgs e) => StartFocusBreak();
    private void OnSessionTimer(object sender, RoutedEventArgs e) => ToggleSessionTimer();
    private void OnWeeklyDigest(object sender, RoutedEventArgs e) => OpenProductivity(showStats: false);
    private void OnStatsDashboard(object sender, RoutedEventArgs e) => OpenProductivity(showStats: true);

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
            SetStatus(back ? Strings.Get("status.no_earlier_pages") : Strings.Get("status.no_forward_pages"));
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
            SetStatus(Strings.Get("status.no_closed_page_to_restore"));
            return;
        }
        Browser.CoreWebView2.Navigate(url);
        SetStatus(Strings.Get("status.restored_the_last_closed_page"));
    }

    private void OnDownloads(object sender, RoutedEventArgs e)
    {
        var dialog = new DownloadsWindow(_log, _downloads, RetryFailedDownloadAsync, _settings.DownloadHistoryCap,
                _settings, StartBatchDownloadsAsync)
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
        try { webViewVersion = CoreWebView2Environment.GetAvailableBrowserVersionString() ?? Strings.Get("main.not_found"); }
        catch { webViewVersion = Strings.Get("main.couldnt_read"); }
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
    private void OnHelp(object sender, RoutedEventArgs e) => OpenHelp(null);

    /// <summary>Open Help, optionally at a specific page (R1: the palette's help commands land on their page, not on page one).</summary>
    private void OpenHelp(string? initialPageId)
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
        var dialog = new HelpWindow(markdown, HandleHelpAction, initialPageId) { Owner = this };
        ThemeManager.ApplyTitleBar(dialog);
        ShellTextScaling.Apply(dialog, _settings.ShellFontScale);
        dialog.ShowDialog();
        FocusReturn.Restore(invoker);
    }

    // ─────────────────── v1.1.0 R1: palette & switcher (items 101–110) ───────────────────

    /// <summary>
    /// Assemble the palette for one opening (R1 #102): providers wrap
    /// LIVE data (profiles, download history, open windows), so the
    /// palette is rebuilt each time — there is no cache to go stale.
    /// </summary>
    private CommandPalette BuildPalette()
    {
        IReadOnlyList<DownloadRecord> downloads;
        try { downloads = DownloadHistoryStore.Load(AppPaths.DownloadsHistoryPath, _settings.DownloadHistoryCap); }
        catch { downloads = Array.Empty<DownloadRecord>(); }
        // R5 #143/#145: the active profile's reading list and this
        // session's navigation history join the palette — same
        // fail-soft loading as the downloads.
        IReadOnlyList<ReadingListEntry> reading;
        try { reading = ReadingListStore.Load(AppPaths.ReadingListPath).EntriesFor(_settings.ActiveProfileId); }
        catch { reading = Array.Empty<ReadingListEntry>(); }
        // R5B #202/#206: the active profile's bookmarks and snippets
        // join the palette the same fail-soft way.
        IReadOnlyList<MessageBookmark> bookmarks;
        try { bookmarks = MessageBookmarkStore.Load(AppPaths.BookmarksPath).EntriesFor(_settings.ActiveProfileId); }
        catch { bookmarks = Array.Empty<MessageBookmark>(); }
        IReadOnlyList<PromptSnippet> snippets;
        try { snippets = PromptSnippetStore.Load(AppPaths.SnippetsPath).EntriesFor(_settings.ActiveProfileId); }
        catch { snippets = Array.Empty<PromptSnippet>(); }
        return CommandRegistry.CreateDefault(
            Profiles.VisibleProfiles(_settings), _settings.ActiveProfileId, downloads, CurrentWindowDescriptors(),
            _settings.Workspaces, _settings.ActiveWorkspaceId, _settings.StartupWorkspaceId,
            groupSettings: _settings,
            readingEntries: reading,
            historyEntries: _history.Entries,
            bookmarks: bookmarks,
            snippets: snippets);
    }

    /// <summary>
    /// Open the command palette (#101; compact form from the tray's
    /// middle-click, #110). The palette names profiles, files, and
    /// windows — content surfaces all — so it sits behind the app
    /// lock like the Downloads window: while locked, the lock screen
    /// is the only answer.
    /// </summary>
    public void OpenPalette(Window? owner = null, bool compact = false)
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        if (!IsVisible)
            ShowFromTray();
        if (!IsVisible)
            return; // the lock screen answered instead
        var history = ActionHistoryStore.Load(AppPaths.PaletteHistoryPath);
        var dialog = new PaletteWindow(BuildPalette(), history, ExecutePaletteCommand, PaletteRowColor, compact)
            { Owner = owner ?? this };
        dialog.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(dialog);
        ShellTextScaling.Apply(dialog, _settings.ShellFontScale);
        dialog.ShowDialog();
    }

    /// <summary>The window switcher overlay (#103): the same MRU-ordered descriptors the palette searches, as a pick-one list.</summary>
    private void OpenSwitcher()
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        var windows = CurrentWindowDescriptors();
        if (windows.Count == 0)
            return;
        // R5 #144: groups name live windows only — prune against the
        // current window set before the switcher renders them.
        _windowGroups.Prune(windows.Select(w => w.Id));
        var dialog = new SwitcherWindow(windows, ActivateTrackedWindow,
            _windowGroups, ShowWindowGroup,
            (windowId, preset) =>
            {
                if (FindTrackedWindow(windowId) is { } target)
                    TileWindowToPreset(target, preset);
            })
            { Owner = this };
        dialog.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(dialog);
        ShellTextScaling.Apply(dialog, _settings.ShellFontScale);
        dialog.ShowDialog();
    }

    /// <summary>Raise a whole window group (R5 #144): activate every member — last-to-first, so the group's most recently used window ends in front.</summary>
    private void ShowWindowGroup(IReadOnlyList<string> windowIds)
    {
        foreach (var id in windowIds.Reverse())
            ActivateTrackedWindow(id);
    }

    private void OnCommandPalette(object sender, RoutedEventArgs e) => OpenPalette();

    private void OnSwitchWindow(object sender, RoutedEventArgs e) => OpenSwitcher();

    /// <summary>The palette row's profile color dot (#102/#103): a profile command carries its own profile's color; a window command carries the color of the (shared) active profile its windows belong to; a workspace command (R2 #115) carries the workspace's accent override color.</summary>
    private string? PaletteRowColor(PaletteCommand command)
    {
        switch (command.Source)
        {
            case PaletteCommandSource.Profile:
                return ProfileColors.HexFor(
                    _settings.Profiles.FirstOrDefault(p => p.Id == command.Payload)?.DisplayColor);
            case PaletteCommandSource.Window:
                return ProfileColors.HexFor(ActiveProfile()?.DisplayColor);
            case PaletteCommandSource.Workspace:
                return ProfileColors.HexFor(
                    _settings.Workspaces.FirstOrDefault(w => w.Id == command.Payload)?.AccentColor);
            default:
                return null;
        }
    }

    /// <summary>Every open window as a descriptor, MRU-sorted — the main window from its page title, secondaries from theirs (their titles carry a " — MuseDesktop" suffix the switcher doesn't need).</summary>
    private IReadOnlyList<WindowDescriptor> CurrentWindowDescriptors()
    {
        var active = ActiveProfile();
        var list = new List<WindowDescriptor>
        {
            new("main",
                string.IsNullOrWhiteSpace(_pageTitle) ? "Muse" : _pageTitle,
                active?.Name, active?.DisplayColor,
                IsMain: true, SupportsFocusMode: true, IsActive: IsActive,
                ProfileGlyphKey: active?.AvatarGlyph),
        };
        foreach (var (window, id) in _trackedWindows)
        {
            var title = window.Title ?? "";
            const string suffix = " — MuseDesktop";
            if (title.EndsWith(suffix, StringComparison.Ordinal))
                title = title[..^suffix.Length];
            list.Add(new WindowDescriptor(
                id,
                string.IsNullOrWhiteSpace(title) ? Strings.Get("muse_second_window") : title,
                active?.Name, active?.DisplayColor,
                IsMain: false, SupportsFocusMode: false, IsActive: window.IsActive,
                ProfileGlyphKey: active?.AvatarGlyph));
        }
        return _windowMru.Sort(list);
    }

    /// <summary>
    /// Run one palette command. History records FIRST (actions only —
    /// Core refuses everything else, so nothing a command carried is
    /// ever written down), then the source-specific dispatch runs the
    /// exact handler the menus and tray use.
    /// </summary>
    private void ExecutePaletteCommand(PaletteCommand command)
    {
        try { ActionHistoryStore.Record(AppPaths.PaletteHistoryPath, command); }
        catch (Exception ex) { _log.Warn($"Palette history save failed: {ex.Message}"); }

        switch (command.Source)
        {
            case PaletteCommandSource.Action:
                ExecutePaletteAction(command.Id);
                break;
            case PaletteCommandSource.Profile:
                if (command.Id.StartsWith("profile-group.switch.", StringComparison.Ordinal))
                {
                    // R3 #127: the payload is a GROUP id — resolve it
                    // to the group's current switch target, then run
                    // the ordinary profile switch (with its confirm).
                    if (command.Payload is { } groupId
                        && ProfileGroups.SwitchTarget(_settings, groupId) is { } groupTarget)
                        SwitchToProfileFromMenu(groupTarget.Id);
                }
                else if (command.Payload is { } profileId)
                    SwitchToProfileFromMenu(profileId);
                break;
            case PaletteCommandSource.Settings:
                OpenSettings(command.Payload);
                break;
            case PaletteCommandSource.Help:
                OpenHelp(command.Payload);
                break;
            case PaletteCommandSource.Download:
                if (command.Payload is { } path)
                {
                    if (command.Id.StartsWith("download.reveal.", StringComparison.Ordinal))
                        RevealDownload(path);
                    else
                        OpenRecentDownload(path);
                }
                break;
            case PaletteCommandSource.Window:
                ExecuteWindowCommand(command);
                break;
            case PaletteCommandSource.Workspace:
                if (command.Payload is { } workspaceId)
                    ApplyWorkspaceById(workspaceId);
                break;
            case PaletteCommandSource.ReadingList:
                // R5 #143: the payload is an app ROUTE (never a full
                // address — that's all the reading list can hold).
                if (command.Payload is { } route && _webReady)
                    Browser.CoreWebView2.Navigate("https://muse.ai" + route);
                break;
            case PaletteCommandSource.History:
                // R5 #145: the payload is the page URL, in-process
                // only; navigation passes the ordinary link policy,
                // exactly like restoring a closed page.
                if (command.Payload is { } historyUrl && _webReady
                    && UrlPolicy.ClassifyNavigation(historyUrl) == LinkDisposition.InApp)
                    Browser.CoreWebView2.Navigate(historyUrl);
                break;
            case PaletteCommandSource.Bookmark:
                // R5B #202: the payload is the bookmark id — resolve
                // it against the store NOW, so the jump uses the
                // bookmark as it stands, not as the palette found it.
                if (command.Payload is { } bookmarkId
                    && MessageBookmarkStore.Load(AppPaths.BookmarksPath)
                        .Find(_settings.ActiveProfileId, bookmarkId) is { } bookmark)
                    JumpToBookmark(bookmark);
                break;
            case PaletteCommandSource.Snippet:
                // R5B #206: same resolve-at-run-time discipline — an
                // edited snippet inserts its current text.
                if (command.Payload is { } snippetId
                    && PromptSnippetStore.Load(AppPaths.SnippetsPath)
                        .Find(_settings.ActiveProfileId, snippetId) is { } snippet)
                    InsertSnippetIntoComposer(snippet);
                break;
        }
    }

    /// <summary>The built-in actions' dispatch — every branch is an existing handler or setter, nothing reimplemented for the palette.</summary>
    private void ExecutePaletteAction(string id)
    {
        switch (id)
        {
            case "action.switch-window": OpenSwitcher(); break;
            case "action.save-workspace": SaveCurrentWorkspace(); break;
            case "action.snapshot-session": TakeSessionSnapshot(); break;
            case "action.restore-session": RestoreSessionSnapshot(); break;
            case "action.settings": OpenSettings(); break;
            case "action.downloads": OnDownloads(this, new RoutedEventArgs()); break;
            case "action.home": OnHome(this, new RoutedEventArgs()); break;
            case "action.reload": OnReload(this, new RoutedEventArgs()); break;
            case "action.find": ShowFindBar(); break;
            case "action.print": OnPrint(this, new RoutedEventArgs()); break;
            case "action.save-pdf": OnSaveAsPdf(this, new RoutedEventArgs()); break;
            case "action.copy-address": OnCopyPageAddress(this, new RoutedEventArgs()); break;
            case "action.open-in-browser": OnOpenInBrowser(this, new RoutedEventArgs()); break;
            case "action.new-window": OnOpenInNewWindow(this, new RoutedEventArgs()); break;
            case "action.focus-mode": SetFocusMode(!_focusMode); break;
            case "action.mini-mode": ToggleMiniMode(); break;
            case "action.fullscreen": SetFullScreen(!_isFullScreen); break;
            case "action.always-on-top": SetAlwaysOnTop(!_settings.AlwaysOnTop); break;
            case "action.zoom-in": OnZoomIn(this, new RoutedEventArgs()); break;
            case "action.zoom-out": OnZoomOut(this, new RoutedEventArgs()); break;
            case "action.zoom-reset":
                if (_webReady) Browser.ZoomFactor = 1.0;
                break;
            case "action.restore-closed": OnRestoreClosedPage(this, new RoutedEventArgs()); break;
            case "action.shortcuts": OnShortcuts(this, new RoutedEventArgs()); break;
            case "action.commands-help": OpenHelp(HelpCatalog.CommandsPageId); break;
            case "action.diagnostics": OnDiagnostics(this, new RoutedEventArgs()); break;
            case "action.help": OnHelp(this, new RoutedEventArgs()); break;
            case "action.about": OnAbout(this, new RoutedEventArgs()); break;
            case "action.check-updates": OnCheckForUpdates(this, new RoutedEventArgs()); break;
            case "action.lock-now":
                if (AppLock.IsLockActive(_settings))
                    LockNow();
                else
                    SetStatus(Strings.Get("status.no_app_lock_is_set_add_one_in_settings"));
                break;
            case "action.quick-dictate": QuickDictate(); break;
            // R5 #141–#150: focus & productivity — the same handlers
            // the ⋯ menu uses, so the scheduler (#147) runs exactly
            // what a click would.
            case "action.focus-timer": StartFocusTimer(); break;
            case "action.session-timer": ToggleSessionTimer(); break;
            case "action.reading-list-add": SavePageToReadingList(); break;
            case "action.reading-list": OpenReadingList(); break;
            case "action.quick-note": ShowQuickNote(); break;
            case "action.notes": OpenNotes(); break;
            case "action.weekly-digest": OpenProductivity(showStats: false); break;
            case "action.stats-dashboard": OpenProductivity(showStats: true); break;
            // R5B #202/#203: the conversation tools' fixed doors.
            case "action.bookmarks": OpenBookmarks(); break;
            case "action.export-conversation": _ = ExportConversationAsync(pdf: false); break;
            case "action.export-conversation-pdf": _ = ExportConversationAsync(pdf: true); break;
            case "action.exit": ExitApp(); break;
        }
    }

    /// <summary>The palette's per-window commands (#104): switch, tile, next monitor, always-on-top, focus mode — each applied to the window the command named.</summary>
    private void ExecuteWindowCommand(PaletteCommand command)
    {
        if (command.Payload is not { } windowId)
            return;
        if (command.Id.StartsWith("window.switch.", StringComparison.Ordinal))
        {
            ActivateTrackedWindow(windowId);
            return;
        }
        if (FindTrackedWindow(windowId) is not { } window)
            return;
        if (command.Id.StartsWith("window.tile-preset.", StringComparison.Ordinal))
        {
            // R5 #149: window.tile-preset.<Preset>.<windowId>
            var rest = command.Id["window.tile-preset.".Length..];
            var presetName = rest[..rest.LastIndexOf('.')];
            if (WindowTilingPresets.TryParse(presetName, out var preset))
                TileWindowToPreset(window, preset);
        }
        else if (command.Id.StartsWith("window.tile-left.", StringComparison.Ordinal))
            TileWindow(window, TileSide.Left);
        else if (command.Id.StartsWith("window.tile-right.", StringComparison.Ordinal))
            TileWindow(window, TileSide.Right);
        else if (command.Id.StartsWith("window.next-monitor.", StringComparison.Ordinal))
            MoveWindowToNextMonitor(window);
        else if (command.Id.StartsWith("window.always-on-top.", StringComparison.Ordinal))
        {
            // The main window's pin is a persisted setting; a second
            // window's is a session toggle on the window itself.
            if (windowId == "main")
                SetAlwaysOnTop(!_settings.AlwaysOnTop);
            else
                window.Topmost = !window.Topmost;
        }
        else if (command.Id.StartsWith("window.focus-mode.", StringComparison.Ordinal))
        {
            if (windowId == "main")
                SetFocusMode(!_focusMode);
        }
    }

    private Window? FindTrackedWindow(string id)
    {
        if (id == "main")
            return this;
        foreach (var (window, wid) in _trackedWindows)
            if (wid == id)
                return window;
        return null;
    }

    private void ActivateTrackedWindow(string id)
    {
        if (id == "main")
        {
            ShowFromTray();
            return;
        }
        if (FindTrackedWindow(id) is { } window)
        {
            if (!window.IsVisible)
                window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            _windowMru.Touch(id);
        }
    }

    private static ScreenRect WindowRectDip(Window window)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        return new ScreenRect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    /// <summary>Tile a window to half of its monitor's work area (#104) — the work area, so the taskbar is never covered.</summary>
    private void TileWindow(Window window, TileSide side)
    {
        var areas = MonitorRects.AllWorkAreasInDip();
        if (areas.Count == 0)
            return;
        var index = WindowTiling.IndexOfLargestOverlap(WindowRectDip(window), areas);
        if (index < 0)
            index = 0;
        var half = WindowTiling.Half(areas[index], side);
        if (window.WindowState != WindowState.Normal)
            window.WindowState = WindowState.Normal;
        window.Left = half.Left;
        window.Top = half.Top;
        window.Width = half.Width;
        window.Height = half.Height;
        window.Activate();
    }

    /// <summary>Apply a tiling preset (R5 #149) to a window: Core's geometry over the work area of the monitor the window mostly sits on — the same discipline as TileWindow.</summary>
    private void TileWindowToPreset(Window window, TilePreset preset)
    {
        var areas = MonitorRects.AllWorkAreasInDip();
        if (areas.Count == 0)
            return;
        var index = WindowTiling.IndexOfLargestOverlap(WindowRectDip(window), areas);
        if (index < 0)
            index = 0;
        var rect = WindowTilingPresets.Rect(areas[index], preset);
        if (window.WindowState != WindowState.Normal)
            window.WindowState = WindowState.Normal;
        window.Left = rect.Left;
        window.Top = rect.Top;
        window.Width = rect.Width;
        window.Height = rect.Height;
        window.Activate();
    }

    /// <summary>Move a window to the next monitor (#104), keeping its size (clamped to the target's work area) and its relative position where it fits.</summary>
    private void MoveWindowToNextMonitor(Window window)
    {
        var areas = MonitorRects.AllWorkAreasInDip();
        if (areas.Count < 2)
        {
            SetStatus(Strings.Get("status.there_s_only_one_monitor"));
            return;
        }
        var rect = WindowRectDip(window);
        var current = WindowTiling.IndexOfLargestOverlap(rect, areas);
        var from = areas[Math.Max(0, current)];
        var to = areas[WindowTiling.NextIndex(current, areas.Count)];
        var width = Math.Min(rect.Width, to.Width);
        var height = Math.Min(rect.Height, to.Height);
        var left = to.Left + Math.Clamp(rect.Left - from.Left, 0, Math.Max(0, to.Width - width));
        var top = to.Top + Math.Clamp(rect.Top - from.Top, 0, Math.Max(0, to.Height - height));
        if (window.WindowState != WindowState.Normal)
            window.WindowState = WindowState.Normal;
        window.Left = left;
        window.Top = top;
        window.Width = width;
        window.Height = height;
        window.Activate();
    }

    // ─────────────────── v1.2.0 R2: workspaces & sessions (items 111–120) ───────────────────

    /// <summary>Every open window as the apply planner sees it (#112): the main window plus the tracked secondaries, routes only — a full URL never leaves this method.</summary>
    private IReadOnlyList<OpenWindowState> CurrentOpenWindowStates()
    {
        var states = new List<OpenWindowState>
        {
            new("main", _settings.ActiveProfileId, CurrentMainRoute(), IsMain: true),
        };
        foreach (var (window, id) in _trackedWindows)
        {
            var route = window is SecondaryWindow secondary
                ? AppRoute.FromUrl(secondary.CurrentUrl) ?? AppRoute.Home
                : AppRoute.Home;
            states.Add(new OpenWindowState(id, _settings.ActiveProfileId, route, IsMain: false));
        }
        return states;
    }

    private string CurrentMainRoute() =>
        _webReady ? AppRoute.FromUrl(Browser.Source?.ToString()) ?? AppRoute.Home : AppRoute.Home;

    /// <summary>The current arrangement as workspace windows (#111 capture): profile, route, and placement per open window.</summary>
    private List<WorkspaceWindow> CurrentWorkspaceWindows()
    {
        var windows = new List<WorkspaceWindow>
        {
            new()
            {
                ProfileId = _settings.ActiveProfileId,
                Route = CurrentMainRoute(),
                Placement = PlacementOf(this),
                IsMain = true,
            },
        };
        foreach (var (window, _) in _trackedWindows)
        {
            windows.Add(new WorkspaceWindow
            {
                ProfileId = _settings.ActiveProfileId,
                Route = window is SecondaryWindow secondary
                    ? AppRoute.FromUrl(secondary.CurrentUrl) ?? AppRoute.Home
                    : AppRoute.Home,
                Placement = PlacementOf(window),
            });
        }
        return windows;
    }

    private static WindowPlacement PlacementOf(Window window)
    {
        var rect = WindowRectDip(window);
        return new WindowPlacement
        {
            Left = rect.Left,
            Top = rect.Top,
            Width = rect.Width,
            Height = rect.Height,
            Maximized = window.WindowState == WindowState.Maximized,
        };
    }

    /// <summary>
    /// Put a window at a saved placement: clamped onto the monitors
    /// connected NOW (S6 #60's rule, via Core's PlacementClamp), size
    /// always applied, position only when the placement has one.
    /// </summary>
    private void ApplyPlacementTo(Window window, WindowPlacement placement)
    {
        ScreenRect? clamped = null;
        if (placement.Left is { } left && placement.Top is { } top)
            clamped = PlacementClamp.Clamp(
                new ScreenRect(left, top, placement.Width, placement.Height),
                MonitorRects.AllInDip());
        if (window.WindowState != WindowState.Normal)
            window.WindowState = WindowState.Normal;
        if (clamped is { } rect)
        {
            window.Left = rect.Left;
            window.Top = rect.Top;
            window.Width = rect.Width;
            window.Height = rect.Height;
        }
        else
        {
            window.Width = placement.Width;
            window.Height = placement.Height;
        }
        if (placement.Maximized)
            window.WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// Save the current arrangement as a workspace (#111) — one click,
    /// no prompt: it takes the next free "Workspace N" name and says
    /// where renaming lives (Settings → Workspaces &amp; sessions).
    /// </summary>
    public void SaveCurrentWorkspace()
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        if (_settings.Workspaces.Count >= Workspaces.MaxWorkspaces)
        {
            SetStatus(Strings.Format("status.too_many_workspaces", Workspaces.MaxWorkspaces));
            return;
        }
        var ws = Workspaces.Capture(Workspaces.NextDefaultName(_settings), CurrentWorkspaceWindows());
        _settings.Workspaces.Add(ws);
        _settings.ActiveWorkspaceId = ws.Id;
        _settings.Sanitized();
        SaveSettings();
        ApplyWorkspaceAccent();
        RebuildWorkspacesMenu();
        _log.Info($"Workspace saved: '{ws.Name}' ({ws.Windows.Count} window(s), routes only)");
        SetStatus(Strings.Format("status.saved_workspace", ws.Name));
    }

    private void OnSaveWorkspace(object sender, RoutedEventArgs e) => SaveCurrentWorkspace();

    /// <summary>Apply a saved workspace by id (tray, palette, ⋯ menu, Settings all land here).</summary>
    public void ApplyWorkspaceById(string id)
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        if (Workspaces.Find(_settings, id) is not { } ws)
        {
            SetStatus(Strings.Get("status.that_workspace_no_longer_exists"));
            return;
        }
        ApplyWorkspace(ws, activate: true);
    }

    private enum ArrangementOutcome
    {
        Applied,
        Aborted,
        HandedOffToProfileSwitch,
    }

    /// <summary>
    /// Apply a workspace (#112): plan through Core, then execute —
    /// with the two asks the item requires (a profile switch restarts
    /// the app; closing a window mid-download asks first).
    /// </summary>
    private void ApplyWorkspace(Workspace ws, bool activate)
    {
        var outcome = TryApplyArrangement(ws.Windows, ws.Name, ws);
        if (outcome != ArrangementOutcome.Applied)
            return;
        if (activate)
        {
            ws.LastAppliedUtc = DateTimeOffset.UtcNow;
            _settings.ActiveWorkspaceId = ws.Id;
            ApplyWorkspaceAccent();
            RebuildWorkspacesMenu();
        }
        SaveSettings();
        UpdateSessionJournal();
    }

    /// <summary>
    /// Plan + execute one arrangement (a workspace, a snapshot, or a
    /// journal restore share this path). <paramref name="asWorkspace"/>
    /// is non-null when the arrangement IS a saved workspace — only
    /// then can a profile mismatch hand off through a restart (the
    /// pending-apply id names a workspace; a snapshot or journal
    /// belongs to whatever profile is active when it's restored).
    /// </summary>
    private ArrangementOutcome TryApplyArrangement(
        IReadOnlyList<WorkspaceWindow> windows, string displayName, Workspace? asWorkspace)
    {
        if (!_webReady)
        {
            SetStatus(Strings.Get("status.muse_is_still_starting_try_again_in_a"));
            return ArrangementOutcome.Aborted;
        }
        var plan = WorkspaceApplyPlanner.Plan(
            windows, CurrentOpenWindowStates(), _settings.ActiveProfileId, _downloads.IsTransferring);
        if (plan.Kind == WorkspaceApplyKind.SwitchProfileFirst)
        {
            var target = _settings.Profiles.FirstOrDefault(p => p.Id == plan.RequiredProfileId);
            if (asWorkspace is null || target is null)
            {
                SetStatus(Strings.Format("status.workspace_belongs_to_another_profile", displayName));
                return ArrangementOutcome.Aborted;
            }
            var invoker = FocusReturn.Capture();
            var confirm = System.Windows.MessageBox.Show(this,
                Strings.Format("msg.workspace_other_profile_body", displayName, target.Name),
                Strings.Get("msg.apply_workspace"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                FocusReturn.Restore(invoker);
                return ArrangementOutcome.Aborted;
            }
            // The pending apply survives the restart in settings; the
            // new process applies it once its web view is ready (#112).
            _settings.PendingWorkspaceApplyId = asWorkspace.Id;
            SaveSettings();
            SwitchToProfile(_settings, plan.RequiredProfileId!);
            return ArrangementOutcome.HandedOffToProfileSwitch;
        }
        if (plan.RequiresDownloadConfirmation)
        {
            var invoker = FocusReturn.Capture();
            var confirm = System.Windows.MessageBox.Show(this,
                Strings.Get("msg.workspace_download_in_progress_body"),
                Strings.Get("msg.apply_workspace"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                FocusReturn.Restore(invoker);
                SetStatus(Strings.Get("status.workspace_not_applied"));
                return ArrangementOutcome.Aborted;
            }
        }
        ExecuteApplyPlan(plan);
        if (plan.SkippedOtherProfileCount > 0)
            _log.Info($"Apply '{displayName}': skipped {plan.SkippedOtherProfileCount} window(s) naming other profiles");
        _log.Info($"Applied arrangement '{displayName}': opened {plan.ToOpen.Count}, closed {plan.ToCloseWindowIds.Count}, kept {plan.KeptWindowIds.Count}");
        SetStatus(plan.IsNoOp
            ? Strings.Format("status.workspace_already_applied", displayName)
            : Strings.Format("status.workspace_applied", displayName));
        return ArrangementOutcome.Applied;
    }

    /// <summary>Execute a plan (#112): close the extras, navigate/place the main window (never close it), open the missing windows at their routes and placements.</summary>
    private void ExecuteApplyPlan(WorkspaceApplyPlan plan)
    {
        foreach (var id in plan.ToCloseWindowIds)
            FindTrackedWindow(id)?.Close();
        if (plan.MainWindow is { } mainEntry)
        {
            if (plan.MainRouteChanges && _webReady)
                Browser.CoreWebView2.Navigate(AppRoute.ToUrl(mainEntry.Route));
            if (mainEntry.Placement is { } placement)
                ApplyPlacementTo(this, placement);
        }
        foreach (var entry in plan.ToOpen)
            OpenWorkspaceWindow(entry);
    }

    private void OpenWorkspaceWindow(WorkspaceWindow entry)
    {
        if (!_webReady)
            return;
        try
        {
            var window = new SecondaryWindow(Browser.CoreWebView2.Environment, _log, AppRoute.ToUrl(entry.Route))
                { Owner = this };
            if (entry.Placement is { } placement)
                ApplyPlacementTo(window, placement);
            TrackSecondaryWindow(window);
            window.Show();
        }
        catch (Exception ex)
        {
            _log.Warn($"Opening a workspace window failed: {ex.Message}");
        }
    }

    /// <summary>The active workspace's accent override (#115): session-only — applied over the settings accent via a clone, so the saved preference is never rewritten by applying a layout.</summary>
    private void ApplyWorkspaceAccent()
    {
        var key = Workspaces.Find(_settings, _settings.ActiveWorkspaceId)?.AccentColor;
        if (key == _workspaceAccentKey)
            return;
        _workspaceAccentKey = key;
        if (key is null)
        {
            ThemeManager.Apply(_settings);
            return;
        }
        var clone = _settings.Clone();
        clone.AccentColor = key;
        ThemeManager.Apply(clone);
        _log.Info($"Workspace accent override in effect: {key}");
    }

    /// <summary>Rebuild the ⋯ menu's Workspaces submenu: glyph labels (#119), the active one checked, then the Settings doorway (#120).</summary>
    private void RebuildWorkspacesMenu()
    {
        WorkspacesItem.Items.Clear();
        if (_settings.Workspaces.Count == 0)
        {
            WorkspacesItem.Items.Add(new MenuItem { Header = Strings.Get("ui.no_workspaces_saved_yet"), IsEnabled = false });
        }
        foreach (var ws in _settings.Workspaces)
        {
            var id = ws.Id;
            var item = new MenuItem
            {
                Header = WorkspaceGlyphs.MenuLabel(ws),
                IsChecked = id == _settings.ActiveWorkspaceId,
            };
            item.Click += (_, _) => ApplyWorkspaceById(id);
            WorkspacesItem.Items.Add(item);
        }
        WorkspacesItem.Items.Add(new System.Windows.Controls.Separator());
        var manage = new MenuItem { Header = Strings.Get("ui.manage_workspaces") };
        manage.Click += (_, _) => OpenSettings(SettingsSearch.WorkspacesId);
        WorkspacesItem.Items.Add(manage);
    }

    // ─────────────────── session snapshot (#114) ───────────────────

    private void OnTakeSessionSnapshot(object sender, RoutedEventArgs e) => TakeSessionSnapshot();
    private void OnRestoreSessionSnapshot(object sender, RoutedEventArgs e) => RestoreSessionSnapshot();

    /// <summary>Take the one-off session snapshot (#114): the open windows, routes only, in their own file — deliberately not the journal's.</summary>
    public void TakeSessionSnapshot()
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        var snapshot = SessionSnapshot.Capture(CurrentWorkspaceWindows(), DateTimeOffset.UtcNow);
        try
        {
            SessionSnapshotStore.Save(AppPaths.SessionSnapshotPath, snapshot);
        }
        catch (Exception ex)
        {
            _log.Warn($"Session snapshot save failed: {ex.Message}");
            SetStatus(Strings.Get("status.couldn_t_save_the_session_snapshot"));
            return;
        }
        _log.Info($"Session snapshot taken ({snapshot.Windows.Count} window(s), routes only)");
        SetStatus(Strings.Format("status.session_snapshot_saved", snapshot.Windows.Count));
    }

    /// <summary>Restore the session snapshot (#114) through the same planner as a workspace apply, download guard included.</summary>
    public void RestoreSessionSnapshot()
    {
        if (_isLocked)
        {
            PresentLockWindow();
            return;
        }
        var snapshot = SessionSnapshotStore.Load(AppPaths.SessionSnapshotPath);
        if (snapshot is null)
        {
            SetStatus(Strings.Get("status.no_session_snapshot_yet_take_one_from"));
            return;
        }
        var clean = snapshot.Sanitized(_settings.Profiles.Select(p => p.Id).ToList());
        if (clean.Windows.Count == 0)
        {
            SetStatus(Strings.Get("status.that_snapshot_s_windows_belong_to"));
            return;
        }
        if (TryApplyArrangement(clean.Windows, Strings.Get("main.session_snapshot"), asWorkspace: null)
                == ArrangementOutcome.Applied)
        {
            SaveSettings();
            UpdateSessionJournal();
        }
    }

    // ─────────────────── session journal, crash restore, startup (#113/#117/#118) ───────────────────

    /// <summary>
    /// Rewrite this session's journal with the current arrangement
    /// (#117/#118). Rides the settings-save cadence (saves already
    /// happen per navigation), plus window open/close — the journal is
    /// never more than one change stale, which is all crash evidence
    /// needs to be.
    /// </summary>
    private void UpdateSessionJournal(bool cleanExit = false)
    {
        try
        {
            _journal ??= new SessionJournal();
            _journal.Windows = CurrentWorkspaceWindows();
            _journal.CleanExit = cleanExit;
            _journal.UpdatedUtc = DateTimeOffset.UtcNow;
            SessionJournalStore.Save(AppPaths.SessionJournalPath, _journal);
        }
        catch (Exception ex)
        {
            _log.Warn($"Session journal save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The startup arrangement, run once when the web view is first
    /// ready (and, for a hidden start, deferred to the first real
    /// show — nothing pops at sign-in). Precedence: a pending
    /// workspace apply (a profile switch just restarted us for it)
    /// &gt; the crash journal (offer once / auto-save) &gt; the
    /// startup workspace (#113). Safe mode skips all of it — it
    /// starts at Home, by definition.
    /// </summary>
    private void RunStartupArrangement()
    {
        if (_startupArrangementHandled)
            return;
        _startupArrangementHandled = true;
        if (_safeMode)
        {
            _journal = new SessionJournal();
            UpdateSessionJournal();
            return;
        }
        if (_settings.PendingWorkspaceApplyId is { } pendingId
            && Workspaces.Find(_settings, pendingId) is { } pending)
        {
            _settings.PendingWorkspaceApplyId = null;
            _journal = new SessionJournal();
            ApplyWorkspace(pending, activate: true);
            UpdateSessionJournal();
            return;
        }
        _settings.PendingWorkspaceApplyId = null;

        var journal = SessionJournalStore.Load(AppPaths.SessionJournalPath);
        var decision = CrashRestorePolicy.Decide(journal, _settings.AutoSaveSession);
        var restored = false;
        switch (decision)
        {
            case CrashRestoreDecision.RestoreSilently:
                restored = TryRestoreJournalWindows(journal!);
                if (restored)
                    _log.Info("Session windows restored from the journal (auto-save or post-crash)");
                break;
            case CrashRestoreDecision.OfferRestore:
                var count = journal!.Windows.Count;
                var answer = System.Windows.MessageBox.Show(this,
                    Strings.Format("msg.crash_restore_body", count),
                    Strings.Get("msg.restore_previous_windows"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                    restored = TryRestoreJournalWindows(journal);
                else
                    _log.Info("Crash-session restore offered once and declined");
                break;
            case CrashRestoreDecision.SuppressRestoreLoop:
                _log.Warn("Crash-session restore skipped: the previous session was itself a restore " +
                          "and never loaded a page — starting fresh so it can't loop");
                break;
            default:
                if (_settings.StartupWorkspaceId is { } startupId
                    && Workspaces.Find(_settings, startupId) is { } startup)
                {
                    _journal = new SessionJournal();
                    ApplyWorkspace(startup, activate: true);
                    UpdateSessionJournal();
                    return;
                }
                break;
        }
        _journal = new SessionJournal { WasRestored = restored };
        UpdateSessionJournal();
    }

    /// <summary>Restore a journal's windows through the planner; a journal from another profile is skipped (the windows belong to that profile's session, not this one).</summary>
    private bool TryRestoreJournalWindows(SessionJournal journal)
    {
        var windows = Workspaces.SanitizeWindows(
            journal.Windows, _settings.Profiles.Select(p => p.Id).ToList());
        if (windows.Count == 0)
            return false;
        var plan = WorkspaceApplyPlanner.Plan(
            windows, CurrentOpenWindowStates(), _settings.ActiveProfileId, downloadsInFlight: false);
        if (plan.Kind != WorkspaceApplyKind.Apply)
        {
            _log.Info("Session restore skipped: the saved windows belong to another profile");
            return false;
        }
        ExecuteApplyPlan(plan);
        return true;
    }

    /// <summary>Show a downloaded file in Explorer — the Downloads window's own reveal behavior, for the palette's download commands (#102).</summary>
    private void RevealDownload(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"")
                    { UseShellExecute = true });
            }
            else if (Path.GetDirectoryName(fullPath) is { } dir && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                    { UseShellExecute = true });
            }
            else
            {
                SetStatus(Strings.Get("status.that_file_s_folder_no_longer_exists"));
            }
        }
        catch (Exception ex) { _log.Warn($"Palette reveal download failed: {ex.Message}"); }
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
                SetStatus(Strings.Get("status.there_s_no_log_file_yet"));
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
            Strings.Format("msg.reinstall_body", version),
            Strings.Get("msg.reinstall_previous_version"), MessageBoxButton.OKCancel, MessageBoxImage.Question);
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
                Strings.Format("msg.reinstall_failed_body", installerPath),
                Strings.Get("msg.reinstall_previous_version"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
                (Strings.Get("main.diag_active_profile"), ProfileDataInfo.MeasureFolderBytes(_profileDir)),
                (Strings.Get("main.diag_settings_data"), ProfileDataInfo.MeasureFolderBytes(AppPaths.RootDir)),
            },
            RecentErrors = LogReading.RecentErrors(logText, 5),
            // R7 #164/#169: the language coverage report and the
            // announcement history, composed by Core from the merged
            // catalog and the recorded history.
            ActiveLanguageLine = $"{Strings.Current.Language} ({Strings.ActiveLanguageName})",
            LanguageCoverageLines = LocalizationCoverage.ReportLines(Strings.MergedTables()),
            RecentAnnouncements = _announcementHistory.Recent(10)
                .Select(AnnouncementHistory.DescribeLine).ToList(),
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
            ? Strings.Get("main.shot_locked")
            : Strings.Get("main.shot_invisible");
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
            Title = Strings.Get("ui.save_support_bundle"),
            FileName = SupportBundle.DefaultFileName(DateTimeOffset.Now),
            Filter = Strings.Get("dlg.filter_zip"),
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
            SetStatus(Strings.Get("status.support_bundle_saved"));
            Announcer.Polite(Strings.Get("ui.support_bundle_saved"));
        }
        catch (Exception ex)
        {
            _log.Error($"Support bundle export failed: {ex.Message}");
            System.Windows.MessageBox.Show(owner,
                Strings.Get("msg.bundle_write_failed_body"),
                Strings.Get("msg.export_support_bundle"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
        // R1 #101: Ctrl+K opens the command palette. A fixed binding,
        // deliberately not in the remappable catalog — K is the
        // convention for this surface, and Core's conflict rules keep
        // every remappable gesture clear of it in the other direction.
        if (key == Key.K && modifiers == ModifierKeys.Control)
        {
            OpenPalette();
            e.Handled = true;
            return;
        }
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
        // R1 #105: go-to-profile chords (Ctrl+Shift+1..9), in-app
        // only. Checked AFTER the remappable actions on purpose: a
        // user who deliberately remapped an action onto a chord
        // gesture keeps their binding, and the chord steps aside.
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift)
            && ProfileChords.TryIndex(
                new ShortcutGesture(ToGestureModifiers(modifiers), key.ToString()), out var chordIndex)
            && ProfileChords.ProfileIdAt(Profiles.Visible(_settings.Profiles), chordIndex) is { } chordProfileId)
        {
            if (chordProfileId != _settings.ActiveProfileId)
                SwitchToProfileFromMenu(chordProfileId);
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
