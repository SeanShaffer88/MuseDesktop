using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
    private bool _closed;

    public DualWindow(AppSettings settings, DiagnosticLog log, string? currentUrl,
        Func<string?, QuickAskContinueRefusal?> openInMain)
    {
        _settings = settings;
        _log = log;
        _profileId = settings.ActiveProfileId;
        _urls = DualMode.InitialUrls(currentUrl);
        _openInMain = openInMain;
        InitializeComponent();
        _left = new Pane { Host = LeftHost, Status = LeftStatus, Back = LeftBack,
            Forward = LeftForward, Name = Strings.Get("dual.left") };
        _right = new Pane { Host = RightHost, Status = RightStatus, Back = RightBack,
            Forward = RightForward, Name = Strings.Get("dual.right") };
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
                UpdateNavigation(pane);
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
            browser.ZoomFactor = Profiles.EffectiveZoom(
                _settings.Profiles.FirstOrDefault(p => p.Id == _profileId),
                _settings.ZoomFactor, _settings.ExtendedZoomRange);
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
