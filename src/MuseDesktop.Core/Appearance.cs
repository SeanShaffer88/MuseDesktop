namespace MuseDesktop.Core;

// ───────────────────────── shell theme (S5 #43) ─────────────────────────

/// <summary>Which shell palette MuseDesktop paints its own chrome with (S5 #43). The muse.ai page is never restyled — this is shell only.</summary>
public enum ThemeMode
{
    /// <summary>The product-family black/red identity (the long-standing look).</summary>
    Dark,

    /// <summary>A light shell palette; the accent color still identifies the app.</summary>
    Light,

    /// <summary>Match the Windows app theme (AppsUseLightTheme), re-evaluated at startup and when Windows reports a change.</summary>
    FollowSystem,

    /// <summary>Light between the user's own light-start and dark-start times (R6 #159), re-evaluated every minute. Appended last so stored values and index-based UI mappings for the older modes never shift.</summary>
    FollowSchedule,
}

/// <summary>
/// One shell palette: the seven surface/text tokens every shell window
/// paints from. Hex strings, like <see cref="ProfileColors"/>, so Core
/// stays free of WPF types and the values are unit-testable — including
/// their WCAG contrast ratios (see <see cref="ColorContrast"/>).
/// </summary>
public sealed record ShellPalette(
    string Bg, string Surface, string Raised, string Hairline,
    string Text, string Muted, string Faint);

public static class ShellPalettes
{
    /// <summary>The dark identity the app has always shipped (matches the tokens in App.xaml).</summary>
    public static readonly ShellPalette Dark = new(
        Bg: "#0B0B0D", Surface: "#141417", Raised: "#1D1D21", Hairline: "#2C2C31",
        Text: "#F5F5F7", Muted: "#A7A7AD", Faint: "#6E6E75");

    /// <summary>
    /// The light palette (S5 #43): near-white surfaces, near-black text,
    /// the same token roles. Text and Muted hold ≥ 4.5:1 against every
    /// light surface (pinned by tests); Faint is de-emphasis only, ≥ 3:1.
    /// </summary>
    public static readonly ShellPalette Light = new(
        Bg: "#F5F5F6", Surface: "#FFFFFF", Raised: "#ECECEF", Hairline: "#D8D8DE",
        Text: "#17171A", Muted: "#55565E", Faint: "#83838C");

    public static ShellPalette For(bool light) => light ? Light : Dark;
}

public static class ThemeResolution
{
    /// <summary>Dark unless the mode says light or the system does (S5 #43). An undefined mode value means Dark. FollowSchedule reads as Dark through this overload — a caller with no clock must not guess at light.</summary>
    public static bool IsLight(ThemeMode mode, bool systemIsLight) =>
        IsLight(mode, systemIsLight, scheduleIsLight: false);

    /// <summary>The full resolution (R6 #159): the schedule input only ever speaks for FollowSchedule.</summary>
    public static bool IsLight(ThemeMode mode, bool systemIsLight, bool scheduleIsLight) => mode switch
    {
        ThemeMode.Light => true,
        ThemeMode.FollowSystem => systemIsLight,
        ThemeMode.FollowSchedule => scheduleIsLight,
        _ => false,
    };
}

/// <summary>WCAG 2.x relative-luminance and contrast-ratio math over #RRGGBB hex colors.</summary>
public static class ColorContrast
{
    public static double RelativeLuminance(string hex)
    {
        var (r, g, b) = Parse(hex);
        return 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);
    }

    /// <summary>Contrast ratio between two colors, 1.0 (identical) to 21.0 (black on white).</summary>
    public static double Ratio(string first, string second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        var (hi, lo) = a >= b ? (a, b) : (b, a);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static (double R, double G, double B) Parse(string hex)
    {
        var h = hex.Trim().TrimStart('#');
        if (h.Length != 6)
            throw new ArgumentException($"Expected a #RRGGBB color, got '{hex}'.", nameof(hex));
        return (Convert.ToInt32(h[..2], 16) / 255.0,
                Convert.ToInt32(h[2..4], 16) / 255.0,
                Convert.ToInt32(h[4..6], 16) / 255.0);
    }

    private static double Linearize(double channel) =>
        channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
}

