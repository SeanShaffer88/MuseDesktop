using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using MuseDesktop.Core;
// The WinForms global usings make these names ambiguous with
// System.Drawing / System.Windows.Forms — pin the WPF ones, the
// same discipline RegionPickerWindow.xaml.cs uses.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace MuseDesktop;

// ─────────────────────── screenshot → ask: one screen's region (M1) ───────────────────────
//
// The single-monitor sibling of the region picker: where
// RegionPicker.Pick drags across every screen in PHYSICAL pixels
// (its per-monitor overlays exist so each conversion stays inside
// one scale), this overlay covers ONE chosen monitor and the drag
// is tracked in that window's own DIPs — the space a lone window
// honestly owns, with no cross-monitor bookkeeping at all. The
// selection comes back as a DIP rectangle in the monitor's local
// space, and Core's ScreenshotRegion turns it into the physical
// rectangle the capture takes (that monitor's scale, clamped to
// that monitor) — the conversion is Core's because it is a
// decision, and this window's job is only the drag and the dim.
// Esc or a right-click cancels; whether a finished drag was big
// enough to count is the caller's question (Core's minimum-size
// rule, applied to the converted physical rectangle, exactly as
// the any-screen path applies it).

/// <summary>One monitor's region overlay (M1): the dim, the selection outline, and the captured-size label.</summary>
public partial class MonitorRegionWindow : Window
{
    private readonly MonitorGeometry _monitor;
    private bool _dragging;
    private Point _anchor;
    private Point _current;

    internal MonitorRegionWindow(MonitorGeometry monitor)
    {
        InitializeComponent();
        _monitor = monitor;
        Left = monitor.Dip.Left;
        Top = monitor.Dip.Top;
        Width = monitor.Dip.Width;
        Height = monitor.Dip.Height;
        SourceInitialized += (_, _) => PinToPhysicalRect();
        RefreshSelection();
    }

    /// <summary>The finished drag, normalized, in this monitor's local DIP space (its top-left is the monitor's); null for a cancel or no drag.</summary>
    public ScreenRect? SelectionDip { get; private set; }

    /// <summary>
    /// WPF placed the window from the DIP rect; pin it to the
    /// monitor's exact PHYSICAL rect once the handle exists — the
    /// region picker's discipline, so the overlay covers precisely
    /// this monitor whatever a mixed-DPI desktop made of the
    /// placement conversion.
    /// </summary>
    private void PinToPhysicalRect()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            var physical = _monitor.Physical;
            SetWindowPos(hwnd, HwndTopmost,
                (int)Math.Round(physical.Left), (int)Math.Round(physical.Top),
                (int)Math.Round(physical.Width), (int)Math.Round(physical.Height),
                SwpNoActivate);
        }
        catch { /* the DIP placement stands on its own if this fails */ }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _anchor = _current = e.GetPosition(Surface);
        _dragging = true;
        RefreshSelection();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
            return;
        _current = e.GetPosition(Surface);
        RefreshSelection();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging)
            return;
        _dragging = false;
        SelectionDip = NormalizedDip();
        Close();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Close();   // SelectionDip stays null: a cancel
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Close();   // SelectionDip stays null: a cancel
    }

    /// <summary>The drag's current rectangle in local DIPs, normalized to positive size whatever direction it went.</summary>
    private ScreenRect NormalizedDip() => new(
        Math.Min(_anchor.X, _current.X),
        Math.Min(_anchor.Y, _current.Y),
        Math.Abs(_current.X - _anchor.X),
        Math.Abs(_current.Y - _anchor.Y));

    // ── drawing: the dim around the drag, the outline, the size ──

    private void RefreshSelection()
    {
        var width = _monitor.Dip.Width;
        var height = _monitor.Dip.Height;
        if (!_dragging)
        {
            SetRect(DimTop, 0, 0, width, height);
            SetRect(DimLeft, 0, 0, 0, 0);
            SetRect(DimRight, 0, 0, 0, 0);
            SetRect(DimBottom, 0, 0, width, 0);
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        var dip = NormalizedDip();
        SetRect(DimTop, 0, 0, width, dip.Top);
        SetRect(DimBottom, 0, dip.Bottom, width, height - dip.Bottom);
        SetRect(DimLeft, 0, dip.Top, dip.Left, dip.Height);
        SetRect(DimRight, dip.Right, dip.Top, width - dip.Right, dip.Height);

        Canvas.SetLeft(SelectionBorder, dip.Left);
        Canvas.SetTop(SelectionBorder, dip.Top);
        SelectionBorder.Width = dip.Width;
        SelectionBorder.Height = dip.Height;
        SelectionBorder.Visibility = Visibility.Visible;

        // The label names the size the capture will take — the
        // PHYSICAL size, from Core's conversion of this very drag —
        // so what the user reads is what the picture will be.
        var physical = ScreenshotRegion.ToPhysicalRect(
            dip, MonitorRects.PhysicalPixels(_monitor.Physical), _monitor.DpiScale);
        if (physical is { } rect)
        {
            SizeLabelText.Text = string.Create(
                CultureInfo.InvariantCulture, $"{rect.Width} × {rect.Height}");
            SizeLabel.Visibility = Visibility.Visible;
            SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelWidth = SizeLabel.DesiredSize.Width;
            var labelHeight = SizeLabel.DesiredSize.Height;
            var labelX = Math.Clamp(dip.Left, 4, Math.Max(4, width - labelWidth - 4));
            var labelY = dip.Top - labelHeight - 6 >= 0 ? dip.Top - labelHeight - 6 : dip.Top + 6;
            Canvas.SetLeft(SizeLabel, labelX);
            Canvas.SetTop(SizeLabel, labelY);
        }
        else
        {
            SizeLabel.Visibility = Visibility.Collapsed;
        }
    }

    private static void SetRect(Rectangle element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

/// <summary>
/// Runs one single-screen pick (M1): shows the overlay on the
/// chosen monitor, and returns the dragged rectangle in that
/// monitor's local DIP space — or null for a cancel. Modal in
/// effect, like the region picker: a nested dispatcher frame pumps
/// until the window closes, and the window is closed before it
/// returns, so a capture taken afterwards sees the real screen.
/// </summary>
public static class MonitorRegionPicker
{
    public static ScreenRect? Pick(MonitorGeometry monitor)
    {
        var window = new MonitorRegionWindow(monitor);
        var frame = new DispatcherFrame();
        window.Closed += (_, _) => frame.Continue = false;
        window.Show();
        window.Activate();
        window.Focus();
        Dispatcher.PushFrame(frame);
        return window.SelectionDip;
    }
}
