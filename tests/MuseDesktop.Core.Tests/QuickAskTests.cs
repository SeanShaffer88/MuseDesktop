using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Quick ask (post-v2.1.0) ─────────────────────
//
// The shell owns the corner-card window and its WebView2; these
// tests pin the decisions around it: where the card docks (fixed
// DIP size, exact right/bottom margins, shrink-to-fit on a small
// work area, never a negative size), what "Continue in main
// window" may navigate to (the popup's in-app address, else
// Home), the hotkey's resolution (the fixed Ctrl+Shift+Space can
// be blocked by a remapped in-app action or by either of the
// other global hotkeys — never silently substituted), the
// registration id's uniqueness, and the settings plumbing (the
// one switch defaults on, clones, and resets).

public class QuickAskTests
{
    // ── dock placement ───────────────────────────────────────────

    [Fact]
    public void DockPlacement_RoomyWorkArea_FixedCardAtTheCorner()
    {
        var placement = QuickAsk.DockPlacement(new ScreenRect(0, 0, 1920, 1040));
        Assert.Equal(QuickAsk.CardWidthDip, placement.Width);
        Assert.Equal(QuickAsk.CardHeightDip, placement.Height);
        // The right and bottom margins are exact.
        Assert.Equal(1920 - QuickAsk.DockMarginDip, placement.Right);
        Assert.Equal(1040 - QuickAsk.DockMarginDip, placement.Bottom);
    }

    [Fact]
    public void DockPlacement_OffsetWorkArea_DocksInsideIt()
    {
        // A work area not starting at the origin (taskbar on the
        // left / a secondary-position primary): the corner that
        // counts is the area's own bottom-right.
        var placement = QuickAsk.DockPlacement(new ScreenRect(64, 30, 1600, 900));
        Assert.Equal(64 + 1600 - QuickAsk.DockMarginDip, placement.Right);
        Assert.Equal(30 + 900 - QuickAsk.DockMarginDip, placement.Bottom);
        Assert.True(placement.Left >= 64 && placement.Top >= 30);
    }

    [Fact]
    public void DockPlacement_SmallWorkArea_ShrinksToFit()
    {
        // 500×400: the width still fits at full size (460 + margins
        // = 484 ≤ 500); the height must shrink to the area minus
        // both margins.
        var placement = QuickAsk.DockPlacement(new ScreenRect(0, 0, 500, 400));
        Assert.Equal(QuickAsk.CardWidthDip, placement.Width);
        Assert.Equal(400 - 2 * QuickAsk.DockMarginDip, placement.Height);
        Assert.Equal(500 - QuickAsk.DockMarginDip, placement.Right);
        Assert.Equal(400 - QuickAsk.DockMarginDip, placement.Bottom);
        // A narrower area shrinks the width too — and nothing ever
        // spills past an edge.
        var narrow = QuickAsk.DockPlacement(new ScreenRect(0, 0, 300, 900));
        Assert.Equal(300 - 2 * QuickAsk.DockMarginDip, narrow.Width);
        Assert.True(narrow.Left >= 0 && narrow.Top >= 0);
        Assert.True(narrow.Right <= 300 && narrow.Bottom <= 900);
    }

    [Fact]
    public void DockPlacement_DegenerateWorkArea_NeverNegative()
    {
        var placement = QuickAsk.DockPlacement(new ScreenRect(0, 0, 10, 10));
        Assert.True(placement.Width >= 0 && placement.Height >= 0);
    }

    // ── the hand-off target ──────────────────────────────────────

    [Fact]
    public void ContinueTarget_InAppConversation_PassesThrough()
    {
        const string conversation = "https://muse.ai/chat/abc123";
        Assert.Equal(conversation, QuickAsk.ContinueTarget(conversation));
        Assert.Equal(UrlPolicy.HomeUrl, QuickAsk.ContinueTarget(UrlPolicy.HomeUrl));
        // A sign-in provider page mid-login is an in-app host too.
        const string signIn = "https://accounts.google.com/signin";
        Assert.Equal(signIn, QuickAsk.ContinueTarget(signIn));
    }

    [Fact]
    public void ContinueTarget_AnythingElse_FallsBackToHome()
    {
        Assert.Equal(UrlPolicy.HomeUrl, QuickAsk.ContinueTarget(null));
        Assert.Equal(UrlPolicy.HomeUrl, QuickAsk.ContinueTarget(""));
        Assert.Equal(UrlPolicy.HomeUrl, QuickAsk.ContinueTarget("https://example.com/elsewhere"));
        Assert.Equal(UrlPolicy.HomeUrl, QuickAsk.ContinueTarget("http://muse.ai/insecure"));
        // A look-alike host never passes for Muse.
        Assert.Equal(UrlPolicy.HomeUrl, QuickAsk.ContinueTarget("https://muse.ai.evil.com/chat/abc123"));
    }

    // ── the hotkey ───────────────────────────────────────────────

