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
// same discipline MainWindow.xaml.cs uses.
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace MuseDesktop;

// ─────────────────────── screenshot → ask: the region picker (v2.1.0) ───────────────────────
//
// The user drags a rectangle anywhere on the screen; the rectangle
// comes back in PHYSICAL pixels, ready for the GDI capture.
//
// Why one overlay window PER MONITOR instead of one window over the
// virtual screen (see DECISIONS, 2026-11-02): under Per-Monitor
// DPI v2 a window owns exactly one DPI scale — its monitor's. One
// virtual-screen window would render and report its mouse positions
// in the PRIMARY monitor's scale, so on a 150% panel beside a 100%
// panel the outline the user drags would drift away from the pixels
// the capture would take on one of them. One window per monitor
// keeps every conversion inside a single scale: each overlay is
// parked on its monitor's DIP rect (MonitorRects' own conversion,
// the one window placement already trusts), and the drag is tracked
// in physical pixels — each overlay turns its local DIP point into
// a physical point with ITS monitor's origin and scale, the session
// keeps the selection as a physical rectangle (Core
// ScreenshotSelection does the maths), and each overlay draws only
// its own monitor's slice of it. A drag can cross monitors freely.
//
// Deliberately NO mouse capture: capture would route every move to
// the window where the drag started, whose scale is the wrong one
// for the monitor the pointer has wandered onto. Without it, the
// overlay under the pointer reports each move — and every monitor
// is covered, so there is always one. Esc or a right-click cancels;
// a drag under Core's minimum size counts as a cancel.

/// <summary>One monitor's picker overlay: the dim, the selection outline, and the size label for its slice of the shared drag.</summary>
public partial class RegionPickerWindow : Window
{
    private readonly MonitorGeometry _monitor;
    private readonly RegionPicker.Session _session;

    internal RegionPickerWindow(MonitorGeometry monitor, RegionPicker.Session session)
    {
        InitializeComponent();
        _monitor = monitor;
        _session = session;
        Left = monitor.Dip.Left;
        Top = monitor.Dip.Top;
        Width = monitor.Dip.Width;
        Height = monitor.Dip.Height;
        SourceInitialized += (_, _) => PinToPhysicalRect();
        RefreshSelection();
    }

    /// <summary>
    /// WPF placed the window from the DIP rect; pin it to the
    /// monitor's exact PHYSICAL rect once the handle exists, so the
    /// overlay covers precisely this monitor whatever the placement
    /// conversion made of a mixed-DPI desktop. SetWindowPos speaks
    /// the window's own DPI context — this monitor's — which is the
    /// physical space of <see cref="MonitorGeometry.Physical"/>.
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

