using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
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
    private bool _isFullScreen;
    private WindowState _preFullScreenState;
    private WindowStyle _preFullScreenStyle;
    private CoreWebView2DownloadOperation? _download;

    public MainWindow(AppSettings settings, DiagnosticLog log)
    {
        _settings = settings;
        _log = log;
        InitializeComponent();
        SetWindowIcon();
        ApplySavedPlacement();
        BuildTray();
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
        CloseToTrayItem.IsChecked = _settings.CloseToTray;
        AutostartItem.IsChecked = _settings.StartWithWindows;
        CloseToTrayItem.Click += (_, _) =>
        {
            _settings.CloseToTray = CloseToTrayItem.IsChecked;
            SaveSettings();
        };
        AutostartItem.Click += (_, _) =>
        {
            _settings.StartWithWindows = AutostartItem.IsChecked;
            Autostart.Sync(_settings.StartWithWindows);
            SaveSettings();
        };
        await InitWebViewAsync();
    }

    /// <summary>Started via --background: create the window, then park it in the tray.</summary>
    public void StartHidden()
    {
        Show();
        HideToTray(showBalloon: false);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    private void HideToTray(bool showBalloon)
    {
        Hide();
        if (showBalloon && !_balloonShown && _tray is not null)
        {
            _balloonShown = true;
            _tray.BalloonTipTitle = "MuseDesktop is still running";
            _tray.BalloonTipText = "Muse keeps working in the cloud. Double-click the tray icon to come back.";
            _tray.ShowBalloonTip(4000);
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
            Directory.CreateDirectory(AppPaths.WebViewDataDir);
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppPaths.WebViewDataDir);
            await Browser.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
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
            _log.Error($"WebView2 init failed: {ex.GetType().Name}: {ex.Message}");
            ShowOverlay(
                "Muse could not start",
                $"Something went wrong starting the web view ({ex.Message}). " +
                $"The log at {AppPaths.LogPath} has the details.",
                "Retry", null);
            return;
        }

        var core = Browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
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
        _log.Info($"WebView2 {envVersion()} ready; profile {AppPaths.WebViewDataDir}");
        core.Navigate(_settings.StartupUrl);
        SetStatus("Loading Muse…");

        string envVersion()
        {
            try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { return "(unknown version)"; }
        }
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
        e.Handled = true;
        _download = e.DownloadOperation;
        var name = Path.GetFileName(e.ResultFilePath);
        _log.Info($"Download started: {name}");
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
                SetStatus($"Download complete: {name} (in your Downloads folder)");
                break;
            case CoreWebView2DownloadState.Interrupted:
                _log.Warn($"Download interrupted: {name} ({_download.InterruptReason})");
                SetStatus($"Download interrupted: {name} ({_download.InterruptReason})");
                break;
        }
        if (_download.State != CoreWebView2DownloadState.InProgress)
        {
            _download.BytesReceivedChanged -= OnDownloadProgress;
            _download.EstimatedEndTimeChanged -= OnDownloadProgress;
            _download.StateChanged -= OnDownloadStateChanged;
            _download = null;
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
            $"Web profile: {AppPaths.WebViewDataDir}\n\n" +
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
