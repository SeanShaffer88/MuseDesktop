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
}
