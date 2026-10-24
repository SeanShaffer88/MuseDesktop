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
    public static IReadOnlyList<ScreenRect> AllInDip()
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
                var physical = new ScreenRect(
                    info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left,
                    info.Monitor.Bottom - info.Monitor.Top);
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
            new ScreenRect(
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