// ───────────────────────── accent color (S5 #44) ─────────────────────────

/// <summary>
/// The shell accent palette (S5 #44) — shell highlights only; profile
/// colors (<see cref="ProfileColors"/>) are a separate feature and are
/// never touched by this. Each accent has TWO variants, because no
/// single hex can be readable text on both a near-black and a
/// near-white background while also carrying white text on a filled
/// button (the ratios are pinned by tests):
/// <list type="bullet">
/// <item><c>DarkShellHex</c> — the brighter variant, used as text /
/// decoration on the dark shell (≥ 4.5:1 vs the dark surfaces).</item>
/// <item><c>LightShellHex</c> — the deeper variant, used as text on the
/// light shell (≥ 4.5:1 vs the light surfaces) AND as the fill of
/// accent buttons in both themes, where white text on it is ≥ 4.5:1.</item>
/// </list>
/// </summary>
public static class AccentPalette
{
    public const string DefaultKey = "Red";

    public static readonly IReadOnlyList<(string Key, string DarkShellHex, string LightShellHex)> Palette = new[]
    {
        ("Red", "#F87171", "#D21F26"),
        ("Blue", "#60A5FA", "#1D4ED8"),
        ("Teal", "#2DD4BF", "#0F766E"),
        ("Purple", "#A78BFA", "#6D28D9"),
        ("Orange", "#FB923C", "#C2410C"),
    };

    /// <summary>The canonical palette key for <paramref name="key"/> (case-insensitive), or the default when it names no accent.</summary>
    public static string Sanitize(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var trimmed = key.Trim();
            foreach (var (k, _, _) in Palette)
                if (string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase))
                    return k;
        }
        return DefaultKey;
    }

    /// <summary>The (dark-shell, light-shell) hex pair for a key; unknown keys resolve to the default accent.</summary>
    public static (string DarkShellHex, string LightShellHex) For(string? key)
    {
        var canonical = Sanitize(key);
        foreach (var (k, dark, light) in Palette)
            if (k == canonical)
                return (dark, light);
        return (Palette[0].DarkShellHex, Palette[0].LightShellHex); // unreachable — Sanitize only returns palette keys
    }
}

// ───────────────────────── toolbar layout (S5 #45) ─────────────────────────

public enum ToolbarGroup
{
    Left,
    Right,
}

/// <summary>One customizable toolbar element (S5 #45). The ⋯ overflow menu is deliberately NOT an item — it is fixed, last, always available.</summary>
public sealed record ToolbarItem(string Id, string DisplayName, ToolbarGroup Group, bool CanHide);

/// <summary>
/// The toolbar layout model (S5 #45): an ORDER list (every known id,
/// in display order) plus a HIDDEN set. Keeping them separate is what
/// lets sanitize append a future new button at its canonical place
/// instead of mistaking "not in the list" for "hidden by the user".
/// Invariants after <see cref="Sanitize"/>: order holds every known id
/// exactly once; hidden holds only known, hideable ids; Back is never
/// hidden (the toolbar must always offer navigation).
/// </summary>
public static class ToolbarLayout
{
    public const string BackId = "back";

    public static readonly IReadOnlyList<ToolbarItem> All = new[]
    {
        new ToolbarItem(BackId, "Back", ToolbarGroup.Left, CanHide: false),
        new ToolbarItem("forward", "Forward", ToolbarGroup.Left, CanHide: true),
        new ToolbarItem("reload", "Reload", ToolbarGroup.Left, CanHide: true),
        new ToolbarItem("home", "Home", ToolbarGroup.Left, CanHide: true),
        new ToolbarItem("zoomout", "Zoom out", ToolbarGroup.Right, CanHide: true),
        new ToolbarItem("zoomlabel", "Zoom % label", ToolbarGroup.Right, CanHide: true),
        new ToolbarItem("zoomin", "Zoom in", ToolbarGroup.Right, CanHide: true),
        new ToolbarItem("pin", "Always-on-top pin", ToolbarGroup.Right, CanHide: true),
        new ToolbarItem("browser", "Open in browser", ToolbarGroup.Right, CanHide: true),
    };

