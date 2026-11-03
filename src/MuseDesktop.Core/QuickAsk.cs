namespace MuseDesktop.Core;

// ─────────────────────── quick ask (post-v2.1.0) ───────────────────────
//
// A small Muse window docked at the bottom-right corner of the
// primary monitor's work area, for one quick question without
// opening (or disturbing) the main window. The shell owns the
// window — one WebView2 on the ACTIVE profile's environment,
// opened on a fresh conversation; the page's own composer is the
// input. This file owns the small decisions around it, in the
// open: where the card docks, what the "Continue in main window"
// hand-off may navigate to, and how its global hotkey resolves.
//
// Privacy shape (see DECISIONS, 2026-11-02): the popup is a PLAIN
// page, like the compare panes — no bridge is installed in it, so
// there are no drafts, chips, or page probes, and the shell never
// reads the conversation back. The one thing that crosses is the
// hand-off: the page's current address, which the user hands to
// the main window by pressing the button; it is classified here,
// in-app or not at all, and only ever logged redacted.

/// <summary>The quick-ask decisions that don't touch a window: dock placement and the hand-off target.</summary>
public static class QuickAsk
{
    /// <summary>The card's width in DIPs on a roomy screen — wide enough for a comfortable reading column, small enough to stay a corner card.</summary>
    public const double CardWidthDip = 460;

    /// <summary>The card's height in DIPs on a roomy screen — most of a typical work area, so a long answer has room to be read where it lands.</summary>
    public const double CardHeightDip = 640;

    /// <summary>The gap in DIPs between the card and the work area's right and bottom edges.</summary>
    public const double DockMarginDip = 12;

    /// <summary>
    /// Where the card docks inside a work area (in DIPs — the
    /// shell feeds the primary monitor's work area from
    /// MonitorRects, which converts per-monitor, so the card is
    /// already DPI-scaled when it gets here). The card takes its
    /// fixed size unless the work area can't hold it plus a margin
    /// on both sides, in which case it shrinks to fit rather than
    /// spilling off-screen; the right and bottom margins are exact
    /// whenever the area is at least the card's size. A degenerate
    /// (zero/negative) area yields a zero-size rect at its corner —
    /// the function is total and never returns negative sizes.
    /// Placement is deliberately NOT persisted: the card docks
    /// fresh at every summon (see DECISIONS).
    /// </summary>
    public static ScreenRect DockPlacement(ScreenRect workAreaDip)
    {
        var width = Math.Max(0, Math.Min(CardWidthDip, workAreaDip.Width - 2 * DockMarginDip));
        var height = Math.Max(0, Math.Min(CardHeightDip, workAreaDip.Height - 2 * DockMarginDip));
        return new ScreenRect(
            workAreaDip.Right - DockMarginDip - width,
            workAreaDip.Bottom - DockMarginDip - height,
            width,
            height);
    }

    /// <summary>
    /// The address "Continue in main window" navigates to: the
    /// popup page's current address when it is an https page on
    /// one of the in-app hosts (Muse itself or a sign-in
    /// provider — the only places the popup's conversation can
    /// legitimately be), and the Muse home page otherwise. The
    /// bar is deliberately narrower than the main window's
    /// top-level navigation policy (any https the USER opens is
    /// allowed there): this address is read off a page, not typed
    /// by the user, so a popup that somehow wandered off Muse
    /// hands over Home rather than smuggling an outside address
    /// into the main window.
    /// </summary>
    public static string ContinueTarget(string? currentUrl) =>
        Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && UrlPolicy.IsInAppHost(uri.Host)
            ? currentUrl!
            : UrlPolicy.HomeUrl;
}

/// <summary>
/// The quick-ask global hotkey: a third RegisterHotKey beside the
/// show/hide (S11 #96) and quick-note (R5 #148) ones, resolved
/// under the same rules as theirs — never equal to an in-app
/// gesture — plus never equal to either of the OTHER two global
/// gestures, or one press would try to do two things. The gesture
/// is FIXED (Ctrl+Shift+Space: free in the in-app catalog, free in
/// Windows and the shell, and not taken by either existing global):
/// unlike the other two it has no Settings capture UI, so the
/// resolution below is also the conflict report — a user who
/// remaps an in-app action onto Ctrl+Shift+Space, or moves another
/// global onto it, blocks this registration and Settings shows why.
/// The feature toggle (<see cref="AppSettings.QuickAskEnabled"/>)
/// is the enable: routes and registration all follow it.
/// </summary>
public static class QuickAskHotkey
{
    /// <summary>The factory (and only) gesture: Ctrl+Shift+Space.</summary>
    public const string DefaultGestureText = "Ctrl+Shift+Space";

    public static ShortcutGesture DefaultGesture =>
        ShortcutGesture.TryParse(DefaultGestureText, out var gesture)
            ? gesture
            : throw new InvalidOperationException("The quick-ask gesture doesn't parse.");

    /// <summary>Resolve the saved state into a registration decision, mirroring <see cref="QuickNoteHotkey.Resolve"/> plus the second-global equality rule.</summary>
    public static HotkeyResolution Resolve(
        bool enabled,
        IReadOnlyDictionary<string, string>? inAppOverrides,
        string? showHideGestureText,
        bool quickNoteEnabled,
        string? quickNoteGestureText)
    {
        var gesture = DefaultGesture;
        if (!enabled)
            return new HotkeyResolution(HotkeyResolutionKind.Disabled, gesture, null);
        if (gesture == EffectiveGlobal(showHideGestureText, GlobalHotkey.DefaultGesture))
            return new HotkeyResolution(HotkeyResolutionKind.BlockedByConflict, gesture,
                "the global show/hide hotkey");
        if (quickNoteEnabled
            && gesture == EffectiveGlobal(quickNoteGestureText, QuickNoteHotkey.DefaultGesture))
            return new HotkeyResolution(HotkeyResolutionKind.BlockedByConflict, gesture,
                "the quick-note hotkey");
        var owner = GlobalHotkey.ConflictOwner(gesture, inAppOverrides);
        return owner is null
            ? new HotkeyResolution(HotkeyResolutionKind.Ready, gesture, null)
            : new HotkeyResolution(HotkeyResolutionKind.BlockedByConflict, gesture, owner);
    }

    /// <summary>A stored global gesture's effective form: the stored text when it parses with Ctrl/Alt, else that hotkey's default — the same repair <see cref="GlobalHotkey.Resolve"/> applies.</summary>
    private static ShortcutGesture EffectiveGlobal(string? storedText, ShortcutGesture fallback) =>
        ShortcutGesture.TryParse(storedText, out var parsed)
        && (parsed.Modifiers.HasFlag(GestureModifiers.Control)
            || parsed.Modifiers.HasFlag(GestureModifiers.Alt))
            ? parsed
            : fallback;
}
