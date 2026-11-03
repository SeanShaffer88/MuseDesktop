using System.Globalization;

namespace MuseDesktop.Core;

// ─────────────────────── screenshot → ask (v2.1.0) ───────────────────────
//
// Grab a rectangle of the screen and start a conversation with the
// picture attached and a starter line ready to edit. The shell owns
// the pixels (the region picker, the GDI capture, the page's upload
// control); this file owns the small decisions around them, in the
// open: how the drag's corners become a rectangle, when a drag is
// too small to count, what the throwaway PNG is called, what the
// starter line says, which status line each outcome earns, and the
// temp file's deletion.
//
// Privacy shape (see DECISIONS, 2026-11-02): the captured image
// exists as a file only between the capture and the attach attempt,
// in the app's temp folder, and is deleted at the end of EVERY path
// — attached, clipboard fallback, or failed. Nothing about the image
// (its bytes, a hash, even its size) is ever logged; the statuses
// below are the whole report.

/// <summary>A rectangle in physical screen pixels — the picker's drag and the capture both live in this one space, so mixed-DPI monitors never need a second conversion.</summary>
public readonly record struct PhysicalRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

/// <summary>How a screenshot → ask attempt ended; each outcome maps to exactly one status line (see <see cref="ScreenshotAsk.StatusKey"/>).</summary>
public enum ScreenshotAskOutcome
{
    /// <summary>The picture went into the new conversation through the page's own upload control.</summary>
    Attached,

    /// <summary>The page wouldn't take the picture directly, so it is on the clipboard for the user to paste.</summary>
    ClipboardFallback,

    /// <summary>The user backed out (Esc, right-click, or a drag too small to count) — nothing was captured.</summary>
    Cancelled,

    /// <summary>The capture itself failed, or the picture could neither be attached nor copied — nothing was added.</summary>
    CaptureFailed,

    /// <summary>The new conversation's message box never appeared, so the starter line never landed.</summary>
    NoMessageBox,
}

/// <summary>
/// The picker's drag maths: the anchor corner and the current
/// corner, in physical pixels, become a normalized rectangle
/// whatever direction the drag went. A drag is only a selection
/// when it reaches <see cref="MinSelectionSize"/> pixels on BOTH
/// sides — anything smaller is a slip, treated as a cancel, the
/// same rule Windows' own snipping tool applies.
/// </summary>
public static class ScreenshotSelection
{
    /// <summary>The smallest width AND height, in physical pixels, a drag must reach to count as a selection.</summary>
    public const int MinSelectionSize = 8;

    /// <summary>The normalized rectangle between two opposite drag corners (either order, any direction).</summary>
    public static PhysicalRect Normalize(int anchorX, int anchorY, int currentX, int currentY) =>
        new(
            Left: Math.Min(anchorX, currentX),
            Top: Math.Min(anchorY, currentY),
            Width: Math.Abs(currentX - anchorX),
            Height: Math.Abs(currentY - anchorY));

    /// <summary>True when the rectangle is big enough to be a deliberate selection.</summary>
    public static bool IsConfirmed(PhysicalRect rect) =>
        rect.Width >= MinSelectionSize && rect.Height >= MinSelectionSize;

    /// <summary>Pin a point inside the virtual screen's bounds (the union of the monitors), so a drag can never run off the world's edge.</summary>
    public static (int X, int Y) ClampPoint(int x, int y, PhysicalRect bounds) =>
        (Math.Clamp(x, bounds.Left, bounds.Right),
         Math.Clamp(y, bounds.Top, bounds.Bottom));
}

