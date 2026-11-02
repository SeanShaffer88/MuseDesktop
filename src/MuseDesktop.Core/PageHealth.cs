using System.Text.Json;

namespace MuseDesktop.Core;

// ─────────────────────── page health (Diagnostics) ───────────────────────
//
// Several features work through the hosted Muse page's structure:
// the conversation tools (message bookmarks, composer drafts,
// conversation export — R5B) find messages and the composer by
// structural markers, and the approval watcher (R7C/R7C2) recognises
// approval cards by its two button rules. When the page changes
// shape, those features quietly stop working — by design they never
// get in the page's way — and the v1.8.3 approval-beep failure
// showed what that silence costs: nothing anywhere said the detector
// no longer matched the real page.
//
// The Page health check in Diagnostics exists to make that breakage
// visible. The shell asks the page bridge for a probe (PageScripts'
// __mdProbePageHealth), which runs the SAME detectors the features
// themselves use — the bridge's message/composer finders and its
// approval-signal detector — and reports presence only. This file
// owns the probe's shape, the verdicts, and their wording.
//
// Privacy construction, same as the bridge itself: the probe carries
// booleans and (for an approval card) WHICH watcher rule matched —
// a rule name from a fixed set. No message text, composer text, card
// text, or page content of any kind crosses into the report, and
// nothing here is ever logged with more than a finding's status.

/// <summary>One feature's page-health verdict.</summary>
public enum PageHealthStatus
{
    /// <summary>The feature's markers were found on the loaded page — the feature can work.</summary>
    Recognised,

    /// <summary>The feature's markers were expected on this page but not found — the page's shape may have changed, or the bridge is not answering.</summary>
    NotRecognised,

    /// <summary>The feature has nothing to attach to on this page (no conversation open), so there is nothing to judge.</summary>
    NotApplicable,

    /// <summary>Approval alerts only: no approval card is showing right now, and the watcher answered normally — the expected state whenever no card is waiting.</summary>
    NoCardShowing,
}

/// <summary>The page-based features the health check covers.</summary>
public enum PageHealthFeature
{
    MessageBookmarks,
    Drafts,
    ConversationExport,
    ApprovalAlerts,
}

/// <summary>
/// The probe's raw result: presence/absence ONLY — whether messages
/// are showing, whether the bookmark buttons the bridge adds are in
/// place, whether the composer was found, and whether an approval
/// card is showing (with the watcher rule that recognised it).
/// Produced by the bridge script, parsed strictly by
/// <see cref="PageHealth.TryParseProbe"/>: like everything the page
/// posts, it is untrusted input.
/// </summary>
public sealed record PageProbeResult(
    bool MessagesPresent,
    bool BookmarkButtonsPresent,
    bool ComposerPresent,
    bool ApprovalCardPresent,
    ApprovalDetectionSignal ApprovalSignal)
{
    /// <summary>The all-absent probe (a page with nothing the features use).</summary>
    public static PageProbeResult None { get; } =
        new(false, false, false, false, ApprovalDetectionSignal.Unknown);
}

/// <summary>One feature's verdict: what was found, in plain words, and what to do about it when something is missing (empty when there is nothing to do).</summary>
public sealed record PageHealthFinding(
    PageHealthFeature Feature,
    PageHealthStatus Status,
    string Explanation,
    string Advice);

