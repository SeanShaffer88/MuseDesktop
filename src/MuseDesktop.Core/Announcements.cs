using System.Text.Json;

namespace MuseDesktop.Core;

// ───────────────────── the announcement center (R7 #169) ─────────────────────

/// <summary>
/// What an announcement is ABOUT (R7 #169). The center's per-type
/// switches key on these; the spoken live-region announcements of S6
/// #54 were the first types, and the R5 timers / focus events join
/// them here so one place decides what MuseDesktop says out loud.
/// </summary>
public enum AnnouncementKind
{
    Downloads,
    Updates,
    Timers,
    Focus,

    /// <summary>
    /// One-off confirmations of something the user just did (a mic
    /// test line, "support bundle saved"). Not an automatic event, so
    /// it has NO Settings switch — it always speaks — but it is
    /// recorded in the history like everything else the app says.
    /// </summary>
    General,
}

public static class AnnouncementKinds
{
    /// <summary>The switchable kinds, in Settings display order. General is deliberately absent: it has no switch.</summary>
    public static readonly IReadOnlyList<AnnouncementKind> All = new[]
    {
        AnnouncementKind.Downloads, AnnouncementKind.Updates,
        AnnouncementKind.Timers, AnnouncementKind.Focus,
    };

    /// <summary>The settings-storage id for a kind.</summary>
    public static string Id(AnnouncementKind kind) => kind switch
    {
        AnnouncementKind.Downloads => "downloads",
        AnnouncementKind.Updates => "updates",
        AnnouncementKind.Timers => "timers",
        AnnouncementKind.Focus => "focus",
        _ => "general",
    };

    public static AnnouncementKind? FromId(string? id) => id switch
    {
        "downloads" => AnnouncementKind.Downloads,
        "updates" => AnnouncementKind.Updates,
        "timers" => AnnouncementKind.Timers,
        "focus" => AnnouncementKind.Focus,
        "general" => AnnouncementKind.General,
        _ => null,
    };

    /// <summary>The catalog key of the kind's Settings label.</summary>
    public static string LabelKey(AnnouncementKind kind) => $"settings.announce.{Id(kind)}";
}

/// <summary>
/// The per-type switches (R7 #169), stored as kind-id → enabled in
/// settings. Default is ON for every kind (the S6 behavior); a switch
/// only exists in the map once the user turns a kind off (turning it
/// back on removes the entry — the map is the list of exceptions).
/// </summary>
public static class AnnouncementSwitches
{
    /// <summary>Whether <paramref name="kind"/> speaks under a stored switch map. Unknown/absent = on.</summary>
    public static bool IsEnabled(IReadOnlyDictionary<string, bool>? switches, AnnouncementKind kind) =>
        switches is null
        || !switches.TryGetValue(AnnouncementKinds.Id(kind), out var enabled)
        || enabled;

    /// <summary>Repair a stored map: switchable ids only (General has no switch), and only the OFF entries are kept (on = the default, storing it is noise).</summary>
    public static Dictionary<string, bool> Sanitize(IReadOnlyDictionary<string, bool>? switches)
    {
        var clean = new Dictionary<string, bool>();
        if (switches is null)
            return clean;
        foreach (var (id, enabled) in switches)
            if (AnnouncementKinds.FromId(id) is { } kind && kind != AnnouncementKind.General && !enabled)
                clean[id] = false;
        return clean;
    }

    /// <summary>Set one kind's switch, returning the new (sanitized) map.</summary>
    public static Dictionary<string, bool> With(
        IReadOnlyDictionary<string, bool>? switches, AnnouncementKind kind, bool enabled)
    {
        var clean = Sanitize(switches);
        if (enabled)
            clean.Remove(AnnouncementKinds.Id(kind));
        else
            clean[AnnouncementKinds.Id(kind)] = false;
        return clean;
    }
}

/// <summary>One announcement in the history (R7 #169): when, what kind, the exact text, and whether it was spoken (a type switched off is recorded as not spoken — the history is the honest record of what the user was told, and how).</summary>
public sealed record AnnouncementEntry(DateTimeOffset WhenUtc, string KindId, string Text, bool Spoken);

/// <summary>
/// The announcement history (R7 #169): a bounded, local-only list —
/// the newest <see cref="MaxEntries"/> announcements, oldest first.
/// It exists so a user who missed (or muted) a spoken announcement
/// can read it later in Diagnostics: a quiet visual record, never a
/// popup, never sent anywhere. Announcements carry no page content
/// (they are the S6 status texts: file names, version tags, timer
/// states) — the same data the status bar already showed.
/// </summary>
public sealed class AnnouncementHistory
{
    public const int MaxEntries = 100;

    public List<AnnouncementEntry> Entries { get; set; } = new();

    /// <summary>Append an entry, trimming to the cap. Returns the history for chaining.</summary>
    public AnnouncementHistory Record(AnnouncementKind kind, string text, bool spoken, DateTimeOffset whenUtc)
    {
        Entries.Add(new AnnouncementEntry(whenUtc, AnnouncementKinds.Id(kind), text, spoken));
        if (Entries.Count > MaxEntries)
            Entries.RemoveRange(0, Entries.Count - MaxEntries);
        return this;
    }

    /// <summary>The newest <paramref name="count"/> entries, newest first (the Diagnostics display order).</summary>
    public IReadOnlyList<AnnouncementEntry> Recent(int count) =>
        Entries.TakeLast(Math.Max(0, count)).Reverse().ToList();

    /// <summary>One history line for Diagnostics: local time, kind, text, and the not-spoken marker when its type was off.</summary>
    public static string DescribeLine(AnnouncementEntry entry)
    {
        var kind = AnnouncementKinds.FromId(entry.KindId);
        var label = kind is { } k ? k.ToString() : entry.KindId;
        var line = $"{entry.WhenUtc.ToLocalTime():yyyy-MM-dd HH:mm} [{label}] {entry.Text}";
        return entry.Spoken ? line : line + " (not spoken — type turned off)";
    }
}

/// <summary>Loads/saves the announcement history JSON, SettingsStore-style: atomic write; a damaged file is preserved as .corrupt-* and an empty history is used.</summary>
public static class AnnouncementHistoryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AnnouncementHistory Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AnnouncementHistory();
            var history = JsonSerializer.Deserialize<AnnouncementHistory>(File.ReadAllText(path), Options)
                          ?? new AnnouncementHistory();
            history.Entries ??= new List<AnnouncementEntry>();
            history.Entries = history.Entries
                .Where(e => e is not null && AnnouncementKinds.FromId(e.KindId) is not null)
                .TakeLast(AnnouncementHistory.MaxEntries)
                .ToList();
            return history;
        }
        catch (Exception)
        {
            try
            {
                var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(path, backup, overwrite: false);
            }
            catch { /* best effort — an empty history is the contract */ }
            return new AnnouncementHistory();
        }
    }

    public static void Save(string path, AnnouncementHistory history)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(history, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