    [Fact]
    public void HotkeyRegistrationIds_AreAllDistinct()
    {
        var ids = new[]
        {
            HotkeyNative.RegistrationId,
            HotkeyNative.RegistrationIdQuickNote,
            HotkeyNative.RegistrationIdQuickAsk,
        };
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void QuickAskHotkey_FixedGesture_ParsesAndMapsToSpace()
    {
        Assert.Equal("Ctrl+Shift+Space", QuickAskHotkey.DefaultGestureText);
        Assert.True(HotkeyNative.TryVirtualKey(QuickAskHotkey.DefaultGesture.Key, out var vk));
        Assert.Equal(0x20u, vk);
        var mods = HotkeyNative.ModifiersFor(QuickAskHotkey.DefaultGesture);
        Assert.True((mods & HotkeyNative.ModControl) != 0);
        Assert.True((mods & HotkeyNative.ModShift) != 0);
        Assert.True((mods & HotkeyNative.ModNoRepeat) != 0);
    }

    [Fact]
    public void QuickAskHotkey_Off_IsDisabled()
    {
        var off = QuickAskHotkey.Resolve(false, null, null, false, null);
        Assert.Equal(HotkeyResolutionKind.Disabled, off.Kind);
        Assert.Equal(QuickAskHotkey.DefaultGesture, off.Gesture);
    }

    [Fact]
    public void QuickAskHotkey_OnAndFree_IsReady()
    {
        var ready = QuickAskHotkey.Resolve(true, null, "Ctrl+Alt+M", true, "Ctrl+Alt+N");
        Assert.Equal(HotkeyResolutionKind.Ready, ready.Kind);
        Assert.Equal(QuickAskHotkey.DefaultGesture, ready.Gesture);
        Assert.Null(ready.ConflictOwner);
    }

    [Fact]
    public void QuickAskHotkey_RemappedInAppActionOntoTheGesture_Blocks()
    {
        var overrides = new Dictionary<string, string>
        {
            [ShortcutCatalog.DownloadsId] = "Ctrl+Shift+Space",
        };
        var blocked = QuickAskHotkey.Resolve(true, overrides, null, false, null);
        Assert.Equal(HotkeyResolutionKind.BlockedByConflict, blocked.Kind);
        Assert.Equal("Open Downloads", blocked.ConflictOwner);
    }

    [Fact]
    public void QuickAskHotkey_ShowHideMovedOntoTheGesture_Blocks()
    {
        var blocked = QuickAskHotkey.Resolve(true, null, "Ctrl+Shift+Space", false, null);
        Assert.Equal(HotkeyResolutionKind.BlockedByConflict, blocked.Kind);
        Assert.Contains("show/hide", blocked.ConflictOwner);
    }

    [Fact]
    public void QuickAskHotkey_QuickNoteMovedOntoTheGesture_BlocksOnlyWhileNoteIsOn()
    {
        var blocked = QuickAskHotkey.Resolve(true, null, null,
            quickNoteEnabled: true, quickNoteGestureText: "Ctrl+Shift+Space");
        Assert.Equal(HotkeyResolutionKind.BlockedByConflict, blocked.Kind);
        Assert.Contains("quick-note", blocked.ConflictOwner);
        // The same stored gesture with the note hotkey OFF owns
        // nothing — the registration is free to proceed.
        var ready = QuickAskHotkey.Resolve(true, null, null,
            quickNoteEnabled: false, quickNoteGestureText: "Ctrl+Shift+Space");
        Assert.Equal(HotkeyResolutionKind.Ready, ready.Kind);
    }

    // ── settings plumbing ────────────────────────────────────────

    [Fact]
    public void QuickAskEnabled_DefaultsOn_ClonesAndResets()
    {
        var settings = new AppSettings();
        Assert.True(settings.QuickAskEnabled);

        settings.QuickAskEnabled = false;
        var clone = settings.Clone();
        Assert.False(clone.QuickAskEnabled);

        SettingsReset.ApplyDefaults(clone);
        Assert.True(clone.QuickAskEnabled);
    }

    [Fact]
    public void Palette_OffersQuickAsk()
    {
        var action = Assert.Single(CommandRegistry.BuiltinActions,
            a => a.Id == "action.quick-ask");
        Assert.Equal("Quick ask…", action.Title);
    }
}

public class QuickAskMonitorChoiceTests
{
    // Primary first, then a monitor to its right, then one above
    // it at negative Y — the mixed layout the choice exists for.
    private static readonly ScreenRect[] Monitors =
    {
        new(0, 0, 1920, 1080),
        new(1920, 0, 2560, 1440),
        new(0, -1080, 1920, 1080),
    };

    [Fact]
    public void MonitorIndexAt_PointInThePrimary_IsZero()
    {
        Assert.Equal(0, QuickAsk.MonitorIndexAt(Monitors, 100, 100));
    }

    [Fact]
    public void MonitorIndexAt_PointOnTheSecondMonitor_IsItsIndex()
    {
        Assert.Equal(1, QuickAsk.MonitorIndexAt(Monitors, 2500, 700));
    }

    [Fact]
    public void MonitorIndexAt_NegativeCoordinates_FindTheirMonitor()
    {
        Assert.Equal(2, QuickAsk.MonitorIndexAt(Monitors, 500, -500));
    }

    [Fact]
    public void MonitorIndexAt_SharedEdge_BelongsToTheMonitorItStarts()
    {
        // The primary's right edge (x = 1920) is the second
        // monitor's left edge: the point is the second monitor's.
        Assert.Equal(1, QuickAsk.MonitorIndexAt(Monitors, 1920, 10));
        Assert.Equal(0, QuickAsk.MonitorIndexAt(Monitors, 1919, 10));
    }

    [Fact]
    public void MonitorIndexAt_PointInNoMonitor_IsMinusOne()
    {
        Assert.Equal(-1, QuickAsk.MonitorIndexAt(Monitors, 5000, 5000));
        Assert.Equal(-1, QuickAsk.MonitorIndexAt(Array.Empty<ScreenRect>(), 0, 0));
    }

    [Fact]
    public void ContinueRefusalStatusKey_EachReasonHasItsOwnLine()
    {
        Assert.Equal("quickask.continue_locked",
            QuickAsk.ContinueRefusalStatusKey(QuickAskContinueRefusal.Locked));
        Assert.Equal("quickask.continue_not_ready",
            QuickAsk.ContinueRefusalStatusKey(QuickAskContinueRefusal.PageNotReady));
    }
}
