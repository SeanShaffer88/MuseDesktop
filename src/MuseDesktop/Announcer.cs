using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// UIA live-region announcements (v0.10.0, S6 #54) + the announcement
/// center (v1.8.0, R7 #169). The main window's status bar carries two
/// invisible text blocks — one Polite, one Assertive — registered
/// here at construction. Announcing writes the text and raises
/// LiveRegionChanged, so Narrator/NVDA speak download completions,
/// download failures, and update notices instead of the user having
/// to poll the status bar visually.
///
/// Since R7 every announcement has a KIND (downloads, updates,
/// timers, focus — plus "general" for one-off confirmations of an
/// action the user just took). The kinded, automatic announcements
/// pass through the per-type switches in Settings: a type the user
/// turned off is not spoken, but it IS recorded — every announcement,
/// spoken or not, lands in the bounded local history that Diagnostics
/// shows, so nothing the app "said" is ever lost silently.
/// Announcements are best-effort: a failure here must never break the
/// feature being announced.
/// </summary>
public static class Announcer
{
    private static TextBlock? _polite;
    private static TextBlock? _assertive;
    private static Func<AppSettings>? _settings;
    private static AnnouncementHistory? _history;

    public static void Register(TextBlock polite, TextBlock assertive)
    {
        _polite = polite;
        _assertive = assertive;
    }

    /// <summary>
    /// Wire the center (R7 #169): where to read the per-type switches
    /// (live settings — a switch change takes effect on the next
    /// announcement, no restart) and where the history lives. Called
    /// by the main window once at construction.
    /// </summary>
    public static void Configure(Func<AppSettings> settings, AnnouncementHistory history)
    {
        _settings = settings;
        _history = history;
    }

    /// <summary>
    /// An automatic announcement of a kind (R7 #169): recorded in the
    /// history always; spoken only when its type is switched on.
    /// </summary>
    public static void Kind(AnnouncementKind kind, string text, bool assertive = false)
    {
        var enabled = kind == AnnouncementKind.General
                      || _settings is null
                      || AnnouncementSwitches.IsEnabled(_settings().AnnouncementKindEnabled, kind);
        Record(kind, text, spoken: enabled);
        if (enabled)
            Announce(assertive ? _assertive : _polite, text);
    }

    /// <summary>Non-urgent one-off confirmation (support bundle saved, a test line): kind General — always spoken, always recorded.</summary>
    public static void Polite(string text) => Kind(AnnouncementKind.General, text);

    /// <summary>Urgent one-off announcement: kind General — always spoken, always recorded.</summary>
    public static void Assertive(string text) => Kind(AnnouncementKind.General, text, assertive: true);

    private static void Record(AnnouncementKind kind, string text, bool spoken)
    {
        if (_history is null)
            return;
        try
        {
            _history.Record(kind, text, spoken, DateTimeOffset.UtcNow);
            AnnouncementHistoryStore.Save(AppPaths.AnnouncementHistoryPath, _history);
        }
        catch { /* the history must never break the app */ }
    }

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
