using System.Text;
using System.Text.Json;

namespace MuseDesktop.Core;

// ─────────────────────── guided field pass (Diagnostics) ───────────────────────
//
// Some of what MuseDesktop does can only be proven on a real PC:
// a toast arriving while the window is inactive, a download's live
// progress, the tray surviving a window close. Automated checks
// can't see any of that — only the person at the machine can. The
// field pass is the guided checklist for exactly those proofs: nine
// checks, each saying what to do and what "working" looks like,
// answered Pass / Problem / Skipped with an optional note.
//
// The pass records the user's OWN verdicts. It verifies nothing by
// itself and never pretends to — the wording everywhere says the
// user is the judge. (The automated counterpart is the Page health
// check, which probes the loaded page directly.)
//
// Everything here is local: one JSON file beside the settings
// (AppPaths.FieldPassPath), saved on every answer so closing the
// window — or the app dying mid-pass — loses nothing. Notes are the
// user's own words about their own machine; they are never logged,
// and the only way they leave the PC is the summary the user
// explicitly copies.

/// <summary>One field-pass check's answer. Unanswered is the starting state, and also what a note-only entry carries.</summary>
public enum FieldPassVerdict
{
    Unanswered,
    Pass,
    Problem,
    Skipped,
}

/// <summary>The feature a check's "Open the feature" button opens — the app's existing windows, never a simulation.</summary>
public enum FieldPassOpenTarget
{
    /// <summary>No button: the check is done by hand (in a conversation, or with the window closed).</summary>
    None,

    /// <summary>The Settings window.</summary>
    Settings,

    /// <summary>The Diagnostics window.</summary>
    Diagnostics,

    /// <summary>The Downloads window.</summary>
    Downloads,
}

/// <summary>One check in the pass: what to do, and what working looks like, in plain words.</summary>
public sealed record FieldPassStep(
    string Id, string Title, string Instructions, string SuccessLine,
    FieldPassOpenTarget OpenTarget);

/// <summary>
/// The nine checks, in pass order. The catalogue is data on
/// purpose: the shell renders whatever is here, the tests pin its
/// shape (nine steps, unique ids, every step fully worded), and the
/// summary walks the same list.
/// </summary>
public static class FieldPassCatalog
{
    public static readonly IReadOnlyList<FieldPassStep> Steps = new[]
    {
        new FieldPassStep("layouts", "Layouts",
            "Open Settings → Appearance, and switch the Shell layout three times: Standard, then Compact Focus, then Left Rail. Leave the one you like selected, then restart MuseDesktop.",
            "The window rearranges every time you switch, and the layout you left selected is still selected after the restart.",
            FieldPassOpenTarget.Settings),
        new FieldPassStep("page-health", "Page health",
            "In Diagnostics, find the Page health section and choose \u201cRun page check\u201d.",
            "Message bookmarks, drafts, conversation export and approval alerts each report Recognised \u2014 approval alerts may instead say \u201cNo card showing\u201d, which is also the healthy result.",
            FieldPassOpenTarget.Diagnostics),
        new FieldPassStep("approval-alert", "Approval alert",
            "Start something in a conversation that asks for your approval. Before you answer it, switch to another app so the MuseDesktop window is not the active one, and wait for the approval card to appear in the chat.",
            "You hear the beep and a MuseDesktop toast appears in the corner. Nothing is approved for you \u2014 the alert only watches and listens.",
            FieldPassOpenTarget.None),
        new FieldPassStep("conversation-tools", "Conversation tools",
            "Open a conversation and bookmark one message. Reload the page. Then type a few words in the message box (don't send), switch to a different conversation, and come back to this one.",
            "The bookmark stripe is still on the message after the reload, and the words you typed are back in the message box when you return.",
            FieldPassOpenTarget.None),
        new FieldPassStep("downloads", "Downloads",
            "Download any file from a conversation. Watch it in the Downloads window while it comes down; when it finishes, open the file from the download history.",
            "The download shows live progress \u2014 percentage, speed and time remaining \u2014 finishes cleanly, and the file opens from the history afterwards.",
            FieldPassOpenTarget.Downloads),
        new FieldPassStep("tray-hotkey", "Tray and hotkey",
            "Close the MuseDesktop window. Then press the global hotkey \u2014 Ctrl+Alt+M unless you changed it in Settings \u2192 Behavior.",
            "Closing the window minimises MuseDesktop to the tray instead of quitting, and the hotkey brings the window back.",
            FieldPassOpenTarget.None),
        new FieldPassStep("app-lock", "App lock",
            "From the \u22ef menu, choose Lock now, then unlock with your PIN or password. If you don't use the app lock, \u201cSkip for now\u201d is the right answer for this check.",
            "MuseDesktop locks, and your PIN or password unlocks it again.",
            FieldPassOpenTarget.None),
        new FieldPassStep("settings-search", "Settings search",
            "Open Settings and type \u201clayout\u201d into the search box at the top of the window.",
            "The Shell layout setting is offered in the search results.",
            FieldPassOpenTarget.Settings),
        new FieldPassStep("update-check", "Update check",
            "In Settings \u2192 Behavior, look over the update settings; then choose Check for updates (in the \u22ef menu, or in About MuseDesktop) and let it finish.",
            "It tells you you're up to date or offers the newer version \u2014 and nothing downloads or installs until you accept.",
            FieldPassOpenTarget.Settings),
    };

