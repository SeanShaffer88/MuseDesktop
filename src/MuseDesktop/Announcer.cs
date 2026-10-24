using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace MuseDesktop;

/// <summary>
/// UIA live-region announcements (v0.10.0, S6 #54). The main window's
/// status bar carries two invisible text blocks — one Polite, one
/// Assertive — registered here at construction. Announcing writes the
/// text and raises LiveRegionChanged, so Narrator/NVDA speak download
/// completions, download failures, and update notices instead of the
/// user having to poll the status bar visually. The announcement texts
/// live in Core (<see cref="MuseDesktop.Core.AnnouncementText"/>) and
/// mirror the status-bar wording. Announcements are best-effort: a
/// failure here must never break the feature being announced.
/// </summary>
public static class Announcer
{
    private static TextBlock? _polite;
    private static TextBlock? _assertive;

    public static void Register(TextBlock polite, TextBlock assertive)
    {
        _polite = polite;
        _assertive = assertive;
    }

    /// <summary>Non-urgent news (download complete, update available): spoken when the screen reader is idle.</summary>
    public static void Polite(string text) => Announce(_polite, text);

    /// <summary>News that should interrupt (a download failed).</summary>
    public static void Assertive(string text) => Announce(_assertive, text);

    private static void Announce(TextBlock? region, string text)
    {
        if (region is null)
            return;
        try
        {
            // Clear first so a repeat of the identical message (two
            // downloads of the same name in a row) still registers as a
            // change worth speaking.
            region.Text = "";
            region.Text = text;
            var peer = UIElementAutomationPeer.FromElement(region)
                       ?? UIElementAutomationPeer.CreatePeerForElement(region);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch { /* announcements must never break the app */ }
    }
}