/// <summary>
/// One monitor's region maths (M1): a selection dragged on ONE
/// monitor's full-screen overlay is reported in that overlay's own
/// space — DIPs, local to the monitor — because a single-monitor
/// window owns exactly one DPI scale and WPF speaks DIPs. Turning
/// that into the physical rectangle the capture takes is a pure
/// decision, so it lives here: normalize the drag (either
/// direction), scale each edge by the monitor's own factor, lay it
/// over the monitor's physical origin, and clamp to the monitor's
/// physical bounds — a drag that runs past an edge selects up to
/// the edge, never pixels from a neighbouring screen. A selection
/// that clamps to nothing returns null; whether what remains is
/// big enough to count is the caller's question, answered by
/// <see cref="ScreenshotSelection.IsConfirmed"/> on the result.
/// </summary>
public static class ScreenshotRegion
{
    /// <summary>
    /// The physical capture rectangle for a DIP selection on one
    /// monitor. <paramref name="dipSelection"/> is in the overlay's
    /// local DIP space (its top-left is the monitor's top-left);
    /// <paramref name="monitorPhysical"/> is the monitor's own
    /// rectangle in physical pixels and <paramref name="dpiScale"/>
    /// its scale factor (1.0 at 96 DPI; a non-positive or NaN
    /// factor means "unknown" and is treated as 1.0, DipMath's
    /// rule). Edges round to the nearest physical pixel — the same
    /// rounding the multi-monitor picker applies point by point.
    /// </summary>
    public static PhysicalRect? ToPhysicalRect(
        ScreenRect dipSelection, PhysicalRect monitorPhysical, double dpiScale)
    {
        var scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1.0;
        var dipLeft = Math.Min(dipSelection.Left, dipSelection.Right);
        var dipTop = Math.Min(dipSelection.Top, dipSelection.Bottom);
        var dipRight = Math.Max(dipSelection.Left, dipSelection.Right);
        var dipBottom = Math.Max(dipSelection.Top, dipSelection.Bottom);
        var left = monitorPhysical.Left + (int)Math.Round(dipLeft * scale);
        var top = monitorPhysical.Top + (int)Math.Round(dipTop * scale);
        var right = monitorPhysical.Left + (int)Math.Round(dipRight * scale);
        var bottom = monitorPhysical.Top + (int)Math.Round(dipBottom * scale);
        // Clamp to the monitor: the intersection with its bounds,
        // in physical pixels.
        left = Math.Max(left, monitorPhysical.Left);
        top = Math.Max(top, monitorPhysical.Top);
        right = Math.Min(right, monitorPhysical.Right);
        bottom = Math.Min(bottom, monitorPhysical.Bottom);
        if (right <= left || bottom <= top)
            return null;
        return new PhysicalRect(left, top, right - left, bottom - top);
    }
}

/// <summary>
/// The screenshot → ask bookkeeping that doesn't touch pixels: the
/// temp file's name and path, the starter line, the outcome → status
/// mapping, and the temp file's guaranteed deletion.
/// </summary>
public static class ScreenshotAsk
{
    /// <summary>The starter line when no localized one is available — the feature's own words, in the language the capability was specified in.</summary>
    public const string DefaultStarterLine = "What is happening in this screenshot?";

    /// <summary>
    /// The temp PNG's file name for a capture moment:
    /// <c>screenshot-ask-yyyyMMdd-HHmmss-fff.png</c> — plainly a
    /// throwaway (the name says what it was for), sortable, and
    /// unique to the millisecond so two captures can never collide.
    /// </summary>
    public static string TempFileName(DateTimeOffset now) =>
        string.Create(CultureInfo.InvariantCulture, $"screenshot-ask-{now:yyyyMMdd-HHmmss-fff}.png");

    /// <summary>The full temp path: the app's temp folder (created by the caller) plus <see cref="TempFileName"/>.</summary>
    public static string TempFilePath(string tempDirectory, DateTimeOffset now) =>
        Path.Combine(tempDirectory, TempFileName(now));

    /// <summary>
    /// The starter line that lands in the new conversation's message
    /// box, ready to edit: the localized line when there is one
    /// (trimmed — it is a sentence the user will read), otherwise
    /// <see cref="DefaultStarterLine"/>.
    /// </summary>
    public static string ComposeStarterLine(string? localizedStarter)
    {
        var clean = localizedStarter?.Trim();
        return string.IsNullOrEmpty(clean) ? DefaultStarterLine : clean;
    }

    /// <summary>The status-line string key for an outcome — one line per ending, so the status bar never claims more than happened.</summary>
    public static string StatusKey(ScreenshotAskOutcome outcome) => outcome switch
    {
        ScreenshotAskOutcome.Attached => "status.screenshot_attached",
        ScreenshotAskOutcome.ClipboardFallback => "status.screenshot_clipboard",
        ScreenshotAskOutcome.Cancelled => "status.screenshot_cancelled",
        ScreenshotAskOutcome.CaptureFailed => "status.screenshot_capture_failed",
        ScreenshotAskOutcome.NoMessageBox => "status.screenshot_no_message_box",
        _ => "status.screenshot_capture_failed",
    };

    /// <summary>
    /// Delete the temp PNG. Called at the end of every path out of
    /// the feature — attached, pasted-from-clipboard, or failed —
    /// because the conversation (or the clipboard) keeps its own
    /// copy and the file was only ever a vehicle. Never throws;
    /// returns true when the file is gone afterwards because this
    /// call removed it, false when there was nothing to remove or
    /// the removal failed (the caller logs the failure, never the
    /// file's contents — there is nothing about an image to log).
    /// </summary>
    public static bool TryDeleteTempFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
