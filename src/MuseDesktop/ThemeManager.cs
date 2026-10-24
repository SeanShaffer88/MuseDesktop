using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// Applies the shell theme (v0.9.0, S5 #43/#44). The palettes and their
/// contrast contracts live in Core (<see cref="ShellPalettes"/> /
/// <see cref="AccentPalette"/>); this class only moves colors into WPF.
///
/// Mechanism: every shell window paints from the shared brush OBJECTS
/// in App.xaml via StaticResource, so mutating a brush's Color in place
/// repaints the whole shell — no XAML rewrite per window, and windows
/// opened later pick the current colors up at parse time. Only the
/// shell is themed; the muse.ai page is never restyled.
/// </summary>
public static class ThemeManager
{
    public static bool IsLight { get; private set; }
    public static ShellPalette CurrentPalette { get; private set; } = ShellPalettes.Dark;

    /// <summary>Raised after a (re-)application so window owners can re-sync title bars and the web view's background.</summary>
    public static event EventHandler? Changed;

    public static void Apply(AppSettings settings)
    {
        IsLight = ThemeResolution.IsLight(settings.Theme, SystemPrefersLight());
        CurrentPalette = ShellPalettes.For(IsLight);
        var (darkAccent, lightAccent) = AccentPalette.For(settings.AccentColor);
        var p = CurrentPalette;
        SetBrushColor("BgBrush", p.Bg);
        SetBrushColor("SurfaceBrush", p.Surface);
        SetBrushColor("RaisedBrush", p.Raised);
        SetBrushColor("HairlineBrush", p.Hairline);
        SetBrushColor("TextBrush", p.Text);
        SetBrushColor("MutedBrush", p.Muted);
        SetBrushColor("FaintBrush", p.Faint);
        // The TEXT/decoration accent follows the theme (each variant is
        // contrast-pinned against its own surfaces); the FILL accent —
        // buttons carrying white text — is the deep variant in BOTH
        // themes, the only way white-on-accent stays at AA everywhere.
        SetBrushColor("AccentBrush", IsLight ? lightAccent : darkAccent);
        SetBrushColor("AccentFillBrush", lightAccent);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void SetBrushColor(string key, string hex)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
            return;
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        if (app.Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
            brush.Color = color;
        else
            app.Resources[key] = new SolidColorBrush(color);
    }

    /// <summary>
    /// Best-effort read of the Windows app-theme preference
    /// (AppsUseLightTheme). Any failure means "not light" — the family
    /// dark identity is the safe default, never a guess at light.
    /// </summary>
    public static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The web view's pre-load background in the current palette — a black flash on a light shell would read as a glitch.</summary>
    public static System.Drawing.Color BrowserBackground =>
        System.Drawing.ColorTranslator.FromHtml(CurrentPalette.Bg);

    /// <summary>Sync one window's title bar with the shell theme via the immersive-dark-mode attribute.</summary>
    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var value = IsLight ? 0 : 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 10 2004+ / Windows 11).
            _ = DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
        }
        catch { /* cosmetic only */ }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
