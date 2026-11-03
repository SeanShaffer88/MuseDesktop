namespace MuseDesktop.Core;

// ─────────────────────── compare mode (v2.1.0) ───────────────────────
//
// Send the same prompt to two profiles at once and read the answers
// side by side. The shell owns the window — two WebView2 panes, each
// on its own profile's environment, over one shared prompt box —
// and the one-shot script that sets the composer and sends. This
// file owns the small decisions around them, in the open: which
// profiles the two panes start on, what the send script's report
// means, which status line each outcome earns, and when the prompt
// box may clear.
//
// Privacy shape (see DECISIONS, 2026-11-02): the prompt crosses into
// each page exactly once, at the user's explicit "Send to both"
// click — that click IS the send, unlike every other bridge insert,
// which lands unsent. The prompt is never logged and never stored;
// the statuses below are the whole report. The panes are plain
// pages: no bridge, no drafts, no page health in v1.

/// <summary>How one pane's half of a compare send ended; each outcome maps to exactly one status line (see <see cref="CompareMode.StatusKey"/>).</summary>
public enum CompareSendOutcome
{
    /// <summary>The prompt landed in the pane's composer and the send went through (the page's send control, or the Enter fallback).</summary>
    Sent,

    /// <summary>The page had loaded but no message box could be found on it — the prompt did not go anywhere on this side.</summary>
    NoMessageBox,

    /// <summary>The pane was still loading its fresh conversation when the send was tried — nothing was sent on this side; trying again once it settles is the remedy.</summary>
    StillLoading,
}

/// <summary>The compare mode decisions that don't touch a window: pane defaults, send-outcome reading, status mapping, and the prompt-clear rule.</summary>
public static class CompareMode
{
    /// <summary>
    /// The profiles the two panes open on. The left pane starts on
    /// the active profile (the first visible profile when the active
    /// one isn't in the list); the right pane starts on the FIRST
    /// OTHER visible profile, so a two-profile user gets the
    /// comparison the feature promises without touching a picker.
    /// With only one visible profile both panes share it — two fresh
    /// conversations in the same sign-in still give two takes. An
    /// empty list (defensive; settings always carry at least the
    /// default profile) yields the default profile twice.
    /// </summary>
    public static (string LeftProfileId, string RightProfileId) DefaultPaneProfiles(
        IReadOnlyList<AccountProfile> visibleProfiles, string? activeProfileId)
    {
        if (visibleProfiles is null || visibleProfiles.Count == 0)
            return (Profiles.DefaultId, Profiles.DefaultId);

        var left = visibleProfiles.FirstOrDefault(p => p.Id == activeProfileId)
            ?? visibleProfiles[0];
        var right = visibleProfiles.FirstOrDefault(p => p.Id != left.Id)
            ?? left;
        return (left.Id, right.Id);
    }

    /// <summary>
    /// Read the send script's report for one pane. The script's own
    /// vocabulary is exactly "sent" / anything else (it returns
    /// "no-composer" when the box isn't there and "error" when the
    /// page refused the run); a non-"sent" report on a pane that is
    /// still loading means the composer simply hasn't arrived yet,
    /// which earns the try-again line instead of the not-found one.
    /// </summary>
    public static CompareSendOutcome OutcomeFor(string? scriptToken, bool pageStillLoading) =>
        string.Equals(scriptToken, "sent", StringComparison.Ordinal)
            ? CompareSendOutcome.Sent
            : pageStillLoading
                ? CompareSendOutcome.StillLoading
                : CompareSendOutcome.NoMessageBox;

    /// <summary>The status-line string key for a pane outcome — one line per ending, so a pane's chip never claims more than happened on that side.</summary>
    public static string StatusKey(CompareSendOutcome outcome) => outcome switch
    {
        CompareSendOutcome.Sent => "compare.status_sent",
        CompareSendOutcome.NoMessageBox => "compare.status_no_message_box",
        CompareSendOutcome.StillLoading => "compare.status_still_loading",
        _ => "compare.status_no_message_box",
    };

    /// <summary>
    /// True only when BOTH panes sent. This is the prompt box's
    /// clear rule: the text is the user's only copy of the question,
    /// so it survives any half-failure and clears only on a clean
    /// double send.
    /// </summary>
    public static bool BothSent(CompareSendOutcome left, CompareSendOutcome right) =>
        left == CompareSendOutcome.Sent && right == CompareSendOutcome.Sent;
}
