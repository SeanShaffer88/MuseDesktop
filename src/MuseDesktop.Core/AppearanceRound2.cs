namespace MuseDesktop.Core;

// ───────────────────── appearance round 2 (R6 #151–#160) ─────────────────────
//
// Every DECISION in this slice lives here, pure and testable: which
// backdrop material a window gets, what hex tints its title bar, how
// a custom accent is repaired until it passes the SAME measured-
// contrast guard as the presets, when the schedule says "light",
// what opacity a window may actually render at, how the tray icon
// is composed, and which fields make up a theme bundle. The shell
// owns only the Windows calls themselves (DWM attributes, WPF
// brushes, GDI pixels) — none of which can run in a test, and none
// of which get to overrule these answers.

/// <summary>A #RRGGBB hex color: the one normalizer every appearance feature shares.</summary>
public static class HexColor
{
    /// <summary>Accept "#RRGGBB" or "RRGGBB" (any case, padded) and return the canonical "#RRGGBB" uppercase form; anything else is not a color.</summary>
    public static bool TryNormalize(string? text, out string hex)
    {
        hex = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var h = text.Trim().TrimStart('#');
        if (h.Length != 6)
            return false;
        foreach (var c in h)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        hex = "#" + h.ToUpperInvariant();
        return true;
    }

    /// <summary>The (R, G, B) bytes of a normalized hex color. Callers pass only values <see cref="TryNormalize"/> accepted.</summary>
    public static (int R, int G, int B) ParseRgb(string hex)
    {
        var h = hex.Trim().TrimStart('#');
        return (Convert.ToInt32(h[..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..6], 16));
    }

    public static string FromRgb(int r, int g, int b) =>
        $"#{Math.Clamp(r, 0, 255):X2}{Math.Clamp(g, 0, 255):X2}{Math.Clamp(b, 0, 255):X2}";

    /// <summary>The same color with its luminance kept and its hue removed — a gray of equal brightness (byte-weighted, the cheap perceptual mix).</summary>
    public static string ToGrayscaleHex(string hex)
    {
        var (r, g, b) = ParseRgb(hex);
        var gray = (int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);
        return FromRgb(gray, gray, gray);
    }
}

// ───────────────────────── backdrop materials (#151) ─────────────────────────

/// <summary>What the user asked for (R6 #151). Auto = the Windows 11 default for a main window (Mica) when the PC can do it.</summary>
public enum BackdropPreference
{
    Auto,
    None,
    Mica,
    Acrylic,
}

/// <summary>The effective material after capability and accessibility checks — the only thing the shell may apply.</summary>
public enum BackdropMaterial
{
    None,
    Mica,
    Acrylic,
}

/// <summary>What the PC offers, probed by the shell: the Windows 11 system-backdrop attribute exists, and the user's Windows transparency-effects switch is on.</summary>
public sealed record BackdropCapabilities(bool SystemBackdropSupported, bool TransparencyEffectsEnabled);

public static class BackdropPolicy
{
    /// <summary>
    /// Decide the material. The order is deliberate: accessibility
    /// outranks taste — High Contrast or Windows transparency effects
    /// being OFF always means no material, whatever the preference
    /// says; a PC without the Windows 11 backdrop attribute means no
    /// material either (graceful fallback, never a fake one); only
    /// then does the preference speak. Auto resolves to Mica, the
    /// main-window material.
    /// </summary>
    public static BackdropMaterial Resolve(
        BackdropPreference preference, BackdropCapabilities capabilities, bool highContrast)
    {
        if (highContrast)
            return BackdropMaterial.None;
        if (!capabilities.TransparencyEffectsEnabled)
            return BackdropMaterial.None;
        if (!capabilities.SystemBackdropSupported)
            return BackdropMaterial.None;
        return preference switch
        {
            BackdropPreference.Mica => BackdropMaterial.Mica,
            BackdropPreference.Acrylic => BackdropMaterial.Acrylic,
            BackdropPreference.Auto => BackdropMaterial.Mica,
            _ => BackdropMaterial.None,
        };
    }
}

// ───────────────────────── per-profile title bar tint (#152) ─────────────────────────

public static class TitleBarTint
{
    /// <summary>
    /// The caption color for a window, from the ACTIVE profile's
    /// color — or null for "leave the Windows/theme default alone".
    /// Null when the feature is off, when High Contrast is on (the
    /// user's system colors outrank any tint, exactly like the shell
    /// palettes), or when the profile has no color.
    /// </summary>
    public static string? Resolve(bool enabled, string? profileColorKey, bool highContrast)
    {
        if (!enabled || highContrast)
            return null;
        return ProfileColors.HexFor(profileColorKey);
    }
}

// ───────────────────────── compact density (#153) ─────────────────────────

