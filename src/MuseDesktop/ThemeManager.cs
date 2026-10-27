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

    /// <summary>Whether Windows High Contrast is currently on (S6 #51) — re-read at every Apply.</summary>
    public static bool HighContrastActive { get; private set; }

    /// <summary>The effective backdrop material (R6 #151) — Core's decision (BackdropPolicy over the probed capabilities), restated here so window owners can adapt their chrome after every Apply.</summary>
    public static BackdropMaterial CurrentBackdrop { get; private set; } = BackdropMaterial.None;

    /// <summary>The active profile's title-bar tint hex (R6 #152), or null for the default caption. Resolved from the settings' active profile at every Apply.</summary>
    public static string? CurrentTitleBarTintHex { get; private set; }

    public static void Apply(AppSettings settings)
    {
        // R6 #159: the schedule input speaks only for FollowSchedule;
        // Core's ThemeResolution keeps the older modes untouched.
        IsLight = ThemeResolution.IsLight(settings.Theme, SystemPrefersLight(),
            ThemeScheduleMath.IsLightNow(settings, DateTimeOffset.Now));
        CurrentPalette = ShellPalettes.For(IsLight);
        HighContrastActive = SystemParameters.HighContrast;
        // R6 #158: a valid custom accent replaces the preset pair —
        // its derived variants carry the same contrast guarantee.
        var (darkAccent, lightAccent) = AccentResolver.Resolve(settings.AccentColor, settings.CustomAccent);
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
        SetBrushColor("AccentTextBrush", "#FFFFFF");
        if (HighContrastActive)
            ApplyHighContrastOverrides();
        // R6 #151/#152: the backdrop and tint decisions are Core's;
        // the shell only stores them for the per-window DWM calls.
        var activeProfile = settings.Profiles.FirstOrDefault(p => p.Id == settings.ActiveProfileId);
        CurrentTitleBarTintHex = TitleBarTint.Resolve(
            settings.TitleBarProfileTint, activeProfile?.DisplayColor, HighContrastActive);
        CurrentBackdrop = BackdropPolicy.Resolve(
            settings.Backdrop, ProbeBackdropCapabilities(), HighContrastActive);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// What this PC offers a backdrop (R6 #151): the system-backdrop
    /// attribute exists on Windows 11 (build 22000+), and the user's
    /// transparency-effects switch (Personalize → EnableTransparency)
    /// gates it — off, missing, or unreadable all decide through
    /// Core's policy (an unreadable switch reads as ON, the Windows
    /// default; Core still vetoes on High Contrast).
    /// </summary>
    private static BackdropCapabilities ProbeBackdropCapabilities()
    {
        var supported = Environment.OSVersion.Version.Build >= 22000;
        var transparency = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("EnableTransparency") is int value)
                transparency = value != 0;
        }
        catch { /* the Windows default is transparency on */ }
        return new BackdropCapabilities(supported, transparency);
    }

    /// <summary>
    /// High Contrast (S6 #51): when Windows HC is on, the user's system
    /// colors outrank the shell palettes entirely — every token is
    /// re-pointed at the SystemColors role Windows guarantees readable
    /// (Window/WindowText for surfaces and text, Highlight +
    /// HighlightText for the accent pair). The palette objects still
    /// get mutated to the same roles, so code reading
    /// <see cref="CurrentPalette"/> (the second window, the browser
    /// background) stays consistent. This is re-run on every Apply,
    /// and App re-applies on UserPreferenceChanged, so toggling HC in
    /// Windows repaints the shell without a restart.
    /// </summary>
    private static void ApplyHighContrastOverrides()
    {
        // Fully qualified: System.Drawing.SystemColors is also in scope
        // in this file (the browser-background fallback uses it).
        SetBrush("BgBrush", System.Windows.SystemColors.WindowColor);
        SetBrush("SurfaceBrush", System.Windows.SystemColors.WindowColor);
        SetBrush("RaisedBrush", System.Windows.SystemColors.ControlColor);
        SetBrush("HairlineBrush", System.Windows.SystemColors.WindowTextColor);
        SetBrush("TextBrush", System.Windows.SystemColors.WindowTextColor);
        SetBrush("MutedBrush", System.Windows.SystemColors.WindowTextColor);
        SetBrush("FaintBrush", System.Windows.SystemColors.GrayTextColor);
        SetBrush("AccentBrush", System.Windows.SystemColors.HighlightColor);
        SetBrush("AccentFillBrush", System.Windows.SystemColors.HighlightColor);
        SetBrush("AccentTextBrush", System.Windows.SystemColors.HighlightTextColor);
    }

    private static void SetBrushColor(string key, string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        SetBrush(key, color);
    }

    private static void SetBrush(string key, System.Windows.Media.Color color)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
            return;
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

    /// <summary>The web view's pre-load background (R6 #157): Core's WebViewBackgroundPolicy names the palette hex — a white or black flash in the wrong theme would read as a glitch — and under High Contrast it defers to the system window color (the null case).</summary>
    public static System.Drawing.Color BrowserBackground =>
        WebViewBackgroundPolicy.ResolveHex(CurrentPalette, HighContrastActive) is { } hex
            ? System.Drawing.ColorTranslator.FromHtml(hex)
            : System.Drawing.SystemColors.Window;

    /// <summary>Sync one window's title bar with the shell theme via the immersive-dark-mode attribute, plus the profile caption tint (R6 #152).</summary>
    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var value = IsLight ? 0 : 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 10 2004+ / Windows 11).
            _ = DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
            // DWMWA_CAPTION_COLOR = 35 (Windows 11): the resolved
            // profile tint as a COLORREF (0x00BBGGRR), or COLOR_DEFAULT
            // (0xFFFFFFFF) to hand the caption back to Windows. Older
            // Windows ignores the unknown attribute — the tint simply
            // doesn't show there, like the backdrop.
            var caption = CurrentTitleBarTintHex is { } tint
                ? unchecked((int)ToColorRef(tint))
                : unchecked((int)0xFFFFFFFF);
            _ = DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        }
        catch { /* cosmetic only */ }
    }

    /// <summary>A "#RRGGBB" hex as a DWM COLORREF (red in the low byte).</summary>
    private static uint ToColorRef(string hex)
    {
        var (r, g, b) = HexColor.ParseRgb(hex);
        return (uint)(r | (g << 8) | (b << 16));
    }

    /// <summary>
    /// Apply (or clear) the window's backdrop material (R6 #151):
    /// DWMWA_SYSTEMBACKDROP_TYPE = 38 with the frame extended into
    /// the client area so the material has somewhere to show. The
    /// material itself is Core's <see cref="CurrentBackdrop"/> —
    /// None here means the attribute is explicitly cleared, so a
    /// Settings change takes a material OFF a live window too.
    /// </summary>
    public static void ApplyBackdrop(Window window)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            // DWMSBT: 1 = none, 2 = main window (Mica), 3 = transient (Acrylic).
            var backdropType = CurrentBackdrop switch
            {
                BackdropMaterial.Mica => 2,
                BackdropMaterial.Acrylic => 3,
                _ => 1,
            };
            if (CurrentBackdrop != BackdropMaterial.None)
            {
                var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            }
            _ = DwmSetWindowAttribute(hwnd, 38, ref backdropType, sizeof(int));
        }
        catch { /* cosmetic only — a plain window is the fallback */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