    // ── pointer → session (this monitor's scale does the conversion) ──

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _session.Begin(ToPhysical(e.GetPosition(Surface)));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_session.Dragging)
            _session.Update(ToPhysical(e.GetPosition(Surface)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _session.End();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        _session.Cancel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            _session.Cancel();
    }

    /// <summary>A point in this window's DIPs → the physical pixel it names, via this monitor's origin and scale.</summary>
    private (int X, int Y) ToPhysical(Point dip) =>
        ((int)Math.Round(_monitor.Physical.Left + dip.X * _monitor.DpiScale),
         (int)Math.Round(_monitor.Physical.Top + dip.Y * _monitor.DpiScale));

    // ── drawing: this monitor's slice of the shared selection ──

    /// <summary>Redraw the dim and (during a drag) the outline + size label for the session's current physical selection.</summary>
    internal void RefreshSelection()
    {
        var width = _monitor.Dip.Width;
        var height = _monitor.Dip.Height;
        var selection = _session.CurrentSelection;
        if (selection is not { } rect)
        {
            SetRect(DimTop, 0, 0, width, height);
            SetRect(DimLeft, 0, 0, 0, 0);
            SetRect(DimRight, 0, 0, 0, 0);
            SetRect(DimBottom, 0, 0, width, 0);
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            return;
        }

        // Intersect the physical selection with this monitor, in
        // physical pixels, then draw the intersection in local DIPs.
        var phys = _monitor.Physical;
        var ix0 = Math.Max(rect.Left, (int)Math.Round(phys.Left));
        var iy0 = Math.Max(rect.Top, (int)Math.Round(phys.Top));
        var ix1 = Math.Min(rect.Right, (int)Math.Round(phys.Right));
        var iy1 = Math.Min(rect.Bottom, (int)Math.Round(phys.Bottom));
        if (ix1 <= ix0 || iy1 <= iy0)
        {
            // The drag is on another monitor: stay fully dimmed.
            SetRect(DimTop, 0, 0, width, height);
            SetRect(DimLeft, 0, 0, 0, 0);
            SetRect(DimRight, 0, 0, 0, 0);
            SetRect(DimBottom, 0, 0, width, 0);
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        var scale = _monitor.DpiScale;
        var dx0 = (ix0 - phys.Left) / scale;
        var dy0 = (iy0 - phys.Top) / scale;
        var dx1 = (ix1 - phys.Left) / scale;
        var dy1 = (iy1 - phys.Top) / scale;

        SetRect(DimTop, 0, 0, width, dy0);
        SetRect(DimBottom, 0, dy1, width, height - dy1);
        SetRect(DimLeft, 0, dy0, dx0, dy1 - dy0);
        SetRect(DimRight, dx1, dy0, width - dx1, dy1 - dy0);

        Canvas.SetLeft(SelectionBorder, dx0);
        Canvas.SetTop(SelectionBorder, dy0);
        SelectionBorder.Width = dx1 - dx0;
        SelectionBorder.Height = dy1 - dy0;
        SelectionBorder.Visibility = Visibility.Visible;

        // The label rides the monitor that holds the selection's
        // top-left corner, just above the outline (inside it when
        // the selection hugs the top edge).
        if (rect.Left >= phys.Left && rect.Left < phys.Right
            && rect.Top >= phys.Top && rect.Top < phys.Bottom)
        {
            SizeLabelText.Text = string.Create(
                CultureInfo.InvariantCulture, $"{rect.Width} × {rect.Height}");
            SizeLabel.Visibility = Visibility.Visible;
            SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelWidth = SizeLabel.DesiredSize.Width;
            var labelHeight = SizeLabel.DesiredSize.Height;
            var labelX = Math.Clamp(dx0, 4, Math.Max(4, width - labelWidth - 4));
            var labelY = dy0 - labelHeight - 6 >= 0 ? dy0 - labelHeight - 6 : dy0 + 6;
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
/// Runs one pick: shows an overlay on every monitor, tracks the
/// drag, and returns the chosen rectangle in physical pixels — or
/// null for a cancel (Esc, right-click, or a drag too small to
/// count). Modal in effect: it pumps a nested dispatcher frame
/// until the drag resolves, exactly like a dialog, and every
/// overlay is closed before it returns, so a capture taken
/// afterwards sees the real screen.
/// </summary>
public static class RegionPicker
{
    public static PhysicalRect? Pick()
    {
        var geometries = MonitorRects.AllGeometries();
        if (geometries.Count == 0)
            return null;
        var session = new Session(geometries);
        foreach (var geometry in geometries)
            session.Overlays.Add(new RegionPickerWindow(geometry, session));
        var frame = new DispatcherFrame();
        session.Completed += () => frame.Continue = false;
        foreach (var overlay in session.Overlays)
            overlay.Show();
        // Esc needs a focused window: the primary monitor's overlay
        // (geometries come back primary-first).
        session.Overlays[0].Activate();
        session.Overlays[0].Focus();
        Dispatcher.PushFrame(frame);
        foreach (var overlay in session.Overlays)
            overlay.Close();
        return session.Result;
    }

    /// <summary>The shared drag state every overlay reads and reports into: anchor + current corner, in physical pixels, clamped to the virtual screen.</summary>
    internal sealed class Session
    {
        private readonly PhysicalRect _bounds;
        private int _anchorX;
        private int _anchorY;
        private int _currentX;
        private int _currentY;
        private bool _finished;

        public Session(IReadOnlyList<MonitorGeometry> geometries)
        {
            var left = geometries.Min(g => (int)Math.Round(g.Physical.Left));
            var top = geometries.Min(g => (int)Math.Round(g.Physical.Top));
            var right = geometries.Max(g => (int)Math.Round(g.Physical.Right));
            var bottom = geometries.Max(g => (int)Math.Round(g.Physical.Bottom));
            _bounds = new PhysicalRect(left, top, right - left, bottom - top);
        }

        public List<RegionPickerWindow> Overlays { get; } = new();

        public bool Dragging { get; private set; }

        public PhysicalRect? Result { get; private set; }

        public event Action? Completed;

        /// <summary>The drag's current normalized rectangle, or null before the first press.</summary>
        public PhysicalRect? CurrentSelection =>
            Dragging
                ? ScreenshotSelection.Normalize(_anchorX, _anchorY, _currentX, _currentY)
                : null;

        public void Begin((int X, int Y) physical)
        {
            if (_finished || Dragging)
                return;
            (_anchorX, _anchorY) = ScreenshotSelection.ClampPoint(physical.X, physical.Y, _bounds);
            (_currentX, _currentY) = (_anchorX, _anchorY);
            Dragging = true;
            RefreshAll();
        }

        public void Update((int X, int Y) physical)
        {
            if (_finished || !Dragging)
                return;
            (_currentX, _currentY) = ScreenshotSelection.ClampPoint(physical.X, physical.Y, _bounds);
            RefreshAll();
        }

        /// <summary>Release: a drag that reached Core's minimum size is the selection; anything smaller was a slip and cancels.</summary>
        public void End()
        {
            if (_finished || !Dragging)
                return;
            var rect = CurrentSelection;
            Finish(rect is { } r && ScreenshotSelection.IsConfirmed(r) ? r : null);
        }

        public void Cancel() => Finish(null);

        private void Finish(PhysicalRect? result)
        {
            if (_finished)
                return;
            _finished = true;
            Dragging = false;
            Result = result;
            Completed?.Invoke();
        }

        private void RefreshAll()
        {
            foreach (var overlay in Overlays)
                overlay.RefreshSelection();
        }
    }
}

/// <summary>
/// The capture itself (v2.1.0): a GDI copy of the chosen physical
/// rectangle into a PNG file. CopyFromScreen speaks the screen's own
/// coordinates — physical pixels, the same space the picker returns
/// — and this process is Per-Monitor v2 aware, so no scaling stands
/// between the rectangle the user dragged and the pixels written.
/// The image is never logged, measured, or kept: the caller deletes
/// the file at the end of every path.
/// </summary>
public static class ScreenCapture
{
    public static void CapturePng(PhysicalRect rect, string path)
    {
        using var bitmap = new System.Drawing.Bitmap(rect.Width, rect.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                rect.Left, rect.Top, 0, 0, bitmap.Size,
                System.Drawing.CopyPixelOperation.SourceCopy);
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
