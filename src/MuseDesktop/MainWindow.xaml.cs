using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using MuseDesktop.Core;
using Forms = System.Windows.Forms;

namespace MuseDesktop;

public partial class MainWindow : Window
{
    // Microsoft's official Evergreen bootstrapper link (see DECISIONS.md).
    private const string WebView2BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private readonly AppSettings _settings;
    private readonly DiagnosticLog _log;
    private Forms.NotifyIcon? _tray;
    private bool _reallyExit;
    private bool _balloonShown;
    private bool _webReady;
    private bool _webInitFailed;
    private bool _isFullScreen;
    private WindowState _preFullScreenState;
    private WindowStyle _preFullScreenStyle;
    private CoreWebView2DownloadOperation? _download;
    private string? _completedDownloadPath;

    // v0.2.0 — update check
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private bool _checkingUpdate;
    private string? _updateTag;
    private string? _updateUrl;

    // v0.2.0 — tray suspend: after this long hidden, the renderer sleeps.
    private static readonly TimeSpan SuspendDelay = TimeSpan.FromMinutes(5);
    private DispatcherTimer? _suspendTimer;
    private bool _webSuspended;

    // v0.3.0 — the active profile's WebView2 data folder (per-profile sign-ins).
    private readonly string _profileDir;

    public MainWindow(AppSettings settings, DiagnosticLog log)
    {
        _settings = settings;
        _log = log;
        _profileDir = AppPaths.ProfileDir(settings.ActiveProfileId);
        InitializeComponent();
        SetWindowIcon();
        ApplySavedPlacement();
        BuildTray();
        RefreshProfileIndicator();
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += (_, _) => UpdateTrayMenuState();
        PreviewKeyDown += OnPreviewKeyDown;
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
        ResumeWebViewIfSuspended();
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    private void HideToTray(bool showBalloon)
    {
        Hide();
        ArmSuspendTimer();
        if (showBalloon && !_balloonShown && _tray is not null)
        {
            _balloonShown = true;
            _tray.BalloonTipTitle = "MuseDesktop is still running";
            _tray.BalloonTipText = "Muse keeps working in the cloud. Double-click the tray icon to come back.";
            _tray.ShowBalloonTip(4000);
        }
    }

    // ───────────────────────── tray suspend (v0.2.0) ─────────────────────────

    private void ArmSuspendTimer()
    {
        if (!_settings.SuspendWhenInTray || !_webReady || _webSuspended)
            return;
        _suspendTimer?.Stop();
        _suspendTimer = new DispatcherTimer { Interval = SuspendDelay };
        _suspendTimer.Tick += OnSuspendTimerTick;
        _suspendTimer.Start();
    }

    private async void OnSuspendTimerTick(object? sender, EventArgs e)
    {
        _suspendTimer?.Stop();
        if (IsVisible || !_webReady || _webSuspended)
            return;
        if (_download is not null)
        {
            // Never sleep mid-download; check again shortly.
            _suspendTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            _suspendTimer.Tick += OnSuspendTimerTick;
            _suspendTimer.Start();
            return;
        }
        try
        {
            var ok = await Browser.CoreWebView2.TrySuspendAsync();
            _webSuspended = ok;
            _log.Info(ok
                ? "WebView2 suspended (window hidden in tray)"
                : "WebView2 suspend declined by the page (active media/call?)");
        }
        catch (Exception ex)
        {
            _log.Warn($"WebView2 suspend failed: {ex.Message}");
        }
    }

    private void ResumeWebViewIfSuspended()
    {
        _suspendTimer?.Stop();
        if (!_webSuspended || !_webReady)
            return;
        try
        {
            Browser.CoreWebView2.Resume();
            _webSuspended = false;
            _log.Info("WebView2 resumed");
        }
        catch (Exception ex)
        {
            _log.Warn($"WebView2 resume failed: {ex.Message}");
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
        _tray?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        Close();
    }

    // ───────────────────────── placement / settings ─────────────────────────

    private void ApplySavedPlacement()
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top
            && left + 80 >= SystemParameters.VirtualScreenLeft
            && top + 40 >= SystemParameters.VirtualScreenTop
            && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
            && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
        {
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
        ZoomLabel.Text = ZoomMath.FormatPercent(_settings.ZoomFactor);
    }

    private void SaveSettings()
    {
        try
        {
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            var bounds = RestoreBounds;
            if (!bounds.IsEmpty)
            {
                _settings.WindowLeft = bounds.Left;
                _settings.WindowTop = bounds.Top;
                _settings.WindowWidth = bounds.Width;
                _settings.WindowHeight = bounds.Height;
            }
            if (_webReady)
            {
                _settings.ZoomFactor = Browser.ZoomFactor;
                var source = Browser.Source?.ToString();
                if (source is not null
                    && UrlPolicy.ClassifyNavigation(source) == LinkDisposition.InApp
                    && Uri.TryCreate(source, UriKind.Absolute, out var u)
                    && UrlPolicy.IsInAppHost(u.Host))
                {
                    _settings.LastUrl = source;
                }
            }
            SettingsStore.Save(AppPaths.SettingsPath, _settings);
        }
        catch (Exception ex)
        {
            _log.Warn($"Settings save failed: {ex.Message}");
        }
    }

    // ───────────────────────── tray ─────────────────────────

    private void BuildTray()
    {
        try
        {
            BuildTrayCore();
        }
        catch (Exception ex)
        {
            // The tray is a convenience; the window must still start.
            _log.Warn($"Tray creation failed: {ex.Message}");
            _tray = null;
        }
    }

    private void BuildTrayCore()
    {
        _tray = new Forms.NotifyIcon
        {
            Text = "MuseDesktop",
            Visible = true,
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "")
                   ?? System.Drawing.SystemIcons.Application,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Muse", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Reload", null, (_, _) => Dispatcher.Invoke(() => Browser.Reload()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        UpdateTrayMenuState();
    }

    private void UpdateTrayMenuState()
    {
        // The tray icon exists for the app's whole lifetime; nothing dynamic
        // is needed yet, but menu state hooks live here as features grow.
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
        core.ContainsFullScreenElementChanged += OnFullScreenElementChanged;
        core.ProcessFailed += OnProcessFailed;
        Browser.ZoomFactorChanged += (_, _) =>
        {
            ZoomLabel.Text = ZoomMath.FormatPercent(Browser.ZoomFactor);
        };

        Browser.ZoomFactor = ZoomMath.Clamp(_settings.ZoomFactor);
        _webReady = true;
        _log.Info($"WebView2 {envVersion()} ready; profile {_profileDir}");
        core.Navigate(_settings.StartupUrl);
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
            SaveSettings();
        }
        else
        {
            _log.Warn($"Navigation failed: {e.WebErrorStatus} for {UrlPolicy.RedactForLog(Browser.Source?.ToString())}");
            ShowOverlay(
                "Muse couldn't be reached",
                $"The page failed to load ({e.WebErrorStatus}). Check your connection, then retry. " +
                "Your sign-in and chats are stored by Muse in the cloud — nothing is lost.",
                "Retry", null);
            SetStatus($"Load failed: {e.WebErrorStatus}");
        }
        UpdateNavButtons();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
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
        }
        else
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
            _log.Info($"Denied {e.PermissionKind} for {UrlPolicy.RedactForLog(e.Uri)}");
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // We report progress in the status bar, so suppress WebView2's own
        // download flyout and follow the operation ourselves.
        var suggestedName = Path.GetFileName(e.ResultFilePath);

        // v0.2.0: honor the downloads folder / ask-where-to-save settings.
        if (_settings.AskWhereToSave)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = suggestedName,
                InitialDirectory = _settings.DownloadsFolder ?? AppPaths.DownloadsHint,
                Title = "Save download",
            };
            if (dialog.ShowDialog(this) == true)
            {
                e.ResultFilePath = dialog.FileName;
            }
            else
            {
                e.Cancel = true;
                _log.Info($"Download cancelled at save prompt: {suggestedName}");
                SetStatus("Download cancelled");
                return;
            }
        }
        else if (!string.IsNullOrWhiteSpace(_settings.DownloadsFolder))
        {
            try
            {
                Directory.CreateDirectory(_settings.DownloadsFolder);
                e.ResultFilePath = Path.Combine(_settings.DownloadsFolder, suggestedName);
            }
            catch (Exception ex)
            {
                _log.Warn($"Downloads folder unusable, keeping default: {ex.Message}");
            }
        }

