using System.Runtime.InteropServices;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The connected monitors as DIP rectangles (v0.10.0, S6 #60), primary
/// first — the input Core's <see cref="PlacementClamp"/> needs. Each
/// monitor's physical rect (EnumDisplayMonitors / GetMonitorInfo) is
/// converted with THAT monitor's own DPI scale (GetDpiForMonitor), so
/// a 150% 4K panel beside a 100% 1080p panel clamps correctly in the
/// DIP space WPF placement uses. Any failure falls back to the virtual
/// screen as a single "monitor" — the pre-S6 behavior.
/// </summary>
public static class MonitorRects
{
    public static IReadOnlyList<ScreenRect> AllInDip() => Enumerate(workArea: false);

    /// <summary>
    /// The monitors' WORK AREAS in DIPs (R1 #104): same enumeration,
    /// but each rect excludes the taskbar and other app bars — the
    /// space a tiled window should actually fill. The fallback is the
    /// system work area of the virtual screen.
    /// </summary>
    public static IReadOnlyList<ScreenRect> AllWorkAreasInDip() => Enumerate(workArea: true);

    /// <summary>
    /// Every monitor's full geometry (v2.1.0, screenshot → ask): the
    /// PHYSICAL rect, the same rect in DIPs (converted with that
    /// monitor's own scale, exactly as <see cref="AllInDip"/> does),
    /// and the scale itself — primary first. The region picker needs
    /// all three at once: it parks one overlay window per monitor at
    /// the DIP rect, then does its drag maths in physical pixels
    /// with that monitor's scale, so a selection stays exact on
    /// mixed-DPI setups. Any failure falls back to the virtual
    /// screen as a single 1.0-scale "monitor", like the rest of
    /// this class.
    /// </summary>
    public static IReadOnlyList<MonitorGeometry> AllGeometries()
    {
        var monitors = new List<MonitorGeometry>();
        try
        {
            var ok = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
            {
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(hMonitor, ref info))
                    return true;
                var scale = 1.0;
                try
                {
                    if (GetDpiForMonitor(hMonitor, MonitorDpiType.EffectiveDpi, out var dpiX, out _) == 0
                        && dpiX > 0)
                        scale = dpiX / 96.0;
                }
                catch { /* scale stays 1.0 for this monitor */ }
                var physical = new ScreenRect(
                    info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left,
                    info.Monitor.Bottom - info.Monitor.Top);
                var workPhysical = new ScreenRect(
                    info.WorkArea.Left, info.WorkArea.Top,
                    info.WorkArea.Right - info.WorkArea.Left,
                    info.WorkArea.Bottom - info.WorkArea.Top);
                monitors.Add(new MonitorGeometry(
                    physical,
                    DipMath.PhysicalToDip(physical, scale),
                    scale,
                    (info.Flags & MonitorInfoPrimary) != 0)
                {
                    WorkAreaDip = DipMath.PhysicalToDip(workPhysical, scale),
                });
                return true;
            }, IntPtr.Zero);
            if (ok && monitors.Count > 0)
                return monitors.OrderByDescending(m => m.IsPrimary).ToList();
        }
        catch { /* fall through to the virtual-screen fallback */ }

        var fallback = new ScreenRect(
            System.Windows.SystemParameters.VirtualScreenLeft,
            System.Windows.SystemParameters.VirtualScreenTop,
            System.Windows.SystemParameters.VirtualScreenWidth,
            System.Windows.SystemParameters.VirtualScreenHeight);
        return new[]
        {
            new MonitorGeometry(fallback, fallback, 1.0, IsPrimary: true)
            {
                WorkAreaDip = new ScreenRect(
                    System.Windows.SystemParameters.WorkArea.Left,
                    System.Windows.SystemParameters.WorkArea.Top,
                    System.Windows.SystemParameters.WorkArea.Width,
                    System.Windows.SystemParameters.WorkArea.Height),
            },
        };
    }

    /// <summary>
    /// A monitor rectangle (a <see cref="ScreenRect"/> built from
    /// whole physical pixels) as Core's integer
    /// <see cref="PhysicalRect"/> — the shape captures and Core's
    /// region maths speak. Rounds, so a rect that somehow carries
    /// fractions lands on the nearest pixel.
    /// </summary>
    public static PhysicalRect PhysicalPixels(ScreenRect rect) => new(
        (int)Math.Round(rect.Left), (int)Math.Round(rect.Top),
        (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));

    /// <summary>
    /// The cursor's position in PHYSICAL pixels (M3) — the space
    /// <see cref="MonitorGeometry.Physical"/> lives in, so Core's
    /// point-in-monitor choice can consume both directly. Null when
    /// the position can't be read; the caller then docks wherever
    /// it would have docked before (the primary monitor).
    /// </summary>
    public static (int X, int Y)? CursorPhysicalPoint()
    {
        try
        {
            return GetCursorPos(out var point) ? (point.X, point.Y) : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<ScreenRect> Enumerate(bool workArea)
    {
        var monitors = new List<(ScreenRect Dip, bool Primary)>();
        try
        {
            var ok = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
            {
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(hMonitor, ref info))
                    return true;
                var scale = 1.0;
                try
                {
                    if (GetDpiForMonitor(hMonitor, MonitorDpiType.EffectiveDpi, out var dpiX, out _) == 0
                        && dpiX > 0)
                        scale = dpiX / 96.0;
                }
                catch { /* scale stays 1.0 for this monitor */ }
                var rect = workArea ? info.WorkArea : info.Monitor;
                var physical = new ScreenRect(
                    rect.Left, rect.Top,
                    rect.Right - rect.Left,
                    rect.Bottom - rect.Top);
                monitors.Add((DipMath.PhysicalToDip(physical, scale),
                              (info.Flags & MonitorInfoPrimary) != 0));
                return true;
            }, IntPtr.Zero);
            if (ok && monitors.Count > 0)
                return monitors.OrderByDescending(m => m.Primary).Select(m => m.Dip).ToList();
        }
        catch { /* fall through to the virtual-screen fallback */ }

        return new[]
        {
            workArea
                ? new ScreenRect(
                    System.Windows.SystemParameters.WorkArea.Left,
                    System.Windows.SystemParameters.WorkArea.Top,
                    System.Windows.SystemParameters.WorkArea.Width,
                    System.Windows.SystemParameters.WorkArea.Height)
                : new ScreenRect(
                    System.Windows.SystemParameters.VirtualScreenLeft,
                    System.Windows.SystemParameters.VirtualScreenTop,
                    System.Windows.SystemParameters.VirtualScreenWidth,
                    System.Windows.SystemParameters.VirtualScreenHeight),
        };
    }

    private const uint MonitorInfoPrimary = 0x00000001;

    private enum MonitorDpiType
    {
        EffectiveDpi = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
}

/// <summary>
/// One monitor in both coordinate spaces at once (v2.1.0):
/// <see cref="Physical"/> is its rectangle in physical pixels (the
/// space the screen, the cursor, and a capture live in);
/// <see cref="Dip"/> is the same rectangle in the DIPs WPF window
/// placement uses, converted with <see cref="DpiScale"/> — this
/// monitor's own factor, never a neighbour's.
/// </summary>
public sealed record MonitorGeometry(
    ScreenRect Physical,
    ScreenRect Dip,
    double DpiScale,
    bool IsPrimary)
{
    /// <summary>
    /// This monitor's WORK AREA in DIPs (M3) — the full rect minus
    /// the taskbar and other app bars, converted with this
    /// monitor's own scale. Quick ask docks inside it on the
    /// monitor under the cursor. Defaults to the full DIP rect
    /// when no enumeration filled it in.
    /// </summary>
    public ScreenRect WorkAreaDip { get; init; } = Dip;
}
