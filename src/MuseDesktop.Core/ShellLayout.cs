namespace MuseDesktop.Core;

/// <summary>
/// Shell layout (SEAN, 2026-11-01 — "Compact Focus", mockup B, confirmed
/// from image mockups before building; Left Rail is mockup C from the
/// same confirmed set, added for 2.0.0): how the shell's own chrome is
/// arranged around the Muse page. Shell-only by design — the page is
/// never restyled, moved, or hidden by a layout; a layout only changes
/// the toolbar row, the status presentation, and (Left Rail) the
/// navigation rail down the left edge.
/// </summary>
public enum ShellLayout
{
    /// <summary>The long-standing arrangement: density-sized toolbar on top, full status bar along the bottom.</summary>
    Standard,

    /// <summary>Slim 30px toolbar; the status bar is replaced by a small floating status pill over the page's bottom-right corner, so the page gains the bar's height.</summary>
    CompactFocus,

    /// <summary>A 56px vertical rail down the left edge carries navigation, Downloads and the pin, with Settings and the ⋯ menu at its foot; a thin 30px title strip runs across the top and the full status bar stays along the bottom.</summary>
    LeftRail,
}

/// <summary>Stepping between shell layouts, for the command palette's cycle command.</summary>
public static class ShellLayoutCycle
{
    /// <summary>The next layout in the cycle Standard → Compact Focus → Left Rail → Standard. An undefined value steps to Standard, the safe repair.</summary>
    public static ShellLayout Next(ShellLayout layout) => layout switch
    {
        ShellLayout.Standard => ShellLayout.CompactFocus,
        ShellLayout.CompactFocus => ShellLayout.LeftRail,
        _ => ShellLayout.Standard,
    };
}

/// <summary>The chrome measurements a shell layout implies, in WPF device-independent pixels.</summary>
public sealed record ShellLayoutMetrics(
    double ToolbarHeight,
    bool StatusBarVisible,
    bool StatusPillVisible,
    double StatusBarHeight,
    double RailWidth)
{
    /// <summary>Whether the layout puts a navigation rail down the left edge.</summary>
    public bool RailVisible => RailWidth > 0;

    /// <summary>
    /// Resolve a layout against the user's density. Standard defers
    /// entirely to <see cref="DensityMetrics"/> (its long-standing
    /// numbers). Compact Focus pins the toolbar to 30px — slimmer than
    /// even Compact density's 32 — and hides the status bar in favour
    /// of the pill, whatever the density. Left Rail pins the same 30px
    /// strip (it carries only the title and zoom there — navigation
    /// lives on the rail) and keeps the density's status bar.
    /// </summary>
    public static ShellLayoutMetrics For(ShellLayout layout, ShellDensity density)
    {
        var densityMetrics = DensityMetrics.For(density);
        return layout switch
        {
            ShellLayout.CompactFocus => new ShellLayoutMetrics(
                ToolbarHeight: 30,
                StatusBarVisible: false,
                StatusPillVisible: true,
                StatusBarHeight: 0,
                RailWidth: 0),
            ShellLayout.LeftRail => new ShellLayoutMetrics(
                ToolbarHeight: 30,
                StatusBarVisible: true,
                StatusPillVisible: false,
                StatusBarHeight: densityMetrics.StatusBarHeight,
                RailWidth: 56),
            _ => new ShellLayoutMetrics(
                ToolbarHeight: densityMetrics.ToolbarHeight,
                StatusBarVisible: true,
                StatusPillVisible: false,
                StatusBarHeight: densityMetrics.StatusBarHeight,
                RailWidth: 0),
        };
    }

    /// <summary>
    /// How much taller the page area is than under Standard with the
    /// same density: the toolbar saving plus the whole status bar.
    /// This is the number the mockup promised, pinned by tests so the
    /// claim and the code cannot drift apart.
    /// </summary>
    public static double PageHeightGain(ShellLayout layout, ShellDensity density)
    {
        var standard = For(ShellLayout.Standard, density);
        var mine = For(layout, density);
        return (standard.ToolbarHeight - mine.ToolbarHeight)
             + (standard.StatusBarHeight - mine.StatusBarHeight);
    }
}
