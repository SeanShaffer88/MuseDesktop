using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// Painting for the tray icon (S4 #31/#37): the WHAT — profile dot,
/// attention badge, badge text — is decided in Core
/// (<see cref="TrayIconPlanner"/>); this class owns the pixels.
/// Geometry: profile dot bottom-left, attention badge top-right on a
/// 32px canvas — they never overlap, so both can show at once.
/// Pure drawing; every failure is the caller's cosmetic problem.
/// </summary>
public static class TrayIconRenderer
{
    private const int Canvas = 32;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// A tray icon composed from the base icon plus the planned
    /// overlays. Returns a CLONE owning its own handle, so the caller
    /// can dispose it freely; the intermediate handle is destroyed.
    /// </summary>
    public static Icon Compose(Icon baseIcon, TrayIconPlan plan, string? profileColorHex)
    {
        using var bmp = new Bitmap(Canvas, Canvas);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawIcon(baseIcon, new Rectangle(0, 0, Canvas, Canvas));

            if (plan.ShowProfileDot && profileColorHex is not null)
            {
                // Bottom-left dot with a dark ring so it reads on any base icon.
                using var ring = new SolidBrush(Color.FromArgb(0x0B, 0x0B, 0x0D));
                g.FillEllipse(ring, 0, 19, 13, 13);
                using var dot = new SolidBrush(ColorTranslator.FromHtml(profileColorHex));
                g.FillEllipse(dot, 2, 21, 9, 9);
            }

            if (plan.ShowBadge)
            {
                // Top-right badge in the family red, white bold text.
                using var ring = new SolidBrush(Color.FromArgb(0x0B, 0x0B, 0x0D));
                g.FillEllipse(ring, 15, 0, 17, 17);
                using var badge = new SolidBrush(Color.FromArgb(0xE5, 0x09, 0x14));
                g.FillEllipse(badge, 16, 1, 15, 15);
                if (plan.BadgeText is { } text)
                {
                    using var font = new Font("Segoe UI", text.Length > 1 ? 7.5f : 9f,
                        System.Drawing.FontStyle.Bold, GraphicsUnit.Point);
                    using var fg = new SolidBrush(Color.White);
                    var format = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center,
                    };
                    g.DrawString(text, font, fg, new RectangleF(16, 0.5f, 15, 15), format);
                }
            }
        }

        var handle = bmp.GetHicon();
        try
        {
            using var fromHandle = Icon.FromHandle(handle);
            return (Icon)fromHandle.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>
    /// The taskbar overlay image (S4 #31): the same badge on a
    /// transparent 16px canvas, as a WPF image for TaskbarItemInfo.
    /// </summary>
    public static BitmapSource BadgeOverlay(string? badgeText)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var badge = new SolidBrush(Color.FromArgb(0xE5, 0x09, 0x14));
            g.FillEllipse(badge, 1, 1, 14, 14);
            if (badgeText is { } text)
            {
                using var font = new Font("Segoe UI", text.Length > 1 ? 6.5f : 8f,
                    System.Drawing.FontStyle.Bold, GraphicsUnit.Point);
                using var fg = new SolidBrush(Color.White);
                var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                g.DrawString(text, font, fg, new RectangleF(1, 0.5f, 14, 14), format);
            }
        }
        var hbitmap = bmp.GetHbitmap();
        try
        {
            return Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(hbitmap);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
