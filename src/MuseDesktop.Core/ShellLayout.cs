namespace MuseDesktop.Core;

/// <summary>
/// Shell layout (SEAN, 2026-11-01 — "Compact Focus", mockup B, confirmed
/// from image mockups before building): how the shell's own chrome is
/// arranged around the Muse page. Shell-only by design — the page is
/// never restyled, moved, or hidden by a layout; a layout only changes
/// the toolbar row, the status bar, and the status pill that replaces
/// the bar in Compact Focus.
/// </summary>
public enum ShellLayout
{
    /// <summary>The long-standing arrangement: density-sized toolbar on top, full status bar along the bottom.</summary>
    Standard,

    /// <summary>Slim 30px toolbar; the status bar is replaced by a small floating status pill over the page's bottom-right corner, so the page gains the bar's height.</summary>
    CompactFocus,
}

/// <summary>The chrome measurements a shell layout implies, in WPF device-independent pixels.</summary>
public sealed record ShellLayoutMetrics(
    double ToolbarHeight,
    bool StatusBarVisible,
    bool StatusPillVisible,
    double StatusBarHeight)
{
    /// <summary>
    /// Resolve a layout against the user's density. Standard defers
    /// entirely to <see cref="DensityMetrics"/> (its long-standing
    /// numbers). Compact Focus pins the toolbar to 30px — slimmer than
    /// even Compact density's 32 — and hides the status bar in favour
    /// of the pill, whatever the density.
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
                StatusBarHeight: 0),
            _ => new ShellLayoutMetrics(
                ToolbarHeight: densityMetrics.ToolbarHeight,
                StatusBarVisible: true,
                StatusPillVisible: false,
                StatusBarHeight: densityMetrics.StatusBarHeight),
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