/// <summary>Shell spacing density (R6 #153): a companion to shell text size (S5 #46) — text scales the words, density scales the air around them.</summary>
public enum ShellDensity
{
    Comfortable,
    Compact,
}

/// <summary>The chrome measurements a density implies, in WPF device-independent pixels. Comfortable values are the long-standing XAML numbers.</summary>
public sealed record DensityMetrics(double ToolbarHeight, double StatusBarHeight, double ToolbarInset, double StatusInset)
{
    public static DensityMetrics For(ShellDensity density) => density switch
    {
        ShellDensity.Compact => new DensityMetrics(32, 20, 4, 6),
        _ => new DensityMetrics(40, 24, 6, 8),
    };
}

// ───────────────────────── custom accent (#158) ─────────────────────────

/// <summary>
/// A user-picked accent color (R6 #158), held to the presets' own
/// contract (see <see cref="AccentPalette"/>): TWO variants are
/// derived from the one picked color — a bright one that reads as
/// text on the dark shell, and a deep one that reads as text on the
/// light shell AND carries white text as a button fill — each
/// repaired by blending toward white/black until it measures
/// ≥ 4.5:1 against every surface it can sit on. The picked color
/// itself is stored untouched; only the derived variants move.
/// </summary>
public static class CustomAccent
{
    /// <summary>The contrast floor both variants must meet — the same 4.5:1 the preset pairs are pinned to.</summary>
    public const double RequiredRatio = 4.5;

    private const double BlendStep = 0.04;

    /// <summary>Normalize a picked color to canonical "#RRGGBB", or null when the text names no color (the caller then falls back to the preset).</summary>
    public static string? TryNormalize(string? text) =>
        HexColor.TryNormalize(text, out var hex) ? hex : null;

    /// <summary>The (dark-shell, light-shell) variant pair for a picked color, each guaranteed to meet <see cref="RequiredRatio"/> on its surfaces.</summary>
    public static (string DarkShellHex, string LightShellHex) Variants(string hex)
    {
        var dark = BlendToward(hex, towardWhite: true, candidate =>
            MinRatio(candidate, ShellPalettes.Dark) >= RequiredRatio);
        var light = BlendToward(hex, towardWhite: false, candidate =>
            MinRatio(candidate, ShellPalettes.Light) >= RequiredRatio
            && ColorContrast.Ratio("#FFFFFF", candidate) >= RequiredRatio);
        return (dark, light);
    }

    private static double MinRatio(string candidate, ShellPalette palette) =>
        Math.Min(ColorContrast.Ratio(candidate, palette.Bg),
            Math.Min(ColorContrast.Ratio(candidate, palette.Surface),
                     ColorContrast.Ratio(candidate, palette.Raised)));

    /// <summary>Move the color toward white or black in fixed steps until it passes — or the pole is reached (white/black measure 21:1/1:21 against everything, so the loop always ends with a pass).</summary>
    private static string BlendToward(string hex, bool towardWhite, Func<string, bool> passes)
    {
        var (r, g, b) = HexColor.ParseRgb(hex);
        var candidate = HexColor.FromRgb(r, g, b);
        for (var step = 0; step <= 25 && !passes(candidate); step++)
        {
            double t = BlendStep * (step + 1);
            int Move(int channel) => towardWhite
                ? (int)Math.Round(channel + (255 - channel) * t)
                : (int)Math.Round(channel * (1 - t));
            candidate = HexColor.FromRgb(Move(r), Move(g), Move(b));
        }
        return candidate;
    }
}

/// <summary>Which accent pair the shell paints with: a valid custom color wins over the preset key (R6 #158); an invalid one silently means the preset, so a hand-edited settings file can never produce an unreadable look.</summary>
public static class AccentResolver
{
    public static (string DarkShellHex, string LightShellHex) Resolve(string? presetKey, string? customHex) =>
        CustomAccent.TryNormalize(customHex) is { } custom
            ? CustomAccent.Variants(custom)
            : AccentPalette.For(presetKey);
}

// ───────────────────────── theme scheduling (#159) ─────────────────────────

/// <summary>
/// The light/dark-by-time-of-day schedule (R6 #159): LIGHT runs from
/// the light-start minute to the dark-start minute, wrapping
/// midnight exactly like quiet hours (<see cref="QuietHours.IsActive"/>
/// is the window math — an equal pair means "never light").
/// </summary>
public static class ThemeScheduleMath
{
    public const int DefaultLightStartMinutes = 7 * 60;
    public const int DefaultDarkStartMinutes = 19 * 60;

    public static bool IsLightAt(int lightStartMinutes, int darkStartMinutes, int nowMinutes) =>
        QuietHours.IsActive(lightStartMinutes, darkStartMinutes, nowMinutes);