        e.Handled = true;
        _download = e.DownloadOperation;
        _completedDownloadPath = null;
        var name = Path.GetFileName(e.ResultFilePath);
        _log.Info($"Download started: {name}");
        CancelDownloadButton.Visibility = Visibility.Visible;
        StatusText.ToolTip = "";
        SetStatus($"Downloading {name}…");

        _download.BytesReceivedChanged += OnDownloadProgress;
        _download.EstimatedEndTimeChanged += OnDownloadProgress;
        _download.StateChanged += OnDownloadStateChanged;
    }

    private void OnDownloadProgress(object? sender, object e) => ReportDownloadProgress();

    private void ReportDownloadProgress()
    {
        if (_download is null) return;
        var name = Path.GetFileName(_download.ResultFilePath);
        if (_download.TotalBytesToReceive is { } total && total > 0)
        {
            var pct = (int)(_download.BytesReceived * 100L / (long)total);
            var eta = _download.EstimatedEndTime - DateTimeOffset.Now;
            var etaText = eta > TimeSpan.Zero ? $", about {FormatEta(eta)} left" : "";
            SetStatus($"Downloading {name} — {pct}%{etaText}", $"{_download.BytesReceived / 1024} KB of {total / 1024} KB");
        }
        else
        {
            SetStatus($"Downloading {name}…", $"{_download.BytesReceived / 1024} KB received");
        }
    }

    private void OnDownloadStateChanged(object? sender, object e)
    {
        if (_download is null) return;
        var name = Path.GetFileName(_download.ResultFilePath);
        switch (_download.State)
        {
            case CoreWebView2DownloadState.Completed:
                _log.Info($"Download completed: {name}");
                _completedDownloadPath = _download.ResultFilePath;
                StatusText.ToolTip = "Click to open the file";
                SetStatus($"Download complete: {name} — click to open");
                break;
            case CoreWebView2DownloadState.Interrupted:
                var cancelled = _download.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled;
                _log.Warn($"Download interrupted: {name} ({_download.InterruptReason})");
                SetStatus(cancelled ? $"Download cancelled: {name}" : $"Download interrupted: {name} ({_download.InterruptReason})");
                break;
        }
        if (_download.State != CoreWebView2DownloadState.InProgress)
        {
            _download.BytesReceivedChanged -= OnDownloadProgress;
            _download.EstimatedEndTimeChanged -= OnDownloadProgress;
            _download.StateChanged -= OnDownloadStateChanged;
            _download = null;
            CancelDownloadButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e)
    {
        try { _download?.Cancel(); }
        catch (Exception ex) { _log.Warn($"Cancel download failed: {ex.Message}"); }
    }

    private void OnStatusTextClick(object sender, MouseButtonEventArgs e)
    {
        if (_completedDownloadPath is { } path && File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _log.Warn($"Open downloaded file failed: {ex.Message}");
                SetStatus($"Couldn't open {Path.GetFileName(path)} — it's in your downloads folder");
            }
        }
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalMinutes >= 1) return $"{(int)Math.Ceiling(eta.TotalMinutes)} min";
        return $"{Math.Max(1, (int)Math.Ceiling(eta.TotalSeconds))} sec";
    }

    private void OnTitleChanged(object? sender, object e)
    {
        var title = Browser.CoreWebView2.DocumentTitle;
        Title = string.IsNullOrWhiteSpace(title) ? "Muse" : $"Muse — {title}";
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Muse" : title;
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

    private void OnBack(object sender, RoutedEventArgs e) { if (_webReady && Browser.CanGoBack) Browser.GoBack(); }
    private void OnForward(object sender, RoutedEventArgs e) { if (_webReady && Browser.CanGoForward) Browser.GoForward(); }

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
        if (_webReady) Browser.ZoomFactor = ZoomMath.StepUp(Browser.ZoomFactor);
    }

    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        if (_webReady) Browser.ZoomFactor = ZoomMath.StepDown(Browser.ZoomFactor);
    }

    private void OnOpenInBrowser(object sender, RoutedEventArgs e)
    {
        var src = Browser.Source?.ToString();
        if (!string.IsNullOrEmpty(src)) OpenExternal(src);
    }

    private void OnMenu(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MenuButton;
        MoreMenu.IsOpen = true;
    }

    // ───────────────────────── settings (v0.2.0) ─────────────────────────

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var clone = _settings.Clone();
        var dialog = new SettingsWindow(clone, _log, ClearBrowsingDataAsync, SwitchToProfile) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var removedProfileIds = _settings.Profiles
            .Select(p => p.Id)
            .Where(id => clone.Profiles.All(p => p.Id != id))
            .ToList();

        _settings.CloseToTray = clone.CloseToTray;
        _settings.StartWithWindows = clone.StartWithWindows;
        _settings.SuspendWhenInTray = clone.SuspendWhenInTray;
        _settings.CheckForUpdates = clone.CheckForUpdates;
        _settings.AskWhereToSave = clone.AskWhereToSave;
        _settings.DownloadsFolder = clone.DownloadsFolder;
        _settings.ZoomFactor = clone.ZoomFactor;
        _settings.Profiles = clone.Profiles;
        _settings.ActiveProfileId = clone.ActiveProfileId;
        _settings.Sanitized();
        Autostart.Sync(_settings.StartWithWindows);
        if (_webReady)
            Browser.ZoomFactor = ZoomMath.Clamp(_settings.ZoomFactor);
        if (!_settings.SuspendWhenInTray)
            _suspendTimer?.Stop();
        SaveSettings();
        RefreshProfileIndicator();

        // Delete the data folders of profiles removed in the dialog — except
        // the one this process is running (WebView2 holds it open; it stays
        // orphaned on disk, noted in KNOWN_LIMITATIONS).
        foreach (var id in removedProfileIds)
        {
            if (AppPaths.ProfileDir(id) == _profileDir)
            {
                _log.Info($"Profile {id} removed while running; its folder is kept until it is no longer in use");
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
                _log.Warn($"Removing profile {id} folder failed: {ex.Message}");
            }
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

    /// <summary>Shows the active profile name in the ⋯ menu and tray tooltip when more than one profile exists.</summary>
    private void RefreshProfileIndicator()
    {
        var active = _settings.Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfileId);
        var multi = _settings.Profiles.Count > 1;
        ProfileItem.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        ProfileSeparator.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        if (multi && active is not null)
            ProfileItem.Header = $"Profile: {active.Name}";
        if (_tray is not null)
            _tray.Text = multi && active is not null ? $"MuseDesktop — {active.Name}" : "MuseDesktop";
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
    /// One quiet request to GitHub's latest-release endpoint. Any failure
    /// (offline, rate limit, odd payload) is a log line, never a dialog —
    /// the app is offline-first and updates are a courtesy.
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
            using var request = new HttpRequestMessage(HttpMethod.Get, UpdateInfo.LatestReleaseApiUrl);
            var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
            request.Headers.UserAgent.ParseAdd($"MuseDesktop/{version.ToString(3)}");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"Update check: HTTP {(int)response.StatusCode}");
                if (manual) SetStatus("Couldn't check for updates — GitHub didn't answer");
                return;
            }
            var json = await response.Content.ReadAsStringAsync();
            var latest = UpdateInfo.ParseLatestReleaseJson(json);
            if (latest is null)
            {
                if (manual) SetStatus("Couldn't read the update information");
                return;
            }
            if (UpdateInfo.IsNewer(latest.Value.Tag, version))
            {
                _updateTag = latest.Value.Tag;
                _updateUrl = latest.Value.Url;
                UpdateItem.Header = $"⬆ Update available: {latest.Value.Tag}";
                UpdateItem.ToolTip = "Open the release page";
                SetStatus($"MuseDesktop {latest.Value.Tag} is available — see the ⋯ menu");
                _log.Info($"Update available: {latest.Value.Tag} (running {version.ToString(3)})");
            }
            else
            {
                _log.Info($"Update check: up to date ({latest.Value.Tag})");
                if (manual) SetStatus($"You're up to date (v{version.ToString(3)})");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Update check failed: {ex.Message}");
            if (manual) SetStatus("Couldn't check for updates — are you offline?");
        }
        finally
        {
            _checkingUpdate = false;
        }
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
        System.Windows.MessageBox.Show(this,
            $"MuseDesktop {version}\n\n" +
            "A native Windows client for Muse at muse.ai.\n" +
            $"Settings: {AppPaths.SettingsPath}\n" +
            $"Log: {AppPaths.LogPath}\n" +
            $"Web profile: {_profileDir}\n\n" +
            "MuseDesktop is an independent open-source project (MIT) and is not made by Meta.",
            "About MuseDesktop", MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.R: OnReload(this, e); e.Handled = true; break;
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
            Toolbar.Visibility = Visibility.Visible;
        }
        _isFullScreen = on;
    }

    // ───────────────────────── dark title bar ─────────────────────────

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var on = 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 10 2004+ / Windows 11).
            _ = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
        }
        catch { /* cosmetic only */ }
    }
}