    /// <summary>The step with this id, or null (ids are the store's keys, so unknown ids in a file are dropped, never guessed at).</summary>
    public static FieldPassStep? ById(string? id) =>
        id is null ? null : Steps.FirstOrDefault(s => s.Id == id);
}

/// <summary>One check's saved state: its verdict, the user's note, and when the verdict was given (null while unanswered).</summary>
public sealed class FieldPassEntry
{
    public FieldPassVerdict Verdict { get; set; } = FieldPassVerdict.Unanswered;
    public string Note { get; set; } = "";
    public DateTimeOffset? AnsweredUtc { get; set; }
}

/// <summary>
/// One run of the pass: an entry per answered (or note-carrying)
/// check, keyed by step id. Deliberately small — the shell loads it
/// when the window opens, records into it on every click, and saves
/// it straight back through <see cref="FieldPassStore"/>.
/// </summary>
public sealed class FieldPassSession
{
    /// <summary>Notes are a sentence or two about one check; the cap keeps a hand-edited file from growing teeth.</summary>
    public const int MaxNoteLength = 4000;

    public Dictionary<string, FieldPassEntry> Entries { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The entry for a step, or null when the step has neither a verdict nor a note yet.</summary>
    public FieldPassEntry? EntryFor(string stepId) =>
        Entries.TryGetValue(stepId, out var entry) ? entry : null;

    /// <summary>The step's verdict (Unanswered when it has no entry).</summary>
    public FieldPassVerdict VerdictFor(string stepId) =>
        EntryFor(stepId)?.Verdict ?? FieldPassVerdict.Unanswered;

    /// <summary>
    /// Record a verdict for a step, with the note as it currently
    /// stands. Unknown step ids are a programming error (the shell
    /// only ever passes catalogue ids) and throw.
    /// </summary>
    public void Record(string stepId, FieldPassVerdict verdict, string? note, DateTimeOffset whenUtc)
    {
        if (FieldPassCatalog.ById(stepId) is null)
            throw new ArgumentException($"Unknown field-pass step id '{stepId}'.", nameof(stepId));
        if (verdict == FieldPassVerdict.Unanswered)
            throw new ArgumentException("Recording a verdict requires Pass, Problem, or Skipped.", nameof(verdict));
        var entry = EntryFor(stepId) ?? new FieldPassEntry();
        entry.Verdict = verdict;
        entry.Note = CleanNote(note);
        entry.AnsweredUtc = whenUtc;
        Entries[stepId] = entry;
    }

    /// <summary>
    /// Keep a note the user typed without (yet) giving a verdict —
    /// autosaved like a draft, so closing the window mid-thought
    /// loses nothing. A blank note on an unanswered step removes the
    /// entry again; the file stays clean.
    /// </summary>
    public void SetNote(string stepId, string? note)
    {
        if (FieldPassCatalog.ById(stepId) is null)
            throw new ArgumentException($"Unknown field-pass step id '{stepId}'.", nameof(stepId));
        var cleaned = CleanNote(note);
        var entry = EntryFor(stepId);
        if (entry is null)
        {
            if (cleaned.Length == 0)
                return;
            Entries[stepId] = new FieldPassEntry { Note = cleaned };
            return;
        }
        entry.Note = cleaned;
        if (entry.Verdict == FieldPassVerdict.Unanswered && cleaned.Length == 0)
            Entries.Remove(stepId);
    }

    /// <summary>Start the pass over: every verdict and note goes. The shell confirms first.</summary>
    public void Reset() => Entries.Clear();

    internal static string CleanNote(string? note)
    {
        var cleaned = (note ?? "").Trim();
        return cleaned.Length <= MaxNoteLength ? cleaned : cleaned[..MaxNoteLength];
    }

    /// <summary>
    /// Drop anything a hand-edited or damaged file smuggled in:
    /// entries for steps that don't exist, and notes past the cap.
    /// Verdict values are validated by the store's mapping before
    /// they get here.
    /// </summary>
    public FieldPassSession Sanitized()
    {
        var clean = new FieldPassSession();
        foreach (var (id, entry) in Entries)
        {
            if (entry is null || FieldPassCatalog.ById(id) is null)
                continue;
            clean.Entries[id] = new FieldPassEntry
            {
                Verdict = entry.Verdict,
                Note = CleanNote(entry.Note),
                AnsweredUtc = entry.AnsweredUtc,
            };
        }
        return clean;
    }
}

/// <summary>The pass's shape at a glance: how many checks are answered, and how they split.</summary>
public sealed record FieldPassProgress(
    int Total, int Passed, int Problems, int Skipped, int Answered, int Remaining)
{
    /// <summary>True when every check has a verdict (notes may still change, but the pass is done).</summary>
    public bool IsComplete => Remaining == 0;
}

/// <summary>Derived answers about a session: progress counts, where to resume, and the verdicts' display words.</summary>
public static class FieldPass
{
    /// <summary>Count the session against the catalogue. A note-only entry is not an answer.</summary>
    public static FieldPassProgress Progress(FieldPassSession session)
    {
        var passed = 0;
        var problems = 0;
        var skipped = 0;
        foreach (var step in FieldPassCatalog.Steps)
        {
            switch (session.VerdictFor(step.Id))
            {
                case FieldPassVerdict.Pass: passed++; break;
                case FieldPassVerdict.Problem: problems++; break;
                case FieldPassVerdict.Skipped: skipped++; break;
            }
        }
        var answered = passed + problems + skipped;
        return new FieldPassProgress(
            FieldPassCatalog.Steps.Count, passed, problems, skipped,
            answered, FieldPassCatalog.Steps.Count - answered);
    }

