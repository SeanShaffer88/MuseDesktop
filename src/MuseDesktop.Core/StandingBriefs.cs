using System.Text;
using System.Text.Json;

namespace MuseDesktop.Core;

// ─────────────────────── standing briefs (v2.1.0) ───────────────────────
//
// A standing brief is a long-form card per project or topic —
// background, rules, preferences — kept locally, in its own file,
// like the snippet library it lives beside. One click opens a new
// conversation with the brief in its message box, so Muse starts
// the session already knowing the situation.
//
// Privacy shape (see DECISIONS, 2026-11-02): a brief is the user's
// own words. It is stored on this PC only, never logged, never
// exported, and it leaves the app exactly once per use — into the
// new conversation's composer, UNSENT. The app never sends on the
// user's behalf: the user reads the brief in the box and presses
// Send themselves, which is what makes it the conversation's first
// message.

/// <summary>
/// One standing brief (v2.1.0): a name, and up to three sections —
/// Background, Rules, Preferences — any of which may be empty, but
/// never all three. The sections are the user's own words, kept
/// verbatim inside (edges trimmed only).
/// </summary>
public sealed class StandingBrief
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Background { get; set; } = "";
    public string Rules { get; set; } = "";
    public string Preferences { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>True when every section is blank — a brief with nothing to say is not a brief.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Background)
        && string.IsNullOrWhiteSpace(Rules)
        && string.IsNullOrWhiteSpace(Preferences);
}

/// <summary>
/// The standing-brief library (v2.1.0): one flat list (briefs are
/// per project, not per profile — the same card serves every
/// profile's conversations), name-sorted, capped. Validation is
/// the snippet library's discipline: a name you can't pick from a
/// list is useless, a section past its cap is refused whole rather
/// than silently cut, and an all-empty brief is refused — it would
/// start a conversation with nothing.
/// </summary>
public sealed class StandingBriefs
{
    /// <summary>The most briefs the library holds. Fifty projects' worth of cards is a shelf, not a database — and every picker stays scannable.</summary>
    public const int MaxBriefs = 50;

    /// <summary>A brief's name, capped like a snippet's (plus a little): it names a project in lists and in the composed header.</summary>
    public const int MaxNameLength = 80;

    /// <summary>One section's cap. A section is a card side, not a document: 6,000 characters is several dense paragraphs, and anything longer belongs in a context pack.</summary>
    public const int MaxSectionLength = 6_000;

    /// <summary>
    /// The composed opening message's cap. Three full sections plus
    /// labels can reach past it, so Compose cuts at this length
    /// (with an ellipsis) rather than handing the composer an
    /// unbounded block — the cut is deterministic and the brief in
    /// the library is never touched by it.
    /// </summary>
    public const int MaxComposedLength = 16_000;

    public List<StandingBrief> Briefs { get; set; } = new();

    /// <summary>The cleaned brief name (one line, capped), or null when nothing usable remains.</summary>
    public static string? CleanName(string? name)
    {
        var clean = string.IsNullOrWhiteSpace(name)
            ? ""
            : string.Join(" ", name.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (clean.Length == 0)
            return null;
        return clean.Length <= MaxNameLength ? clean : clean[..MaxNameLength].TrimEnd();
    }

    /// <summary>
    /// The cleaned section: trimmed at the edges only — the inside
    /// is the user's brief, verbatim. Null when the text is over the
    /// section cap; an empty section cleans to "" (sections are
    /// optional, length is not negotiable).
    /// </summary>
    public static string? CleanSection(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var clean = text.Trim();
        return clean.Length <= MaxSectionLength ? clean : null;
    }

    /// <summary>Add a brief; returns it, or null when the name fails validation, a section is overlong, every section is empty, or the library is at the cap.</summary>
    public StandingBrief? Add(
        string? name, string? background, string? rules, string? preferences, DateTimeOffset now)
    {
        if (CleanName(name) is not { } cleanName
            || CleanSection(background) is not { } cleanBackground
            || CleanSection(rules) is not { } cleanRules
            || CleanSection(preferences) is not { } cleanPreferences)
            return null;
        var brief = new StandingBrief
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = cleanName,
            Background = cleanBackground,
            Rules = cleanRules,
            Preferences = cleanPreferences,
        };
        if (brief.IsEmpty || Briefs.Count >= MaxBriefs)
            return null;
        brief.CreatedUtc = now;
        brief.UpdatedUtc = now;
        Briefs.Add(brief);
        return brief;
    }

    /// <summary>Replace a brief's name and sections — the same validation as Add; a failing edit changes nothing. The creation stamp is kept.</summary>
    public bool Update(
        string briefId, string? name, string? background, string? rules, string? preferences,
        DateTimeOffset now)
    {
        if (CleanName(name) is not { } cleanName
            || CleanSection(background) is not { } cleanBackground
            || CleanSection(rules) is not { } cleanRules
            || CleanSection(preferences) is not { } cleanPreferences)
            return false;
        if (Find(briefId) is not { } brief)
            return false;
        if (string.IsNullOrWhiteSpace(cleanBackground)
            && string.IsNullOrWhiteSpace(cleanRules)
            && string.IsNullOrWhiteSpace(cleanPreferences))
            return false;
        brief.Name = cleanName;
        brief.Background = cleanBackground;
        brief.Rules = cleanRules;
        brief.Preferences = cleanPreferences;
        brief.UpdatedUtc = now;
        return true;
    }

