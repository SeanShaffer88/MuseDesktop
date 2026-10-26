using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// A lightweight second window (v0.5.0, S1 #7): a WebView2 created from the
/// SAME CoreWebView2Environment as the main window, so it shares the active
/// profile's sign-in without a second process, a second single-instance
/// dance, or a second WebView2 user-data folder lock. It deliberately has
/// no tray, settings, download or update duties — those stay with the main
/// window — and its new-window requests follow the same UrlPolicy routing.
/// Closing it never exits the app.
/// </summary>
public partial class SecondaryWindow : Window
{
    private readonly CoreWebView2Environment _env;
    private readonly DiagnosticLog _log;
    private readonly string _initialUrl;

    /// <summary>
    /// R1 #101: Ctrl+K in a secondary window asks for the command
    /// palette. The palette itself is a main-window surface (it
    /// searches app state this window deliberately doesn't own), so
    /// the main window sets this to a handler that opens it owned by
    /// the asking window.
    /// </summary>
    public Action? PaletteRequested { get; set; }

    /// <summary>
    /// The page this window currently shows (R2 #111), read live for
    /// workspace/session capture. Null before the web view exists or
    /// when nothing has loaded; the caller turns it into an app route
    /// (Core's <see cref="AppRoute.FromUrl"/>) and never stores the
    /// raw value.
    /// </summary>
    public string? CurrentUrl
    {
        get
        {
            try { return Browser.Source?.ToString(); }
            catch { return null; }
        }
    }

    public SecondaryWindow(CoreWebView2Environment env, DiagnosticLog log, string initialUrl)
    {
        _env = env;
        _log = log;
        _initialUrl = initialUrl;
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
            {
                PaletteRequested?.Invoke();
                e.Handled = true;
            }
        };
        // S5 #43: the second window follows the shell palette too (its
        // XAML colors are the dark defaults; light theme overrides here).
        var palette = ThemeManager.CurrentPalette;
        Background = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(palette.Bg));
        Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(palette.Text));
        Browser.DefaultBackgroundColor = ThemeManager.BrowserBackground;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async(_env);
            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.NewWindowRequested += OnNewWindowRequested;
            core.DocumentTitleChanged += (_, _) =>
            {
                var t = core.DocumentTitle;
                Title = string.IsNullOrWhiteSpace(t) ? "Muse — second window" : $"{t} — MuseDesktop";
            };
            core.Navigate(_initialUrl);
            _log.Info($"Secondary window opened: {UrlPolicy.RedactForLog(_initialUrl)}");
        }
        catch (Exception ex)
        {
            _log.Error($"Secondary window WebView2 init failed: {ex.Message}");
            Close();
        }
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
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
                catch { /* no default browser etc. */ }
                break;
            default:
                _log.Warn($"Blocked new-window request (secondary): {UrlPolicy.RedactForLog(e.Uri)}");
                break;
        }
    }
}