    /// <summary>The first check with no verdict yet — where a reopened pass resumes. Null when the pass is complete.</summary>
    public static FieldPassStep? NextUnanswered(FieldPassSession session) =>
        FieldPassCatalog.Steps.FirstOrDefault(s => session.VerdictFor(s.Id) == FieldPassVerdict.Unanswered);

    /// <summary>The verdict's word in the summary and the window's status line.</summary>
    public static string VerdictWord(FieldPassVerdict verdict) => verdict switch
    {
        FieldPassVerdict.Pass => "Passed",
        FieldPassVerdict.Problem => "Problem reported",
        FieldPassVerdict.Skipped => "Skipped",
        _ => "Not answered",
    };
}

/// <summary>
/// The copyable summary: plain markdown naming the app version and
/// date, the counts, and every check's verdict with its note. This
/// is the only way a pass leaves the PC, and only when the user
/// copies it — it goes wherever the user pastes it (a bug report,
/// a support thread), nowhere else.
/// </summary>
public static class FieldPassSummary
{
    public static string Render(FieldPassSession session, string appVersion, DateTimeOffset now)
    {
        var progress = FieldPass.Progress(session);
        var text = new StringBuilder();
        text.AppendLine("# Field pass \u2014 MuseDesktop");
        text.AppendLine();
        text.AppendLine($"MuseDesktop version: {appVersion}");
        text.AppendLine($"Date: {now:yyyy-MM-dd} (this PC's local time)");
        text.AppendLine($"Progress: {progress.Answered} of {progress.Total} answered \u2014 " +
                        $"{progress.Passed} passed, {progress.Problems} with a problem, " +
                        $"{progress.Skipped} skipped, {progress.Remaining} not answered");
        text.AppendLine();
        var number = 0;
        foreach (var step in FieldPassCatalog.Steps)
        {
            number++;
            var entry = session.EntryFor(step.Id);
            var verdict = entry?.Verdict ?? FieldPassVerdict.Unanswered;
            text.AppendLine($"{number}. {step.Title} \u2014 {FieldPass.VerdictWord(verdict)}");
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.Note))
                text.AppendLine($"   Note: {entry.Note.Trim()}");
        }
        return text.ToString().TrimEnd() + "\n";
    }
}

