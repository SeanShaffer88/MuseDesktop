namespace MuseDesktop.Core;

/// <summary>
/// A rectangle in device-independent pixels (the units WPF window
/// placement uses). The shell converts each physical monitor rect with
/// <see cref="DipMath"/> before anything here sees it, so mixed-scale
/// monitor sets are handled in one consistent space.
/// </summary>
public readonly record struct ScreenRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    /// <summary>The overlap of two rects; zero-area when they don't intersect.</summary>
    public ScreenRect Intersect(ScreenRect other)
    {
        var left = Math.Max(Left, other.Left);
        var top = Math.Max(Top, other.Top);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return new ScreenRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}

public static class DipMath
{
    /// <summary>
    /// Physical pixels → DIPs for one monitor. <paramref name="dpiScale"/>
    /// is the monitor's scale factor (1.0 at 96 DPI, 1.5 at 144 DPI);
    /// a non-positive or NaN factor means "unknown" and is treated as 1.0.
    /// </summary>
    public static ScreenRect PhysicalToDip(ScreenRect physical, double dpiScale)
    {
        if (double.IsNaN(dpiScale) || dpiScale <= 0)
            dpiScale = 1.0;
        return new ScreenRect(
            physical.Left / dpiScale, physical.Top / dpiScale,
            physical.Width / dpiScale, physical.Height / dpiScale);
    }
}

/// <summary>
/// Window-placement clamping for multi-monitor changes (v0.10.0, S6
/// #60). A placement saved against a monitor that has since been
/// disconnected (a laptop undocked, a display unplugged) must not
/// restore the window into the void: if less than a minimum sliver of
/// the window would be visible on the largest single monitor overlap,
/// the window is moved onto the primary (first) monitor, fitted inside
/// it where it fits and pinned to its top-left corner where it doesn't.
/// A window that straddles two live monitors with enough showing on one
/// of them is left exactly where it was.
/// </summary>
public static class PlacementClamp
{
    public const double DefaultMinVisibleWidth = 80;
    public const double DefaultMinVisibleHeight = 40;

    public static ScreenRect Clamp(
        ScreenRect window,
        IReadOnlyList<ScreenRect> monitors,
        double minVisibleWidth = DefaultMinVisibleWidth,
        double minVisibleHeight = DefaultMinVisibleHeight)
    {
        if (monitors.Count == 0)
            return window;
        var bestVisible = monitors
            .Select(m => window.Intersect(m))
            .OrderByDescending(r => r.Width * r.Height)
            .First();
        if (bestVisible.Width >= minVisibleWidth && bestVisible.Height >= minVisibleHeight)
            return window;

        var primary = monitors[0];
        var left = window.Width >= primary.Width
            ? primary.Left
            : Math.Clamp(window.Left, primary.Left, primary.Right - window.Width);
        var top = window.Height >= primary.Height
            ? primary.Top
            : Math.Clamp(window.Top, primary.Top, primary.Bottom - window.Height);
        return new ScreenRect(left, top, window.Width, window.Height);
    }
}