    /// <summary>Whether the schedule calls for light at <paramref name="now"/> (local time-of-day of the moment given).</summary>
    public static bool IsLightNow(AppSettings settings, DateTimeOffset now) =>
        IsLightAt(settings.ThemeLightStartMinutes, settings.ThemeDarkStartMinutes, QuietHours.MinutesOfDay(now));

    /// <summary>"7:00" / "07:00" / "19:30" / "7:00 PM" → minutes after midnight. Anything else is not a time.</summary>
    public static bool TryParseTime(string? text, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim();
        var meridiem = 0; // 0 none, 1 AM, 2 PM
        if (t.EndsWith("AM", StringComparison.OrdinalIgnoreCase)) { meridiem = 1; t = t[..^2].Trim(); }
        else if (t.EndsWith("PM", StringComparison.OrdinalIgnoreCase)) { meridiem = 2; t = t[..^2].Trim(); }
        var parts = t.Split(':');
        if (parts.Length != 2)
            return false;
        if (!int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute))
            return false;
        if (minute is < 0 or > 59)
            return false;
        if (meridiem != 0)
        {
            if (hour is < 1 or > 12)
                return false;
            hour = meridiem == 2 ? (hour % 12) + 12 : hour % 12;
        }
        else if (hour is < 0 or > 23)
        {
            return false;
        }
        minutes = hour * 60 + minute;
        return QuietHours.IsValidMinutes(minutes);
    }

    /// <summary>Minutes after midnight as "07:00" (24-hour, the Settings display form).</summary>
    public static string FormatMinutes(int minutes) =>
        QuietHours.IsValidMinutes(minutes) ? $"{minutes / 60:00}:{minutes % 60:00}" : "00:00";
}

// ───────────────────────── window opacity (#156) ─────────────────────────

/// <summary>
/// Per-window opacity (R6 #156): a window may render at 60–100%.
/// The two safety rules outrank the slider — under High Contrast
/// (where see-through chrome would fight the user's system colors)
/// and while the app is locked (a locked window is never anything
/// but fully present) the effective value is always 100%.
/// </summary>
public static class WindowOpacityPolicy
{
    public const double MinOpacity = 0.60;
    public const double MaxOpacity = 1.0;

    /// <summary>Clamp a requested opacity into 60–100%; non-finite input means fully opaque.</summary>
    public static double Clamp(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinOpacity, MaxOpacity) : MaxOpacity;

    /// <summary>What a window may actually render at right now.</summary>
    public static double Effective(double requested, bool highContrast, bool locked) =>
        highContrast || locked ? MaxOpacity : Clamp(requested);
}

// ───────────────────────── tray icon styles (#155) ─────────────────────────

/// <summary>How the tray icon's base is painted (R6 #155).</summary>
public enum TrayIconStyle
{
    /// <summary>The app icon as shipped.</summary>
    Color,

    /// <summary>The app icon in gray tones (the profile dot follows as a gray — see TrayIconPlanner.Compose).</summary>
    Monochrome,

    /// <summary>The whole icon tinted with the active profile's color (and no dot — the icon IS the color signal).</summary>
    ProfileColored,
}

/// <summary>How attention shows on the tray icon (R6 #155): the numbered badge, a plain dot, or nothing (the tooltip still tells).</summary>
public enum TrayOverlayStyle
{
    NumberBadge,
    Dot,
    None,
}

/// <summary>The pixel transform the renderer applies to the base icon before overlays.</summary>
public enum TrayBaseTransform
{
    None,
    Grayscale,
    ProfileTint,
}

/// <summary>
/// The full tray-icon composition (R6 #155): base transform, the dot
/// color ALREADY resolved for the style (a luminance gray under
/// Monochrome, null when the whole icon carries the color or the
/// profile has none), and the badge per the overlay choice.
/// </summary>
public sealed record TrayIconComposition(
    TrayBaseTransform Base, string? ProfileDotHex, bool ShowBadge, string? BadgeText)
{
    /// <summary>True when the composition is exactly the shipped icon — the caller may skip composing entirely.</summary>
    public bool IsPlainIcon => Base == TrayBaseTransform.None && ProfileDotHex is null && !ShowBadge;
}