/// <summary>
/// Loads/saves the field-pass session JSON, in the house store
/// style: atomic write (the shared <see cref="AtomicFile"/>); a
/// damaged file is preserved as .corrupt-* and a fresh session is
/// used — a broken checklist file must never cost the user the app,
/// only the pass. Verdicts travel as strings in the file and map
/// leniently on the way in: an unrecognised verdict word reads as
/// Unanswered with its note kept, rather than condemning the whole
/// file for one hand-edit typo.
/// </summary>
public static class FieldPassStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class EntryDto
    {
        public string? Verdict { get; set; }
        public string? Note { get; set; }
        public DateTimeOffset? AnsweredUtc { get; set; }
    }

    private sealed class SessionDto
    {
        public Dictionary<string, EntryDto?>? Entries { get; set; }
    }

    public static FieldPassSession Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new FieldPassSession();
            var dto = JsonSerializer.Deserialize<SessionDto>(File.ReadAllText(path), Options);
            var session = new FieldPassSession();
            if (dto?.Entries is not null)
            {
                foreach (var (id, entry) in dto.Entries)
                {
                    if (entry is null)
                        continue;
                    session.Entries[id] = new FieldPassEntry
                    {
                        Verdict = ParseVerdict(entry.Verdict),
                        Note = entry.Note ?? "",
                        AnsweredUtc = entry.AnsweredUtc,
                    };
                }
            }
            return session.Sanitized();
        }
        catch (Exception)
        {
            try
            {
                var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(path, backup, overwrite: false);
            }
            catch { /* best effort — a fresh session is the contract */ }
            return new FieldPassSession();
        }
    }

    public static void Save(string path, FieldPassSession session)
    {
        var dto = new SessionDto
        {
            Entries = session.Entries.ToDictionary(
                pair => pair.Key,
                pair => (EntryDto?)new EntryDto
                {
                    Verdict = pair.Value.Verdict.ToString(),
                    Note = pair.Value.Note,
                    AnsweredUtc = pair.Value.AnsweredUtc,
                }),
        };
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(dto, Options));
    }

    private static FieldPassVerdict ParseVerdict(string? word) =>
        Enum.TryParse<FieldPassVerdict>(word, ignoreCase: true, out var verdict)
        && Enum.IsDefined(verdict)
            ? verdict
            : FieldPassVerdict.Unanswered;
}
