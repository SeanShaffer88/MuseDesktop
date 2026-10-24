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
    private bool _autoRetried;
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

    public MainWindow(AppSettings settings, DiagnosticLog log)
    {
        _settings = settings;
        _log = log;
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
            CurrentNotificationSuppression);

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
        // always tell page focus from shell focus.
        Browser.GotKeyboardFocus += (_, _) =>
            WebFocusRing.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        Browser.LostKeyboardFocus += (_, _) =>
            WebFocusRing.BorderBrush = System.Windows.Media.Brushes.Transparent;

        PinItem.IsChecked = _settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        SetWindowIcon();
        ApplySavedPlacement();
        _tray.Build();
        RefreshProfileIndicator();
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

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
        try
        {
            _taskbar = new TaskbarProgress(new WindowInteropHelper(this).Handle, _log);
        }
        catch (Exception ex) { _log.Warn($"Taskbar progress init failed: {ex.Message}"); }
        await InitWebViewAsync();
        if (_settings.CheckForUpdates)
            _ = CheckForUpdatesAsync(manual: false);
    }

    /// <summary>Started via --background: create the window, then park it in the tray.</summary>
    public void StartHidden()
    {
        Show();
        HideToTray(showBalloon: false);
    }

    public void ShowFromTray()
    {
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
    }

    // ── tray delegates (v0.8.0, S4 #32): the tray menu's window actions.
    // Each shows the window first where the action needs a visible
    // owner, then runs the exact handler the ⋯ menu uses — no
    // duplicated logic behind the tray's back.

    private void OpenDownloadsFromTray()
    {
        ShowFromTray();
        OnDownloads(this, new RoutedEventArgs());
    }

    /// <summary>Open Settings on a visible window (tray menu and the --settings launch flag, S4 #32/#33).</summary>
    public void OpenSettingsWindow()
    {
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
        }
        catch (Exception ex)
        {
            _log.Warn($"Settings save failed: {ex.Message}");
        }
    }

    // ───────────────────────── WebView2 ─────────────────────────

    private async Task InitWebViewAsync()
    {
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
        core.Settings.IsGeneralAutofillEnabled = true;
        core.Settings.IsPasswordAutosaveEnabled = true;

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
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
        core.Navigate(startupUrl);
        SetStatus("Loading Muse…");

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
            SetStatus("That link can't open inside MuseDesktop");
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            Overlay.Visibility = Visibility.Collapsed;
            SetStatus("Ready");
            _autoRetried = false;
            // S4 #35: a new document ends whatever media capture the
            // old one held, so the microphone-grant heuristic resets.
            _micGrantedThisSession = false;
            var src = Browser.Source?.ToString();
            if (src is not null)
                _history.Record(src, Browser.CoreWebView2.DocumentTitle);
            SaveSettings();
        }
        else
        {
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

    /// <summary>
    /// Auto-retry once (v0.5.0, S1 #10): when the network reports a change
    /// while a load-failure overlay is showing and connectivity is back,
    /// reload a single time. The flag resets on the next successful load,
    /// so a flapping network cannot cause a retry loop.
    /// </summary>
    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_webReady || _autoRetried || Overlay.Visibility != Visibility.Visible)
                return;
            if (!NetworkInterface.GetIsNetworkAvailable())
                return;
            _autoRetried = true;
            _log.Info("Network changed while a load failure was showing; auto-retrying once");
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
        UpdateNavButtons();
    }

    private void OnFullScreenElementChanged(object? sender, object e)
    {
        // Page content (e.g. a video) asked for full screen.
        SetFullScreen(Browser.CoreWebView2.ContainsFullScreenElement);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _log.Error($"WebView2 process failed: {e.ProcessFailedKind}");
        _history.NoteClosedPage(Browser.Source?.ToString());
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            ShowOverlay(
                "The page stopped responding",
                "Muse's web view closed unexpectedly. Reloading usually fixes it, and your work is saved in the cloud.",
                "Reload Muse", null);
        }
        SetStatus($"Web view problem: {e.ProcessFailedKind}");
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
        MoreMenu.PlacementTarget = MenuButton;
        MoreMenu.IsOpen = true;
    }

    // ───────────────────────── settings (v0.2.0) ─────────────────────────

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var clone = _settings.Clone();
        var dialog = new SettingsWindow(clone, _log, ClearBrowsingDataAsync, SwitchToProfile) { Owner = this };
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
        _settings.Sanitized();
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

    /// <summary>
    /// Wipes the WebView2 profile's cookies/cache/site data (the Settings
    /// window's clear-data action). Muse's chats live server-side; this is a
    /// local sign-out + reset.
    /// </summary>
    public async Task<bool> ClearBrowsingDataAsync()
    {
        if (!_webReady || Browser.CoreWebView2 is null)
            return false;
        try
        {
            await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.AllProfile);
            _log.Info("Browsing data cleared from Settings");
            Browser.CoreWebView2.Navigate(UrlPolicy.HomeUrl);
            SetStatus("Browsing data cleared — sign in to Muse to continue");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Clear browsing data failed: {ex.Message}");
            return false;
        }
    }

    // ───────────────────────── update check (v0.2.0) ─────────────────────────

    private void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        if (_updateUrl is not null && _updateTag is not null)
        {
            OpenExternal(_updateUrl);
            return;
        }
        _ = CheckForUpdatesAsync(manual: true);
    }

    /// <summary>
    /// Runs the update check (UpdateService) and applies the outcome to
    /// the menu and status bar. Manual checks report every outcome;
    /// the automatic launch check only speaks up when an update exists.
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
            var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
            var outcome = await UpdateService.CheckAsync(version, _log);
            switch (outcome.Kind)
            {
                case UpdateOutcomeKind.Available:
                    _updateTag = outcome.Tag;
                    _updateUrl = outcome.Url;
                    UpdateItem.Header = $"⬆ Update available: {outcome.Tag}";
                    UpdateItem.ToolTip = "Open the release page";
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

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var version = typeof(App).Assembly.GetName().Version;
        var invoker = FocusReturn.Capture();
        System.Windows.MessageBox.Show(this,
            $"MuseDesktop {version}\n\n" +
            "A native Windows client for Muse at muse.ai.\n" +
            $"Settings: {AppPaths.SettingsPath}\n" +
            $"Log: {AppPaths.LogPath}\n" +
            $"Web profile: {_profileDir}\n\n" +
            "MuseDesktop is an independent open-source project (MIT) and is not made by Meta.",
            "About MuseDesktop", MessageBoxButton.OK, MessageBoxImage.Information);
        FocusReturn.Restore(invoker);
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
