using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MuseDesktop.Core;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MuseDesktop;

/// <summary>
/// Quick ask: a small Muse window docked at the bottom-right
/// corner of the primary monitor's work area, for one quick
/// question (design Concept B — the corner card; SEAN's pick
/// 2026-11-02). One WebView2 on the ACTIVE profile's environment —
/// built exactly the way the main window and the compare window
/// build theirs (<see cref="CoreWebView2Environment.CreateAsync"/>
/// over <see cref="AppPaths.ProfileDir"/>; the same folder resolves
/// to the same environment in-process, so the popup shares the
/// main window's sign-in) — opened on a FRESH conversation at
/// every summon: it is a one-off tool, and reopening never resumes
/// the previous quick conversation (see DECISIONS).
///
/// The page is PLAIN, like the compare panes: the bridge is never
/// installed in it, so there are no drafts, chips, or page probes,
/// and nothing reads the conversation back. The page's own
/// composer is the input — there is deliberately no shell composer
/// and no send of any kind here. The one shell action is the
/// footer button: "Continue in main window" hands the page's
/// current address (Core's <see cref="QuickAsk.ContinueTarget"/>
/// decides what may be handed over) to the main window through
/// the delegate it was constructed with, and the popup closes
/// when the main window accepts it. Placement is Core's dock
/// maths over the primary work area, re-decided at every summon
/// and never persisted.
/// </summary>
public partial class QuickAskWindow : Window
{
    private readonly DiagnosticLog _log;
    private readonly string _profileId;
    private readonly Func<string?, bool> _continueInMainWindow;
    private Task<CoreWebView2Environment>? _environment;
    private WebView2? _browser;
    private CoreWebView2? _core;
    private string? _currentUrl;
    private bool _closed;

    public QuickAskWindow(AppSettings settings, DiagnosticLog log, Func<string?, bool> continueInMainWindow)
    {
        _log = log;
        _continueInMainWindow = continueInMainWindow;
        var profiles = Profiles.VisibleProfiles(settings);
        var profile = profiles.FirstOrDefault(p => p.Id == settings.ActiveProfileId)
            ?? profiles.FirstOrDefault()
            ?? new AccountProfile();
        _profileId = profile.Id;
        InitializeComponent();
        // The title names the profile the popup is signed in as —
        // the card's one piece of shell chrome above the page.
        Title = Strings.Format("quickask.title_with_profile", Profiles.DisplayLabel(profile));
        // Dock fresh, bottom-right of the primary monitor's work
        // area (DIPs from MonitorRects are per-monitor converted,
        // so the fixed DIP card size arrives DPI-scaled).
        var workArea = MonitorRects.AllWorkAreasInDip().FirstOrDefault();
        var placement = QuickAsk.DockPlacement(workArea);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Math.Max(placement.Width, MinWidth);
        Height = Math.Max(placement.Height, MinHeight);
        // The window minimums can outgrow the shrunk placement on
        // a small work area (a small screen at high scaling), so
        // dock by the EFFECTIVE size: the card's bottom-right —
        // the footer rides on it — stays on the work area's
        // corner, and the top/left yield instead of the bottom.
        Left = workArea.Right - QuickAsk.DockMarginDip - Width;
        Top = workArea.Bottom - QuickAsk.DockMarginDip - Height;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc closes — the footer's hint promises it. Keys the page
        // doesn't consume reach the window through the same WPF
        // routing the main window's Esc handling relies on; when
        // the shell itself has focus this fires directly.
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var browser = new WebView2 { DefaultBackgroundColor = ThemeManager.BrowserBackground };
        AutomationProperties.SetName(browser, Strings.Get("muse_web_content"));
        WebHost.Children.Add(browser);
        _browser = browser;
        try
        {
            var env = await GetEnvironmentAsync();
            if (_closed)
            {
                try { browser.Dispose(); } catch { /* already dead */ }
                return;
            }
            await browser.EnsureCoreWebView2Async(env);
            if (_closed)
                return;
            var core = browser.CoreWebView2;
            _core = core;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.NewWindowRequested += OnNewWindowRequested;
            core.SourceChanged += (_, _) => _currentUrl = core.Source;
            _currentUrl = core.Source;
            // A fresh conversation, at every summon — never the
            // profile's last page, never the previous quick ask.
            core.Navigate(UrlPolicy.HomeUrl);
            _log.Info($"Quick ask opened on profile {_profileId}: {UrlPolicy.RedactForLog(UrlPolicy.HomeUrl)}");
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                StatusText.Text = Strings.Get("quickask.failed_to_start");
                _log.Error($"Quick ask failed to start: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        if (_browser is not null)
        {
            WebHost.Children.Remove(_browser);
            try { _browser.Dispose(); } catch { /* already dead */ }
            _browser = null;
            _core = null;
        }
        // The environment is NOT disposed: it is shared
        // process-wide (the active profile's is the main window's
        // own), and the main window never disposes its either.
    }

    /// <summary>
    /// The environment for the popup's profile, created the way
    /// the main window creates its own. A faulted creation is not
    /// cached — the next summon starts clean, the main window's
    /// discipline.
    /// </summary>
    private Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_environment is null)
        {
            Directory.CreateDirectory(AppPaths.ProfileDir(_profileId));
            var task = CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppPaths.ProfileDir(_profileId));
            _environment = task;
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted && ReferenceEquals(_environment, task))
                    _environment = null;
            }, TaskScheduler.Default);
        }
        return _environment;
    }

    /// <summary>New-window requests follow the same UrlPolicy routing the compare panes use: in-app links stay in the popup, external ones go to the browser, the rest are refused.</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        switch (UrlPolicy.ClassifyNewWindow(e.Uri))
        {
            case LinkDisposition.InApp:
                _core?.Navigate(e.Uri);
                break;
            case LinkDisposition.ExternalBrowser:
                try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
                catch { /* no default browser etc. */ }
                break;
            default:
                _log.Warn($"Blocked new-window request (quick ask): {UrlPolicy.RedactForLog(e.Uri)}");
                break;
        }
    }

    /// <summary>
    /// The hand-off: the main window takes the conversation from
    /// here. Core decides the address (the page's current one when
    /// it is in-app, Home otherwise); the delegate reports whether
    /// the main window accepted it (it refuses while locked, for
    /// instance) — and only then does the popup close.
    /// </summary>
    private void OnContinueInMainWindow(object sender, RoutedEventArgs e)
    {
        var target = QuickAsk.ContinueTarget(_currentUrl);
        _log.Info($"Quick ask handed to the main window: {UrlPolicy.RedactForLog(target)}");
        if (_continueInMainWindow(target))
            Close();
    }
}
