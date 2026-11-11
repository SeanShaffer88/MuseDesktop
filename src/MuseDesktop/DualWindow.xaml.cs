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
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MuseDesktop;

/// <summary>
/// Two live, independent pages on the active profile. Each page's own
/// composer sends only to its own chat: no shared prompt, script, bridge,
/// or conversation extraction. The owner closes this window on app lock.
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
        public required string Name { get; init; }
        public WebView2? Browser;
        public CoreWebView2? Core => Browser?.CoreWebView2;
    }

    private readonly AppSettings _settings;
    private readonly DiagnosticLog _log;
    private readonly string _profileId;
    private readonly (string Left, string Right) _urls;
    private readonly Func<string?, QuickAskContinueRefusal?> _openInMain;
    private readonly Pane _left;
    private readonly Pane _right;
    private readonly DualSessions _sessions;
    private readonly DualSession _session;
    private bool _closed;

    public DualWindow(AppSettings settings, DiagnosticLog log, string? currentUrl,
        Func<string?, QuickAskContinueRefusal?> openInMain)
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
        InitializeComponent();
        _left = new Pane { Host = LeftHost, Status = LeftStatus, Back = LeftBack,
            Forward = LeftForward, ZoomReset = LeftZoomReset, Name = Strings.Get("dual.left") };
        _right = new Pane { Host = RightHost, Status = RightStatus, Back = RightBack,
            Forward = RightForward, ZoomReset = RightZoomReset, Name = Strings.Get("dual.right") };
        ApplyDivider(_session.LeftFraction);
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ProfileDir(_profileId));
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.ProfileDir(_profileId));
            if (_closed) return;
            await Task.WhenAll(StartPaneAsync(_left, env, _urls.Left),
                StartPaneAsync(_right, env, _urls.Right));
            if (!_closed) _left.Browser?.Focus();
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _left.Status.Text = _right.Status.Text = Strings.Get("dual.failed");
            // Exception messages may carry an address or a local path.
            _log.Error($"Dual environment initialization failed: {ex.GetType().Name}");
        }
    }

    private async Task StartPaneAsync(Pane pane, CoreWebView2Environment env, string url)
    {
        var browser = new WebView2 { DefaultBackgroundColor = ThemeManager.BrowserBackground };
        AutomationProperties.SetName(browser, pane.Name);
        pane.Host.Children.Add(browser);
        pane.Browser = browser;
        pane.Status.Text = Strings.Get("dual.loading");
        try
        {
            await browser.EnsureCoreWebView2Async(env);
            if (_closed) return;
            var core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = _settings.GeneralAutofillEnabled;
            core.NavigationStarting += (_, e) =>
            {
                if (UrlPolicy.ClassifyNavigation(e.Uri) == LinkDisposition.Block)
                {
                    e.Cancel = true;
                    pane.Status.Text = Strings.Get("dual.blocked");
                    return;
                }
                pane.Status.Text = Strings.Get("dual.loading");
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (_closed) return;
                pane.Status.Text = Strings.Get(e.IsSuccess ? "dual.ready" : "dual.failed");
                if (e.IsSuccess) RememberChat(pane, core.Source);
                UpdateNavigation(pane);
            };
            // Muse creates chat routes with history.pushState; those
            // same-document changes need remembering as well as full loads.
            core.SourceChanged += (_, e) =>
            {
                if (!_closed && !e.IsNewDocument) RememberChat(pane, core.Source);
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
                // Only main-window tabs participate in DownloadTracker.
                // Refuse here rather than bypassing its collision/history rules.
                e.Cancel = true;
                e.Handled = true;
                pane.Status.Text = Strings.Get("dual.download_main");
            };
            core.ProcessFailed += (_, _) =>
            {
                if (!_closed) pane.Status.Text = Strings.Get("dual.failed");
            };
            SetZoom(pane, ReferenceEquals(pane, _left) ? _session.LeftZoom : _session.RightZoom,
                persist: false);
            core.Navigate(url);
        }
        catch (Exception ex)
        {
            if (!_closed) pane.Status.Text = Strings.Get("dual.failed");
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
        if (pane.Core is { } core) core.Reload();
        else pane.Status.Text = Strings.Get("dual.failed");
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
        fraction = DualSession.CleanFraction(fraction);
        PaneLayout.ColumnDefinitions[0].Width = new GridLength(fraction, GridUnitType.Star);
        PaneLayout.ColumnDefinitions[2].Width = new GridLength(1 - fraction, GridUnitType.Star);
    }

    private void RememberDivider()
    {
        var left = PaneLayout.ColumnDefinitions[0].ActualWidth;
        var right = PaneLayout.ColumnDefinitions[2].ActualWidth;
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
        else if (e.Key is Key.Left or Key.Right)
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
        if (_openInMain(QuickAsk.ContinueTarget(pane.Core.Source)) is { } refusal)
            pane.Status.Text = Strings.Get(QuickAsk.ContinueRefusalStatusKey(refusal));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F6) return;
        e.Handled = true;
        FocusOtherPane(_right.Host.IsKeyboardFocusWithin ? _right : _left);
    }

    private void FocusOtherPane(Pane current)
    {
        if (!_closed) (ReferenceEquals(current, _left) ? _right : _left).Browser?.Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        foreach (var pane in new[] { _left, _right })
        {
            var browser = pane.Browser;
            pane.Browser = null;
            if (browser is null) continue;
            pane.Host.Children.Remove(browser);
            try { browser.Dispose(); } catch { /* already disposed */ }
        }
    }
}