/// <summary>
/// The page-health model: parse the bridge's probe, turn it into
/// per-feature findings, and render the findings as the plain lines
/// the Diagnostics window lists (and a support bundle can carry).
/// </summary>
public static class PageHealth
{
    /// <summary>
    /// Parse the probe script's JSON. Strict like the bridge parser
    /// (<see cref="PageBridge"/>): an object whose known fields have
    /// the expected types — a flag counts only when it is literally
    /// JSON true, and the approval signal maps through the same
    /// fixed vocabulary as the bridge's appearance reports, anything
    /// else becoming <see cref="ApprovalDetectionSignal.Unknown"/>
    /// (and no card). Garbage, a non-object, or null yields null,
    /// which <see cref="Evaluate"/> reads as "the bridge did not
    /// answer".
    /// </summary>
    public static PageProbeResult? TryParseProbe(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            static bool Flag(JsonElement root, string name) =>
                root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

            var signal = ApprovalDetectionSignal.Unknown;
            var cardPresent = false;
            if (root.TryGetProperty("approval", out var approvalEl)
                && approvalEl.ValueKind == JsonValueKind.String)
            {
                switch (approvalEl.GetString())
                {
                    case "allow-prefix":
                        signal = ApprovalDetectionSignal.AllowPrefix;
                        cardPresent = true;
                        break;
                    case "pair":
                        signal = ApprovalDetectionSignal.ButtonPair;
                        cardPresent = true;
                        break;
                }
            }
            return new PageProbeResult(
                Flag(root, "messages"),
                Flag(root, "bookmarkButtons"),
                Flag(root, "composer"),
                cardPresent,
                signal);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The verdicts for one probe, always in the same order:
    /// bookmarks, drafts, export, approval alerts. A null probe
    /// means the bridge script did not answer at all — every feature
    /// is Not recognised, because none of them can see the page.
    /// </summary>
    public static IReadOnlyList<PageHealthFinding> Evaluate(PageProbeResult? probe)
    {
        if (probe is null)
        {
            const string advice =
                "Reload the page and run the check again. If this keeps happening, the page's shape may have changed and an update may be needed.";
            return new[]
            {
                new PageHealthFinding(PageHealthFeature.MessageBookmarks, PageHealthStatus.NotRecognised,
                    "The page bridge did not answer, so the page's messages could not be checked.", advice),
                new PageHealthFinding(PageHealthFeature.Drafts, PageHealthStatus.NotRecognised,
                    "The page bridge did not answer, so the message box could not be checked.", advice),
                new PageHealthFinding(PageHealthFeature.ConversationExport, PageHealthStatus.NotRecognised,
                    "The page bridge did not answer, so the conversation could not be checked.", advice),
                new PageHealthFinding(PageHealthFeature.ApprovalAlerts, PageHealthStatus.NotRecognised,
                    "The page bridge did not answer, so approval cards cannot be watched.", advice),
            };
        }

        var findings = new List<PageHealthFinding>(4);

        // Message bookmarks (#202): the bridge's message finder plus
        // the buttons its scan adds to each message.
        if (!probe.MessagesPresent)
            findings.Add(new(PageHealthFeature.MessageBookmarks, PageHealthStatus.NotApplicable,
                "No messages are showing on this page, so there is nothing for the bookmark buttons to attach to.",
                "Open a conversation and run the check again."));
        else if (probe.BookmarkButtonsPresent)
            findings.Add(new(PageHealthFeature.MessageBookmarks, PageHealthStatus.Recognised,
                "Messages are showing and their bookmark buttons are in place.", ""));
        else
            findings.Add(new(PageHealthFeature.MessageBookmarks, PageHealthStatus.NotRecognised,
                "Messages are showing, but their bookmark buttons are missing — the page's shape may have changed.",
                "Bookmarks may not appear until an update catches up. Reloading the page is worth one try first."));

        // Composer drafts (#204): the bridge's composer finder.
        if (probe.ComposerPresent)
            findings.Add(new(PageHealthFeature.Drafts, PageHealthStatus.Recognised,
                "The message box was found, so drafts can be captured and restored as you type.", ""));
        else
            findings.Add(new(PageHealthFeature.Drafts, PageHealthStatus.NotRecognised,
                "The message box was not found — the page's shape may have changed.",
                "Drafts may not be saved or restored until an update catches up."));

        // Conversation export (#203): the same message finder the
        // extraction runs over. The probe counts markers only — it
        // never reads the messages themselves.
        if (probe.MessagesPresent)
            findings.Add(new(PageHealthFeature.ConversationExport, PageHealthStatus.Recognised,
                "Messages are showing, so the conversation can be read for export.", ""));
        else
            findings.Add(new(PageHealthFeature.ConversationExport, PageHealthStatus.NotApplicable,
                "No messages are showing on this page, so there is nothing to export.",
                "Open a conversation and run the check again."));

        // Approval alerts (R7C/R7C2): the watcher's own detector.
        // No card showing is NOT a failure — the watcher only has
        // something to report while a card waits.
        if (probe.ApprovalCardPresent)
        {
            var rule = probe.ApprovalSignal switch
            {
                ApprovalDetectionSignal.AllowPrefix => "the allow-button rule",
                ApprovalDetectionSignal.ButtonPair => "the approve/deny button-pair rule",
                _ => "the watcher",
            };
            findings.Add(new(PageHealthFeature.ApprovalAlerts, PageHealthStatus.Recognised,
                $"An approval card is showing and {rule} recognised it, so the beep and toast alerts can fire for it.", ""));
        }
        else
            findings.Add(new(PageHealthFeature.ApprovalAlerts, PageHealthStatus.NoCardShowing,
                "No approval card is showing right now, and the watcher answered normally — it is watching.",
                "Nothing to do: this is the expected result whenever no card is waiting."));

        return findings;
    }

    /// <summary>The feature's display name, as the findings list it.</summary>
    public static string FeatureName(PageHealthFeature feature) => feature switch
    {
        PageHealthFeature.MessageBookmarks => "Message bookmarks",
        PageHealthFeature.Drafts => "Drafts",
        PageHealthFeature.ConversationExport => "Conversation export",
        PageHealthFeature.ApprovalAlerts => "Approval alerts",
        _ => feature.ToString(),
    };

    /// <summary>The status's display words.</summary>
    public static string StatusWord(PageHealthStatus status) => status switch
    {
        PageHealthStatus.Recognised => "Recognised",
        PageHealthStatus.NotRecognised => "Not recognised",
        PageHealthStatus.NotApplicable => "Not applicable",
        PageHealthStatus.NoCardShowing => "No card showing",
        _ => status.ToString(),
    };

    /// <summary>One line per finding — "Feature — Status. Explanation Advice" — the shape the Diagnostics section lists.</summary>
    public static IReadOnlyList<string> RenderLines(IReadOnlyList<PageHealthFinding> findings)
    {
        var lines = new List<string>(findings.Count);
        foreach (var finding in findings)
        {
            var line = $"{FeatureName(finding.Feature)} — {StatusWord(finding.Status)}. {finding.Explanation}";
            if (!string.IsNullOrWhiteSpace(finding.Advice))
                line += $" {finding.Advice}";
            lines.Add(line);
        }
        return lines;
    }
}