public static partial class TrayIconPlanner
{
    /// <summary>
    /// Compose the tray icon (R6 #155) from the user's style choices,
    /// the active profile's color (a hex, or null for none), and the
    /// attention signal. The older <see cref="Plan"/> remains the
    /// Color + NumberBadge composition this one generalizes.
    /// </summary>
    public static TrayIconComposition Compose(
        TrayIconStyle style, TrayOverlayStyle overlay, string? profileColorHex, AttentionSignal attention)
    {
        var baseTransform = style switch
        {
            TrayIconStyle.Monochrome => TrayBaseTransform.Grayscale,
            TrayIconStyle.ProfileColored when profileColorHex is not null => TrayBaseTransform.ProfileTint,
            _ => TrayBaseTransform.None,
        };
        string? dotHex = style switch
        {
            // The tinted icon is itself the color signal — a dot too
            // would say the same thing twice.
            TrayIconStyle.ProfileColored => null,
            TrayIconStyle.Monochrome when profileColorHex is not null => HexColor.ToGrayscaleHex(profileColorHex),
            _ => profileColorHex,
        };
        var (showBadge, badgeText) = overlay switch
        {
            TrayOverlayStyle.None => (false, (string?)null),
            TrayOverlayStyle.Dot => (attention.Kind != AttentionKind.None, (string?)null),
            _ => attention.Kind switch
            {
                AttentionKind.Count => (true, (string?)BadgeTextFor(attention.Count)),
                AttentionKind.Dot => (true, (string?)null),
                _ => (false, (string?)null),
            },
        };
        return new TrayIconComposition(baseTransform, dotHex, showBadge, badgeText);
    }
}

// ───────────────────────── theme bundles (#160) ─────────────────────────

/// <summary>
/// A theme bundle (R6 #160): the portable face of the appearance
/// settings — mode, accent (preset key or the custom color), and
/// backdrop. Bundles ride INSIDE the ordinary settings export/import
/// (they are plain settings fields there); this type is the named,
/// tested guarantee that the four pieces travel together, plus the
/// capture/apply pair any future theme-only surface would use.
/// Schedule times are their own fields in the same export, so a
/// FollowSchedule bundle is never stranded without its times.
/// </summary>
public sealed record ThemeBundle(
    ThemeMode Mode, string AccentKey, string? CustomAccentHex, BackdropPreference Backdrop)
{
    public static ThemeBundle Capture(AppSettings settings) => new(
        settings.Theme,
        AccentPalette.Sanitize(settings.AccentColor),
        CustomAccent.TryNormalize(settings.CustomAccent),
        Enum.IsDefined(settings.Backdrop) ? settings.Backdrop : BackdropPreference.Auto);

    /// <summary>Write the bundle into a settings object, normalizing as it goes (a bundle from anywhere is always safe to apply).</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.Theme = Enum.IsDefined(Mode) ? Mode : ThemeMode.Dark;
        settings.AccentColor = AccentPalette.Sanitize(AccentKey);
        settings.CustomAccent = CustomAccent.TryNormalize(CustomAccentHex);
        settings.Backdrop = Enum.IsDefined(Backdrop) ? Backdrop : BackdropPreference.Auto;
    }
}

// ───────────────────────── no white flash (#157) ─────────────────────────

/// <summary>
/// The web view's before-first-paint background (R6 #157): the
/// theme's own background color, chosen here so every web view the
/// shell creates — main, secondary, retry, recovery — paints the
/// same first frame. Null under High Contrast, where the shell maps
/// it to the system window color instead of any palette value.
/// </summary>
public static class WebViewBackgroundPolicy
{
    public static string? ResolveHex(ShellPalette palette, bool highContrast) =>
        highContrast ? null : palette.Bg;
}

// ───────────────────────── mini mode (#154) ─────────────────────────

/// <summary>
/// What the mini window's download cluster shows (R6 #154), counted
/// from the live download set: how many are moving, how many are
/// paused, and which of Pause all / Resume all can do anything.
/// The strip's label and button states are decided here so they are
/// the same in the window and in tests.
/// </summary>
public sealed record MiniDownloadSummary(int Transferring, int Paused)
{
    public int Active => Transferring + Paused;
    public bool CanPause => Transferring > 0;
    public bool CanResume => Paused > 0;

    public string Label => Active == 0
        ? "No active downloads"
        : string.Join(" · ", Parts());

    private IEnumerable<string> Parts()
    {
        if (Transferring > 0)
            yield return Transferring == 1 ? "1 downloading" : $"{Transferring} downloading";
        if (Paused > 0)
            yield return Paused == 1 ? "1 paused" : $"{Paused} paused";
    }

    /// <summary>Count a live download set: only InProgress and Paused entries are active; finished rows the tracker still holds are not.</summary>
    public static MiniDownloadSummary FromDownloads(IEnumerable<ActiveDownload> downloads)
    {
        var transferring = 0;
        var paused = 0;
        foreach (var d in downloads)
        {
            switch (d.State)
            {
                case DownloadActivityState.InProgress: transferring++; break;
                case DownloadActivityState.Paused: paused++; break;
            }
        }
        return new MiniDownloadSummary(transferring, paused);
    }
}