    /// <summary>
    /// Duplicate a brief under a fresh name — "Novel project copy",
    /// then "Novel project copy 2", and so on. The suffix wording
    /// comes from the caller's catalog (this file owns no prose);
    /// each candidate goes through the same name cleaning, so the
    /// cap can shorten but never break it. Null when the brief is
    /// missing, the library is at the cap, or no unique name turns
    /// up in a hundred tries.
    /// </summary>
    public StandingBrief? Duplicate(string briefId, string copySuffix, DateTimeOffset now)
    {
        if (Find(briefId) is not { } source || Briefs.Count >= MaxBriefs)
            return null;
        var taken = new HashSet<string>(
            Briefs.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        for (var attempt = 1; attempt <= 100; attempt++)
        {
            var candidate = CleanName(attempt == 1
                ? $"{source.Name} {copySuffix}"
                : $"{source.Name} {copySuffix} {attempt}");
            if (candidate is null || taken.Contains(candidate))
                continue;
            var copy = new StandingBrief
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = candidate,
                Background = source.Background,
                Rules = source.Rules,
                Preferences = source.Preferences,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            Briefs.Add(copy);
            return copy;
        }
        return null;
    }

    public bool Remove(string briefId) =>
        Briefs.RemoveAll(b => b.Id == briefId) > 0;

    /// <summary>The briefs, name-sorted — the order every picker shows.</summary>
    public IReadOnlyList<StandingBrief> Entries =>
        Briefs.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public StandingBrief? Find(string briefId) =>
        Briefs.FirstOrDefault(b => b.Id == briefId);

    /// <summary>
    /// Compose the brief as one opening message: the localized
    /// header (its {0} is the brief's name), then each non-empty
    /// section under its localized label as a Markdown heading.
    /// Null when the brief is missing or every section is empty —
    /// the "nothing to start with" result the shell detects and
    /// stops on. The result never exceeds
    /// <see cref="MaxComposedLength"/>: an over-long composition is
    /// cut at the cap with an ellipsis marking the cut.
    /// </summary>
    public static string? Compose(
        StandingBrief? brief,
        string header, string backgroundLabel, string rulesLabel, string preferencesLabel)
    {
        if (brief is null || brief.IsEmpty)
            return null;
        var sb = new StringBuilder();
        sb.Append(string.Format(header, brief.Name));
        AppendSection(sb, backgroundLabel, brief.Background);
        AppendSection(sb, rulesLabel, brief.Rules);
        AppendSection(sb, preferencesLabel, brief.Preferences);
        var composed = sb.ToString().TrimEnd();
        if (composed.Length <= MaxComposedLength)
            return composed;
        return composed[..(MaxComposedLength - 1)].TrimEnd() + "…";
    }

    private static void AppendSection(StringBuilder sb, string label, string? text)
    {
        var clean = CleanSection(text);
        if (string.IsNullOrEmpty(clean))
            return;
        sb.Append("\n\n## ").Append(label).Append("\n\n").Append(clean);
    }

    /// <summary>Repair pass after a load: drop briefs that fail the same validation Add enforces, cap the list (name order decides what stays).</summary>
    public void Sanitize()
    {
        Briefs ??= new List<StandingBrief>();
        Briefs.RemoveAll(b => b is null
            || string.IsNullOrWhiteSpace(b.Id)
            || CleanName(b.Name) is null
            || CleanSection(b.Background) is null
            || CleanSection(b.Rules) is null
            || CleanSection(b.Preferences) is null
            || b.IsEmpty);
        foreach (var brief in Briefs)
        {
            brief.Name = CleanName(brief.Name)!;
            brief.Background = CleanSection(brief.Background)!;
            brief.Rules = CleanSection(brief.Rules)!;
            brief.Preferences = CleanSection(brief.Preferences)!;
        }
        var sorted = Briefs.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Briefs.Clear();
        Briefs.AddRange(sorted);
        while (Briefs.Count > MaxBriefs)
            Briefs.RemoveAt(Briefs.Count - 1);
    }
}

/// <summary>
/// The brief store: the house pattern (the field-pass store's
/// shape) — atomic write through the shared <see cref="AtomicFile"/>;
/// a damaged file is preserved as .corrupt-* and an empty library
/// is used, so a broken briefs file can never cost the user the
/// app, only the briefs.
/// </summary>
public static class StandingBriefStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static StandingBriefs Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new StandingBriefs();
            var briefs = JsonSerializer.Deserialize<StandingBriefs>(File.ReadAllText(path), Options)
                ?? new StandingBriefs();
            briefs.Sanitize();
            return briefs;
        }
        catch (Exception)
        {
            try
            {
                var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(path, backup, overwrite: false);
            }
            catch { /* best effort — an empty library is the contract */ }
            return new StandingBriefs();
        }
    }

    public static void Save(string path, StandingBriefs briefs)
    {
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(briefs, Options));
    }
}
