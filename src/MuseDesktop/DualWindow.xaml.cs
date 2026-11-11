using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MuseDesktop.Core;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MuseDesktop;

/// <summary>
/// Two live, independent pages. Opt-in tools never submit a composer.
/// The owner closes this window on app lock.
/// </summary>
public partial class DualWindow : Window
{
    private sealed class Pane
    {
        public required Grid Host { get; init; }
        public required TextBlock Status { get; init; }
        public required Button Back { get; init; }
        public required Button Forward { get; init; }
        public required Button ZoomReset { get; init; }
        public required string Name { get; set; }
        public string ProfileId = Profiles.DefaultId;
        public int Generation;
        public bool Covered;
        public bool Failed;
        public bool PendingReply;
        public readonly DualReplyTracker Replies = new();
        public ComboBox? ProfilePicker;
        public Window? Detached;
        public Grid? DetachedHost;
        public WebView2? Browser;
        public CoreWebView2? Core => Browser?.CoreWebView2;
    }

    private readonly AppSettings _settings;
    private readonly DiagnosticLog _log;
    private readonly string _profileId;
    private readonly (string Left, string Right) _urls;
    private readonly Func<string?, QuickAskContinueRefusal?> _openInMain;
    private readonly Action<string, CoreWebView2DownloadStartingEventArgs> _download;
    private readonly Pane _left;
    private readonly Pane _right;
    private readonly DualSessions _sessions;
    private readonly DualSession _session;
    private bool _closed;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DualWindow(AppSettings settings, DiagnosticLog log, string? currentUrl,
        Func<string?, QuickAskContinueRefusal?> openInMain,
        Action<string, CoreWebView2DownloadStartingEventArgs> download)
    {
        _settings = settings;
        _log = log;
        _profileId = settings.ActiveProfileId;
        _sessions = DualSessionStore.Load(AppPaths.DualSessionPath);
        var saved = _sessions.Find(_profileId);
        _urls = saved is null
            ? DualMode.InitialUrls(currentUrl)
            : ("https://muse.ai" + saved.LeftRoute, "https://muse.ai" + saved.RightRoute);
        _session = _sessions.ForProfile(_profileId);
        if (saved is null)
            _session.LeftZoom = _session.RightZoom = Profiles.EffectiveZoom(
                settings.Profiles.FirstOrDefault(p => p.Id == _profileId),
                settings.ZoomFactor, settings.ExtendedZoomRange);
        _openInMain = openInMain;
        _download = download;
        InitializeComponent();
        _left = new Pane { Host = LeftHost, Status = LeftStatus, Back = LeftBack,
            Forward = LeftForward, ZoomReset = LeftZoomReset, Name = Strings.Get("dual.left") };
        _right = new Pane { Host = RightHost, Status = RightStatus, Back = RightBack,
            Forward = RightForward, ZoomReset = RightZoomReset, Name = Strings.Get("dual.right") };
        InitializeWorkbench();
        ApplyDivider(_session.LeftFraction);
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.WhenAll(StartProfileAsync(_left, _session.LeftProfileId ?? _profileId, _urls.Left),
                StartProfileAsync(_right, _session.RightProfileId ?? _profileId, _urls.Right));
            _ready.TrySetResult(!_closed && _left.Core is not null && _right.Core is not null &&
                !_left.Failed && !_right.Failed);
            if (!_closed) _left.Browser?.Focus();
        }
        catch (Exception ex)
        {
            _ready.TrySetResult(false);
            if (_closed) return;
            _left.Status.Text = _right.Status.Text = Strings.Get("dual.failed");
            // Exception messages may carry an address or a local path.
            _log.Error($"Dual environment initialization failed: {ex.GetType().Name}");
        }
    }

    private async Task StartPaneAsync(Pane pane, CoreWebView2Environment env, string url, int generation)
    {
        var browser = new WebView2 { DefaultBackgroundColor = ThemeManager.BrowserBackground,
            Visibility = pane.Covered ? Visibility.Hidden : Visibility.Visible };
        AutomationProperties.SetName(browser, pane.Name);
        (pane.DetachedHost ?? pane.Host).Children.Add(browser);
        pane.Browser = browser;
        pane.Status.Text = Strings.Get("dual.loading");
        try
        {
            await browser.EnsureCoreWebView2Async(env);
            if (_closed || generation != pane.Generation || !ReferenceEquals(browser, pane.Browser)) return;
            var core = browser.CoreWebView2;
            ApprovalAutoAcceptController.Attach(this, browser, pane.ProfileId);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = _settings.GeneralAutofillEnabled;
            core.NavigationStarting += (_, e) =>
            {
                if (_closed || generation != pane.Generation) { e.Cancel = true; return; }
                if (UrlPolicy.ClassifyNavigation(e.Uri) == LinkDisposition.Block)
                {
                    e.Cancel = true;
                    pane.Status.Text = Strings.Get("dual.blocked");
                    return;
                }
                pane.Status.Text = Strings.Get("dual.loading");
                pane.Replies.Reset();
                pane.PendingReply = false;
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (_closed || generation != pane.Generation) return;
                pane.Status.Text = Strings.Get(e.IsSuccess ? "dual.ready" : "dual.failed");
                if (e.IsSuccess) RememberChat(pane, core.Source);
                UpdateNavigation(pane);
                if (e.IsSuccess) _ = ApplyPageToolsAsync(pane);
            };
            // Muse creates chat routes with history.pushState; those
            // same-document changes need remembering as well as full loads.
            core.SourceChanged += (_, e) =>
            {
                if (!_closed && generation == pane.Generation && !e.IsNewDocument)
                {
                    RememberChat(pane, core.Source);
                    _ = ApplyPageToolsAsync(pane);
                }
            };
            core.HistoryChanged += (_, _) => UpdateNavigation(pane);
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                switch (UrlPolicy.ClassifyNewWindow(e.Uri))
                {
                    case LinkDisposition.InApp:
                        core.Navigate(e.Uri);
                        break;
                    case LinkDisposition.ExternalBrowser:
                        try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
                        catch { pane.Status.Text = Strings.Get("dual.browser_failed"); }
                        break;
                    default:
                        pane.Status.Text = Strings.Get("dual.blocked");
                        break;
                }
            };
            core.PermissionRequested += (_, e) =>
            {
                var kind = e.PermissionKind switch
                {
                    CoreWebView2PermissionKind.Microphone => "microphone",
                    CoreWebView2PermissionKind.Notifications => "notifications",
                    _ => "other",
                };
                e.State = UrlPolicy.IsAutoGrantedPermission(e.Uri, kind)
                    ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                e.Handled = true;
            };
            core.ServerCertificateErrorDetected += (_, e) =>
            {
                e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                pane.Status.Text = Strings.Get("dual.certificate");
            };
            core.DownloadStarting += (_, e) =>
            {
                if (_closed || generation != pane.Generation || pane.Covered)
                { e.Cancel = true; e.Handled = true; return; }
                _download(pane.ProfileId, e);
                pane.Status.Text = Strings.Get(e.Cancel ? "dual.download_cancelled" : "dual.download_tracking");
            };
            core.ProcessFailed += (_, _) =>
            {
                if (!_closed && generation == pane.Generation)
                { pane.Failed = true; pane.Status.Text = Strings.Get("dual.recover_hint"); }
            };
            WirePaneTools(pane, browser, core, generation);
            SetZoom(pane, ReferenceEquals(pane, _left) ? _session.LeftZoom : _session.RightZoom,
                persist: false);
            core.Navigate(url);
            ApplyPrivacy(pane);
        }
        catch (Exception ex)
        {
            if (!_closed && generation == pane.Generation)
            { pane.Failed = true; pane.Status.Text = Strings.Get("dual.recover_hint"); }
            _log.Error($"Dual pane initialization failed: {ex.GetType().Name}");
        }
    }

    private void UpdateNavigation(Pane pane)
    {
        if (_closed) return;
        pane.Back.IsEnabled = pane.Core?.CanGoBack == true;
        pane.Forward.IsEnabled = pane.Core?.CanGoForward == true;
    }

    private Pane PaneFor(object sender) =>
        sender is Button { Tag: "right" } ? _right : _left;

    private void OnBack(object sender, RoutedEventArgs e)
    {
        var core = PaneFor(sender).Core;
        if (core?.CanGoBack == true) core.GoBack();
    }

    private void OnForward(object sender, RoutedEventArgs e)
    {
        var core = PaneFor(sender).Core;
        if (core?.CanGoForward == true) core.GoForward();
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        var pane = PaneFor(sender);
        if (pane.Failed || pane.Core is null) _ = RecoverPaneAsync(pane);
        else pane.Core.Reload();
    }

    private void OnNewChat(object sender, RoutedEventArgs e) =>
        PaneFor(sender).Core?.Navigate(UrlPolicy.HomeUrl);

    private void RememberChat(Pane pane, string? url)
    {
        if (!_closed && _sessions.RememberChat(_profileId, ReferenceEquals(pane, _left), url))
            SaveSession();
    }

    private void SaveSession()
    {
        try { DualSessionStore.Save(AppPaths.DualSessionPath, _sessions); }
        catch (Exception ex)
        {
            _log.Warn($"Dual session save failed: {ex.GetType().Name}");
            if (!_closed) _left.Status.Text = Strings.Get("dual.save_failed");
        }
    }

    private void ApplyDivider(double fraction)
    {
        _session.LeftFraction = DualSession.CleanFraction(fraction);
        ConfigureLayout();
    }

    private void RememberDivider()
    {
        var left = _session.Vertical ? PaneLayout.RowDefinitions[0].ActualHeight : PaneLayout.ColumnDefinitions[0].ActualWidth;
        var right = _session.Vertical ? PaneLayout.RowDefinitions[2].ActualHeight : PaneLayout.ColumnDefinitions[2].ActualWidth;
        if (_closed || left + right <= 0) return;
        _session.LeftFraction = DualSession.CleanFraction(left / (left + right));
        SaveSession();
    }

    private void OnDividerChanged(object sender, DragCompletedEventArgs e) => RememberDivider();

    private void OnDividerKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Home)
        {
            ResetDivider();
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            RememberDivider();
    }

    private void OnResetDivider(object sender, MouseButtonEventArgs e)
    {
        ResetDivider();
        e.Handled = true;
    }

    private void ResetDivider()
    {
        _session.LeftFraction = 0.5;
        ApplyDivider(0.5);
        SaveSession();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        var pane = PaneFor(sender);
        if (pane.Browser is { } browser)
            SetZoom(pane, ZoomMath.StepUp(browser.ZoomFactor, _settings.ExtendedZoomRange));
    }

    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        var pane = PaneFor(sender);
        if (pane.Browser is { } browser)
            SetZoom(pane, ZoomMath.StepDown(browser.ZoomFactor, _settings.ExtendedZoomRange));
    }

    private void OnZoomReset(object sender, RoutedEventArgs e) => SetZoom(PaneFor(sender), 1);

    private void SetZoom(Pane pane, double zoom, bool persist = true)
    {
        if (pane.Browser is not { } browser || pane.Core is null) return;
        zoom = ZoomMath.Clamp(zoom, _settings.ExtendedZoomRange);
        browser.ZoomFactor = zoom;
        pane.ZoomReset.Content = ZoomMath.FormatPercent(zoom);
        if (ReferenceEquals(pane, _left)) _session.LeftZoom = zoom;
        else _session.RightZoom = zoom;
        if (persist) SaveSession();
    }

    private void OnOpenMain(object sender, RoutedEventArgs e)
    {
        var pane = PaneFor(sender);
        if (pane.Core is null) return;
        if (pane.ProfileId != _settings.ActiveProfileId)
        { Announce(pane, "dual.main_profile"); return; }
        if (_openInMain(QuickAsk.ContinueTarget(pane.Core.Source)) is { } refusal)
            pane.Status.Text = Strings.Get(QuickAsk.ContinueRefusalStatusKey(refusal));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleWorkbenchKey(e)) return;
        if (e.Key != Key.F6) return;
        e.Handled = true;
        FocusOtherPane(_right.Host.IsKeyboardFocusWithin ? _right : _left);
    }

    private void FocusOtherPane(Pane current)
    {
        if (!_closed) FocusPane(ReferenceEquals(current, _left) ? _right : _left);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _ready.TrySetResult(false);
        CloseWorkbench();
        foreach (var pane in new[] { _left, _right })
        {
            var browser = pane.Browser;
            pane.Browser = null;
            if (browser is null) continue;
            (pane.DetachedHost ?? pane.Host).Children.Remove(browser);
            try { browser.Dispose(); } catch { /* already disposed */ }
        }
    }
}