    private static readonly Dictionary<string, ToolbarItem> ById =
        All.ToDictionary(i => i.Id, StringComparer.Ordinal);

    public static ToolbarItem? Find(string? id) =>
        id is not null && ById.TryGetValue(id, out var item) ? item : null;

    public static List<string> DefaultOrder() => All.Select(i => i.Id).ToList();

    /// <summary>
    /// Repair a stored layout: drop unknown ids and duplicates, restore
    /// any known id the order is missing (at its canonical position
    /// relative to its surviving neighbors), and un-hide anything that
    /// cannot be hidden. Null inputs mean "never customized" → defaults.
    /// </summary>
    public static (List<string> Order, List<string> Hidden) Sanitize(
        IEnumerable<string>? order, IEnumerable<string>? hidden)
    {
        var cleanOrder = new List<string>();
        if (order is not null)
        {
            foreach (var id in order)
            {
                if (id is not null && ById.ContainsKey(id) && !cleanOrder.Contains(id))
                    cleanOrder.Add(id);
            }
        }
        // Append missing known ids at their canonical spot: walk the
        // canonical list and insert each missing id after the last
        // canonical predecessor that is present (or at the front).
        foreach (var item in All)
        {
            if (cleanOrder.Contains(item.Id))
                continue;
            var canonicalIndex = All.ToList().IndexOf(item);
            var insertAt = 0;
            for (var i = canonicalIndex - 1; i >= 0; i--)
            {
                var predecessor = cleanOrder.IndexOf(All[i].Id);
                if (predecessor >= 0)
                {
                    insertAt = predecessor + 1;
                    break;
                }
            }
            cleanOrder.Insert(insertAt, item.Id);
        }

        var cleanHidden = new List<string>();
        if (hidden is not null)
        {
            foreach (var id in hidden)
            {
                if (id is not null && ById.TryGetValue(id, out var item)
                    && item.CanHide && !cleanHidden.Contains(id))
                    cleanHidden.Add(id);
            }
        }
        return (cleanOrder, cleanHidden);
    }

    /// <summary>The visible ids of one group, in layout order. Inputs are expected sanitized (AppSettings.Sanitized does it).</summary>
    public static IReadOnlyList<string> VisibleIds(
        IEnumerable<string> order, IEnumerable<string> hidden, ToolbarGroup group)
    {
        var hiddenSet = new HashSet<string>(hidden, StringComparer.Ordinal);
        return order.Where(id => ById.TryGetValue(id, out var item)
                                 && item.Group == group && !hiddenSet.Contains(id))
                    .ToList();
    }
}

// ───────────────────────── shell text size (S5 #46) ─────────────────────────

/// <summary>
/// Shell UI text scale (S5 #46): the offered factors are 100 / 125 /
/// 150%. Deliberately a scale of the shell's own text only — page zoom
/// (ZoomMath) is a separate, per-profile concern.
/// </summary>
public static class ShellTextScale
{
    public static readonly IReadOnlyList<double> Allowed = new[] { 1.0, 1.25, 1.5 };

    /// <summary>Nearest allowed factor; non-finite input means 100%.</summary>
    public static double Sanitize(double value)
    {
        if (!double.IsFinite(value))
            return 1.0;
        var best = Allowed[0];
        foreach (var candidate in Allowed)
        {
            if (Math.Abs(candidate - value) < Math.Abs(best - value))
                best = candidate;
        }
        return best;
    }

    public static string FormatPercent(double scale) =>
        $"{(int)Math.Round(Sanitize(scale) * 100)}%";
}
