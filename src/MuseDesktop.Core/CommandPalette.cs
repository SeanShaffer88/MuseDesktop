using System.Text;
using System.Text.Json;

namespace MuseDesktop.Core;

// ───────────────────── fuzzy matching (R1 #101/#108) ─────────────────────
//
// ONE matcher for the whole shell: the command palette ranks with it,
// and Settings search adopted it in R1 (replacing its simpler
// whole-word-prefix rule). Two shapes, one scoring core:
//
//  - Score(query, text): the query's characters must appear in the
//    text in order (a subsequence). Bonuses for matches that start a
//    word, run consecutively, start the text, or appear verbatim;
//    penalties for a late start and for spread. Built for SHORT texts
//    (command titles, profile names).
//
//  - WordScore(term, text): the best Score of the term against any
//    single WORD of the text. Built for keyword blobs (Settings
//    sections, help keywords): a term may not smear across words —
//    "profile" must not match "…protocol… first…" by borrowing
//    letters from three different words.
//
// Scores are only ever compared within one query; their absolute
// values mean nothing. Everything is deterministic — same inputs,
// same order, every time (pinned by tests).

/// <summary>The outcome of a fuzzy match: whether it matched, and the rank score when it did.</summary>
public readonly record struct FuzzyScore(bool IsMatch, int Score)
{
    public static readonly FuzzyScore NoMatch = new(false, 0);
}

public static class FuzzyMatch
{
    private const int CharScore = 10;
    private const int ConsecutiveBonus = 15;
    private const int WordStartBonus = 20;
    private const int StartsTextBonus = 15;
    private const int VerbatimBonus = 120;
    private const int WordPrefixBonus = 60;

    /// <summary>
    /// Score <paramref name="query"/> against <paramref name="text"/>
    /// as a subsequence. A blank query matches everything with score 0
    /// ("no filter"); a null/empty text never matches a real query.
    /// Case-insensitive; a camelCase hump counts as a word start.
    /// </summary>
    public static FuzzyScore Score(string? query, string? text)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new FuzzyScore(true, 0);
        if (string.IsNullOrEmpty(text))
            return FuzzyScore.NoMatch;
        var q = query.Trim().ToLowerInvariant();
        var t = text.ToLowerInvariant();

        var qi = 0;
        var prev = -2;
        var first = -1;
        var score = 0;
        var gaps = 0;
        for (var i = 0; i < t.Length && qi < q.Length; i++)
        {
            if (t[i] == q[qi])
            {
                if (first < 0)
                    first = i;
                score += CharScore;
                if (i == prev + 1)
                    score += ConsecutiveBonus;
                if (IsWordStart(text, i))
                    score += WordStartBonus;
                prev = i;
                qi++;
            }
            else if (first >= 0)
            {
                gaps++;
            }
        }
        if (qi < q.Length)
            return FuzzyScore.NoMatch;

        score -= gaps;        // a spread-out match ranks below a tight one
        score -= first;       // …and a late start below an early one
        if (first == 0)
            score += StartsTextBonus;
        if (q.Length > 1 && t.Contains(q, StringComparison.Ordinal))
            score += VerbatimBonus;
        else if (q.Length > 1 && AnyWordStartsWith(t, q))
            score += WordPrefixBonus;
        // For equal match quality the shorter text wins by a touch.
        score -= Math.Min(20, Math.Max(0, (text.Length - q.Length) / 8));
        return new FuzzyScore(true, score);
    }

    /// <summary>
    /// The best <see cref="Score"/> of <paramref name="term"/> against
    /// any single word of <paramref name="text"/> (words are runs of
    /// letters/digits). No match when no one word contains the term
    /// as a subsequence.
    /// </summary>
    public static FuzzyScore WordScore(string? term, string? text)
    {
        if (string.IsNullOrWhiteSpace(term))
            return new FuzzyScore(true, 0);
        if (string.IsNullOrEmpty(text))
            return FuzzyScore.NoMatch;
        var best = FuzzyScore.NoMatch;
        foreach (var word in Words(text))
        {
            var s = Score(term, word);
            if (s.IsMatch && (!best.IsMatch || s.Score > best.Score))
                best = s;
        }
        return best;
    }

    private static bool IsWordStart(string text, int i)
    {
        if (i == 0)
            return true;
        var before = text[i - 1];
        if (!char.IsLetterOrDigit(before))
            return true;
        // A camelCase hump ("savePdf") starts a word too.
        return char.IsLower(before) && char.IsUpper(text[i]);
    }

    private static bool AnyWordStartsWith(string lowerText, string lowerQuery)
    {
        foreach (var word in Words(lowerText))
            if (word.StartsWith(lowerQuery, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static IEnumerable<string> Words(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inWord && start < 0)
                start = i;
            if (!inWord && start >= 0)
            {
                yield return text[start..i];
                start = -1;
            }
        }
    }
}

// ───────────────────── the command model (R1 #101/#102) ─────────────────────

/// <summary>Where a palette command came from — the palette groups and the history rule key off it.</summary>
public enum PaletteCommandSource
{
    Action,
    Profile,
    Settings,
    Help,
    Download,
    Window,
    Workspace,
    // R5 (#143/#145): payload-carrying sources like Download/Window —
    // the action history refuses them all the same way.
    ReadingList,
    History,
    // R5B (#202/#206): payload = the bookmark / snippet id, resolved
    // against the store at run time; refused by the history the same.
    Bookmark,
    Snippet,
}

/// <summary>
/// One thing the palette can do (R1 #101). <see cref="Id"/> is the
/// stable identity (recents, tests, and the shell's dispatch all key
/// off it); <see cref="Payload"/> carries the one piece of data the
/// dispatch needs (a profile id, a section id, a file path, a window
/// id) — it is used in-process only and is NEVER logged or written to
/// the action history (see <see cref="ActionHistory"/>).
/// </summary>
public sealed record PaletteCommand(
    string Id,
    string Title,
    string? Detail,
    string Category,
    string Keywords,
    PaletteCommandSource Source,
    string? Payload = null,
    string? GestureText = null);

/// <summary>One ranked palette result (R1 #101): the command, its score for this query, and whether history vouches for it.</summary>
public sealed record PaletteResult(PaletteCommand Command, int Score, bool IsRecent);

/// <summary>
/// A palette provider (R1 #102): one source of commands. The palette
/// asks every registered provider each time it opens, so providers
/// that wrap live data (downloads, windows) are built fresh by the
/// shell per opening — there is no cache to go stale.
/// </summary>
public interface IPaletteProvider
{
    string Name { get; }
    IEnumerable<PaletteCommand> GetCommands();
}

/// <summary>A provider over a fixed list — the built-in actions ride on this.</summary>
public sealed class StaticPaletteProvider : IPaletteProvider
{
    private readonly IReadOnlyList<PaletteCommand> _commands;

    public StaticPaletteProvider(string name, IEnumerable<PaletteCommand> commands)
    {
        Name = name;
        _commands = commands.ToList();
    }

    public string Name { get; }
    public IEnumerable<PaletteCommand> GetCommands() => _commands;
}

/// <summary>One switch command per profile (R1 #102), in profile order.</summary>
public sealed class ProfilesPaletteProvider : IPaletteProvider
{
    private readonly IReadOnlyList<AccountProfile> _profiles;
    private readonly string _activeProfileId;

    public ProfilesPaletteProvider(IReadOnlyList<AccountProfile> profiles, string activeProfileId)
    {
        _profiles = profiles;
        _activeProfileId = activeProfileId;
    }

    public string Name => "Profiles";

    public IEnumerable<PaletteCommand> GetCommands()
    {
        foreach (var p in _profiles)
        {
            yield return new PaletteCommand(
                Id: $"profile.switch.{p.Id}",
                Title: $"Switch to profile: {p.Name}",
                Detail: p.Id == _activeProfileId ? "Current profile" : "Switch profile",
                Category: "Profiles",
                Keywords: $"profile switch account sign-in {p.Name}",
                Source: PaletteCommandSource.Profile,
                Payload: p.Id);
        }
    }
}

/// <summary>
/// Profile-group switch commands (R3 #127): one per group that has a
/// switch target, titled with where it will land ("Switch to group:
/// Clients (opens Work)") — a group switch is a profile switch to the
/// group's most recently used member, so it rides the Profile source
/// and the shell resolves the payload (a GROUP id) through
/// ProfileGroups.SwitchTarget at run time, when "most recent" is true.
/// </summary>
public sealed class ProfileGroupsPaletteProvider : IPaletteProvider
{
    private readonly AppSettings _settings;

    public ProfileGroupsPaletteProvider(AppSettings settings) => _settings = settings;

    public string Name => "Profile groups";

    public IEnumerable<PaletteCommand> GetCommands()
    {
        foreach (var group in _settings.ProfileGroups)
        {
            if (ProfileGroups.SwitchTarget(_settings, group.Id) is not { } target)
                continue;
            yield return new PaletteCommand(
                Id: $"profile-group.switch.{group.Id}",
                Title: $"Switch to group: {group.Name}",
                Detail: $"Opens {target.Name}",
                Category: "Profiles",
                Keywords: $"profile group switch folder {group.Name} {target.Name}",
                Source: PaletteCommandSource.Profile,
                Payload: group.Id);
        }
    }
}

/// <summary>One command per Settings section (R1 #102), from the same index Settings search uses.</summary>
public sealed class SettingsSectionsPaletteProvider : IPaletteProvider
{
    public string Name => "Settings";

    public IEnumerable<PaletteCommand> GetCommands()
    {
        foreach (var s in SettingsSearch.Sections)
        {
            yield return new PaletteCommand(
                Id: $"settings.open.{s.Id}",
                Title: $"Settings: {s.Title}",
                Detail: "Open Settings",
                Category: "Settings",
                Keywords: $"settings {s.Keywords}",
                Source: PaletteCommandSource.Settings,
                Payload: s.Id);
        }
    }
}

/// <summary>One command per Help page (R1 #102), from the help catalog.</summary>
public sealed class HelpTopicsPaletteProvider : IPaletteProvider
{
    public string Name => "Help";

    public IEnumerable<PaletteCommand> GetCommands()
    {
        foreach (var p in HelpCatalog.Pages)
        {
            yield return new PaletteCommand(
                Id: $"help.open.{p.Id}",
                Title: p.Title,
                Detail: "Help & troubleshooting",
                Category: "Help",
                Keywords: $"help troubleshooting {p.Keywords}",
                Source: PaletteCommandSource.Help,
                Payload: p.Id);
        }
    }
}

/// <summary>
/// Recent downloads (R1 #102): an Open and a Show-in-folder command
/// per completed record, newest first, capped — a failed download has
/// no file to open, so it contributes nothing. The file path rides in
/// the payload only; command ids are positional, never path-derived.
/// </summary>
public sealed class DownloadsPaletteProvider : IPaletteProvider
{
    public const int DefaultMaxDownloads = 8;

    private readonly IReadOnlyList<DownloadRecord> _records;
    private readonly int _max;

    public DownloadsPaletteProvider(IReadOnlyList<DownloadRecord> records, int max = DefaultMaxDownloads)
    {
        _records = records;
        _max = Math.Max(0, max);
    }

    public string Name => "Downloads";

    public IEnumerable<PaletteCommand> GetCommands()
    {
        var completed = _records
            .Where(r => r.State == DownloadRecordState.Completed && !string.IsNullOrWhiteSpace(r.FullPath))
            .Take(_max)
            .ToList();
        for (var i = 0; i < completed.Count; i++)
        {
            var r = completed[i];
            var detail = $"{DownloadText.FormatBytes(r.SizeBytes)} · {r.CompletedUtc.ToLocalTime():yyyy-MM-dd}";
            yield return new PaletteCommand(
                Id: $"download.open.{i}",
                Title: r.FileName,
                Detail: $"Open download · {detail}",
                Category: "Downloads",
                Keywords: "download open file",
                Source: PaletteCommandSource.Download,
                Payload: r.FullPath);
            yield return new PaletteCommand(
                Id: $"download.reveal.{i}",
                Title: $"Show {r.FileName} in folder",
                Detail: $"Show in folder · {detail}",
                Category: "Downloads",
                Keywords: "download show reveal folder file explorer",
                Source: PaletteCommandSource.Download,
                Payload: r.FullPath);
        }
    }
}

/// <summary>
/// One open window as the palette and the switcher see it (R1
/// #102/#103). The shell builds these live; <see cref="ProfileColorKey"/>
/// is a <see cref="ProfileColors"/> key the shell turns into the dot.
/// </summary>
public sealed record WindowDescriptor(
    string Id,
    string Title,
    string? ProfileName,
    string? ProfileColorKey,
    bool IsMain,
    bool SupportsFocusMode,
    bool IsActive,
    // R3 #121: the profile's avatar glyph key rides along so the
    // switcher can show the same mark as every other surface.
    // Optional and trailing: every existing construction is unchanged.
    string? ProfileGlyphKey = null);

/// <summary>
/// Window commands (R1 #102/#104): a switch command per window plus
/// the window actions for it — tile left/right, move to next monitor,
/// toggle always-on-top, and toggle focus mode, the last only for
/// windows that have one (today: the main window only).
/// </summary>
public sealed class WindowsPaletteProvider : IPaletteProvider
{
    private readonly IReadOnlyList<WindowDescriptor> _windows;

    public WindowsPaletteProvider(IReadOnlyList<WindowDescriptor> windows) => _windows = windows;

    public string Name => "Windows";

    public IEnumerable<PaletteCommand> GetCommands()
    {
        foreach (var w in _windows)
        {
            var title = string.IsNullOrWhiteSpace(w.Title) ? "Muse window" : w.Title;
            var who = w.ProfileName is null ? "Window" : $"Window · {w.ProfileName}";
            yield return new PaletteCommand(
                Id: $"window.switch.{w.Id}",
                Title: title,
                Detail: w.IsActive ? $"{who} · current" : who,
                Category: "Windows",
                Keywords: "window switch go to",
                Source: PaletteCommandSource.Window,
                Payload: w.Id);
            yield return new PaletteCommand(
                Id: $"window.tile-left.{w.Id}",
                Title: $"Tile left — {title}",
                Detail: "Window",
                Category: "Windows",
                Keywords: "window tile snap left half screen move",
                Source: PaletteCommandSource.Window,
                Payload: w.Id);
            yield return new PaletteCommand(
                Id: $"window.tile-right.{w.Id}",
                Title: $"Tile right — {title}",
                Detail: "Window",
                Category: "Windows",
                Keywords: "window tile snap right half screen move",
                Source: PaletteCommandSource.Window,
                Payload: w.Id);
            yield return new PaletteCommand(
                Id: $"window.next-monitor.{w.Id}",
                Title: $"Move to next monitor — {title}",
                Detail: "Window",
                Category: "Windows",
                Keywords: "window move next monitor display screen",
                Source: PaletteCommandSource.Window,
                Payload: w.Id);
            yield return new PaletteCommand(
                Id: $"window.always-on-top.{w.Id}",
                Title: $"Toggle always on top — {title}",
                Detail: "Window",
                Category: "Windows",
                Keywords: "window pin always on top toggle",
                Source: PaletteCommandSource.Window,
                Payload: w.Id);
            if (w.SupportsFocusMode)
            {
                yield return new PaletteCommand(
                    Id: $"window.focus-mode.{w.Id}",
                    Title: $"Toggle focus mode — {title}",
                    Detail: "Window",
                    Category: "Windows",
                    Keywords: "window focus mode hide toolbar toggle",
                    Source: PaletteCommandSource.Window,
                    Payload: w.Id);
            }
            // R5 #149: the tiling presets ride the same provider, but
            // only for the CURRENT window — eleven presets times every
            // open window would flood the palette with near-duplicates;
            // the switcher offers the full set for any window.
            if (w.IsActive)
            {
                foreach (var preset in WindowTilingPresets.All)
                {
                    if (preset is TilePreset.HalfLeft or TilePreset.HalfRight)
                        continue; // already offered above as tile-left/right
                    yield return new PaletteCommand(
                        Id: $"window.tile-preset.{preset}.{w.Id}",
                        Title: $"Tile: {WindowTilingPresets.Title(preset)} — {title}",
                        Detail: "Window",
                        Category: "Windows",
                        Keywords: "window tile snap preset quarter third screen arrange",
                        Source: PaletteCommandSource.Window,
                        Payload: w.Id);
                }
            }
        }
    }
}

/// <summary>
/// Workspaces (R1 #102 seam; lit up by R2 #115): one Apply command
/// per saved workspace, glyph-prefixed (#119), with the window count
/// and the current/startup marks as the detail. Applying is the
/// shell's business (the planner decides what it takes); the payload
/// is the workspace id and nothing else.
/// </summary>
public sealed class WorkspacesPaletteProvider : IPaletteProvider
{
    private readonly IReadOnlyList<Workspace> _workspaces;
    private readonly string? _activeWorkspaceId;
    private readonly string? _startupWorkspaceId;

    /// <summary>The seam's original shape (no workspaces known): contributes nothing.</summary>
    public WorkspacesPaletteProvider()
        : this(Array.Empty<Workspace>(), null, null)
    {
    }

    public WorkspacesPaletteProvider(
        IReadOnlyList<Workspace> workspaces, string? activeWorkspaceId, string? startupWorkspaceId)
    {
        _workspaces = workspaces;
        _activeWorkspaceId = activeWorkspaceId;
        _startupWorkspaceId = startupWorkspaceId;
    }

    public string Name => "Workspaces";

    public IEnumerable<PaletteCommand> GetCommands()
    {
        foreach (var ws in _workspaces)
        {
            var marks = new List<string>
            {
                ws.Windows.Count == 1 ? "1 window" : $"{ws.Windows.Count} windows",
            };
            if (ws.Id == _activeWorkspaceId)
                marks.Add("current");
            if (ws.Id == _startupWorkspaceId)
                marks.Add("opens at launch");
            var glyphPrefix = WorkspaceGlyphs.SymbolFor(ws.Glyph) is { } symbol ? symbol + " " : "";
            yield return new PaletteCommand(
                Id: $"workspace.apply.{ws.Id}",
                Title: $"{glyphPrefix}Apply workspace: {ws.Name}",
                Detail: string.Join(" · ", marks),
                Category: "Workspaces",
                Keywords: $"workspace apply layout windows session arrange {ws.Name}",
                Source: PaletteCommandSource.Workspace,
                Payload: ws.Id);
        }
    }
}

/// <summary>
/// The built-in command set and the default palette assembly (R1
/// #101/#102). The action list is the single source of truth the
/// commands help page (#109) is generated from, so help can never
/// drift from what the palette offers.
/// </summary>
public static class CommandRegistry
{
    public const string ActionsCategory = "Actions";

    /// <summary>Fixed gesture text for an action from the shortcut catalog, or null when it has none.</summary>
    private static string? GestureOf(string shortcutId) =>
        ShortcutCatalog.Find(shortcutId)?.DisplayGestures;

    public static readonly IReadOnlyList<PaletteCommand> BuiltinActions = BuildActions();

    private static PaletteCommand Action(
        string id, string title, string keywords, string? gesture = null, string? detail = null) =>
        new($"action.{id}", title, detail, ActionsCategory, keywords,
            PaletteCommandSource.Action, GestureText: gesture);

    private static IReadOnlyList<PaletteCommand> BuildActions() => new[]
    {
        Action("switch-window", "Switch window…", "window switcher open windows recent", detail: "Windows"),
        Action("save-workspace", "Save current arrangement as workspace", "workspace save current arrangement layout windows", detail: "Workspaces"),
        Action("snapshot-session", "Take session snapshot", "session snapshot save windows now one-off", detail: "Workspaces"),
        Action("restore-session", "Restore session snapshot", "session snapshot restore windows back one-off", detail: "Workspaces"),
        Action("settings", "Open Settings", "settings preferences options configure", GestureOf(ShortcutCatalog.SettingsId)),
        Action("downloads", "Open Downloads", "downloads files manager finished", GestureOf(ShortcutCatalog.DownloadsId)),
        Action("home", "Go to Muse home", "home start page main", GestureOf(ShortcutCatalog.HomeId)),
        Action("reload", "Reload page", "reload refresh page", GestureOf("reload")),
        Action("find", "Find in page", "find search page text", GestureOf("find")),
        Action("print", "Print", "print page paper", GestureOf("print")),
        Action("save-pdf", "Save page as PDF", "save pdf print file page", detail: "Page"),
        Action("copy-address", "Copy page address", "copy url link address clipboard", detail: "Page"),
        Action("open-in-browser", "Open page in browser", "browser external open default", detail: "Page"),
        Action("new-window", "Open page in new window", "second window new window another", detail: "Windows"),
        Action("focus-mode", "Toggle focus mode", "focus mode hide toolbar compact chrome", GestureOf("focus-mode")),
        Action("fullscreen", "Toggle full screen", "full screen fullscreen f11", GestureOf("fullscreen")),
        Action("always-on-top", "Toggle always on top", "pin always on top window stay", detail: "Window"),
        Action("zoom-in", "Zoom in", "zoom in bigger text page", GestureOf("zoom-in")),
        Action("zoom-out", "Zoom out", "zoom out smaller text page", GestureOf("zoom-out")),
        Action("zoom-reset", "Reset zoom to 100%", "zoom reset 100 percent page", GestureOf("zoom-reset")),
        Action("restore-closed", "Restore the last closed page", "restore closed reopen page tab", GestureOf("restore-closed")),
        Action("shortcuts", "Keyboard shortcuts", "keyboard shortcuts keys list", GestureOf("shortcuts")),
        Action("commands-help", "Commands help", "command palette help list commands ctrl k", detail: "Help"),
        Action("diagnostics", "Open Diagnostics", "diagnostics support health runtime memory", detail: "Support"),
        Action("help", "Help & troubleshooting", "help troubleshooting problems support", detail: "Help"),
        Action("about", "About MuseDesktop", "about version licenses", detail: "App"),
        Action("check-updates", "Check for updates", "update check version new release", detail: "App"),
        Action("lock-now", "Lock now", "lock app lock password pin secure", detail: "App"),
        Action("quick-dictate", "Quick dictate", "voice dictate microphone speak", detail: "Page"),
        // R5 (#141–#150): focus & productivity. Same registry, so the
        // scheduler (#147) can name every one of these too.
        Action("focus-timer", "Start focus timer", "focus timer block countdown concentrate", detail: "Focus"),
        Action("session-timer", "Start/stop session timer", "session timer pomodoro work break cycles start stop", detail: "Focus"),
        Action("reading-list-add", "Save page to reading list", "reading list save page read later bookmark", detail: "Reading list"),
        Action("reading-list", "Open reading list", "reading list saved pages read later", detail: "Reading list"),
        Action("quick-note", "Quick note", "quick note capture jot write down remember", detail: "Notes"),
        Action("notes", "Open notes", "notes notebook view list quick notes", detail: "Notes"),
        Action("weekly-digest", "Weekly digest", "weekly digest summary week review recap", detail: "Focus"),
        Action("stats-dashboard", "Stats dashboard", "stats dashboard counts launches downloads focus minutes week charts", detail: "Focus"),
        // R5B (#202/#203): the conversation tools. The per-bookmark
        // and per-snippet commands ride their own providers below;
        // these are the two fixed doors (the list, the export).
        Action("bookmarks", "Open bookmarks", "bookmarks marked messages nicknames saved jump list", detail: "Bookmarks"),
        Action("export-conversation", "Export conversation as Markdown…", "export conversation save chat markdown file transcript", detail: "Page"),
        Action("export-conversation-pdf", "Export conversation as PDF…", "export conversation save chat pdf file transcript", detail: "Page"),
        Action("exit", "Exit MuseDesktop", "quit exit close app", detail: "App"),
    };

    /// <summary>
    /// The commands the generated help page lists (R1 #109): the
    /// stable, user-independent set — built-in actions, Settings
    /// sections, and Help topics. Profiles, downloads, windows, and
    /// workspaces depend on the user's own data, so the page names
    /// them in prose instead of inventing entries for them.
    /// </summary>
    public static IReadOnlyList<PaletteCommand> HelpListing()
    {
        var palette = new CommandPalette();
        palette.Register(new StaticPaletteProvider("Actions", BuiltinActions));
        palette.Register(new SettingsSectionsPaletteProvider());
        palette.Register(new HelpTopicsPaletteProvider());
        return palette.All();
    }

    /// <summary>Assemble the palette the shell opens (R1 #102; workspaces join in R2 #115): every provider, in display order.</summary>
    public static CommandPalette CreateDefault(
        IReadOnlyList<AccountProfile> profiles,
        string activeProfileId,
        IReadOnlyList<DownloadRecord> recentDownloads,
        IReadOnlyList<WindowDescriptor> windows,
        IReadOnlyList<Workspace>? workspaces = null,
        string? activeWorkspaceId = null,
        string? startupWorkspaceId = null,
        // R3 #127: the settings the group commands resolve against —
        // optional and trailing like the workspace inputs; without it
        // there are simply no group commands.
        AppSettings? groupSettings = null,
        // R5 #143/#145: the active profile's reading list and the
        // session's navigation history — optional and trailing like
        // the rest; without them those sections simply don't appear.
        IReadOnlyList<ReadingListEntry>? readingEntries = null,
        IReadOnlyList<NavigationEntry>? historyEntries = null,
        // R5B #202/#206: the active profile's message bookmarks and
        // prompt snippets — optional and trailing like the rest.
        IReadOnlyList<MessageBookmark>? bookmarks = null,
        IReadOnlyList<PromptSnippet>? snippets = null)
    {
        var palette = new CommandPalette();
        palette.Register(new StaticPaletteProvider("Actions", BuiltinActions));
        palette.Register(new ProfilesPaletteProvider(profiles, activeProfileId));
        if (groupSettings is not null)
            palette.Register(new ProfileGroupsPaletteProvider(groupSettings));
        palette.Register(new WindowsPaletteProvider(windows));
        if (historyEntries is not null)
            palette.Register(new HistoryPaletteProvider(historyEntries));
        palette.Register(new DownloadsPaletteProvider(recentDownloads));
        if (readingEntries is not null)
            palette.Register(new ReadingListPaletteProvider(readingEntries));
        if (bookmarks is not null)
            palette.Register(new MessageBookmarksPaletteProvider(bookmarks));
        if (snippets is not null)
            palette.Register(new PromptSnippetsPaletteProvider(snippets));
        palette.Register(new SettingsSectionsPaletteProvider());
        palette.Register(new HelpTopicsPaletteProvider());
        palette.Register(new WorkspacesPaletteProvider(
            workspaces ?? Array.Empty<Workspace>(), activeWorkspaceId, startupWorkspaceId));
        return palette;
    }
}

/// <summary>
/// The palette engine (R1 #101): providers feed it, the fuzzy matcher
/// ranks, and the action history boosts what the user actually runs.
/// Recents rank first — on a blank query they lead the list outright
/// (the pinned "Recent" section); on a typed query they get a boost
/// that fades with how long ago they ran. Duplicate command ids are
/// impossible in the output: the first provider to offer an id wins.
/// </summary>
public sealed class CommandPalette
{
    public const int DefaultMaxResults = 50;

    private readonly List<IPaletteProvider> _providers = new();

    public void Register(IPaletteProvider provider) => _providers.Add(provider);

    public IReadOnlyList<string> ProviderNames => _providers.Select(p => p.Name).ToList();

    /// <summary>Every command, deduped by id (first provider wins), in provider order.</summary>
    public IReadOnlyList<PaletteCommand> All()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<PaletteCommand>();
        foreach (var provider in _providers)
        {
            foreach (var command in provider.GetCommands())
            {
                if (command is null || string.IsNullOrWhiteSpace(command.Id))
                    continue;
                if (seen.Add(command.Id))
                    all.Add(command);
            }
        }
        return all;
    }

    public IReadOnlyList<PaletteResult> Search(
        string? query, ActionHistory? history = null, int maxResults = DefaultMaxResults)
    {
        if (maxResults < 1)
            maxResults = 1;
        var commands = All();
        if (string.IsNullOrWhiteSpace(query))
        {
            // Blank query: the pinned Recent section first (history
            // order), then everything else in provider order.
            var results = new List<PaletteResult>();
            var recentIds = new HashSet<string>(StringComparer.Ordinal);
            if (history is not null)
            {
                foreach (var id in history.RecentIds)
                {
                    var command = commands.FirstOrDefault(c => c.Id == id);
                    if (command is not null && recentIds.Add(id))
                        results.Add(new PaletteResult(command, 0, IsRecent: true));
                }
            }
            foreach (var command in commands)
            {
                if (!recentIds.Contains(command.Id))
                    results.Add(new PaletteResult(command, 0, IsRecent: false));
            }
            return results.Take(maxResults).ToList();
        }

        var scored = new List<PaletteResult>();
        foreach (var command in commands)
        {
            var title = FuzzyMatch.Score(query, command.Title);
            var wide = title.IsMatch
                ? title
                : FuzzyMatch.Score(query, $"{command.Title} {command.Keywords}");
            if (!wide.IsMatch)
                continue;
            // A title match outranks the same match found only in the
            // keywords — the wide score pays a fixed toll for it.
            var score = title.IsMatch ? title.Score : wide.Score - 40;
            // The palette is a command surface first: a small source
            // weight keeps an action ahead of a help page whose title
            // happens to match the same words ("downloads" opens
            // Downloads; the page about failing downloads is second).
            score += command.Source switch
            {
                PaletteCommandSource.Action => 30,
                PaletteCommandSource.Profile => 15,
                PaletteCommandSource.Window => 15,
                PaletteCommandSource.Workspace => 15,
                PaletteCommandSource.Settings => 5,
                PaletteCommandSource.Download => 5,
                _ => 0,
            };
            var rank = history?.RankOf(command.Id) ?? -1;
            if (rank >= 0)
                score += Math.Max(0, 30 - 3 * rank);
            scored.Add(new PaletteResult(command, score, rank >= 0));
        }
        return scored
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Command.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Command.Id, StringComparer.Ordinal)
            .Take(maxResults)
            .ToList();
    }
}

// ───────────────────── action history (R1 #106) ─────────────────────

/// <summary>
/// What the palette remembers (R1 #106): ACTION IDS ONLY — the ids of
/// built-in actions the user ran, most recent first, capped. Never a
/// profile id, a file path, a page title, or anything else a command
/// carried: <see cref="Record"/> refuses any command whose source is
/// not <see cref="PaletteCommandSource.Action"/>, and the (de)serializer
/// refuses any id without the "action." prefix, so even a hand-edited
/// history file cannot smuggle content in.
/// </summary>
public sealed class ActionHistory
{
    public const int MaxEntries = 12;

    private readonly List<string> _ids = new();

    public IReadOnlyList<string> RecentIds => _ids;

    /// <summary>Remember a run command. Returns false (and remembers nothing) for non-action commands.</summary>
    public bool Record(PaletteCommand command)
    {
        if (command is null || command.Source != PaletteCommandSource.Action)
            return false;
        return RecordId(command.Id);
    }

    /// <summary>Remember an action id directly — same rule: only "action." ids are history.</summary>
    public bool RecordId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("action.", StringComparison.Ordinal))
            return false;
        _ids.Remove(id);
        _ids.Insert(0, id);
        while (_ids.Count > MaxEntries)
            _ids.RemoveAt(_ids.Count - 1);
        return true;
    }

    /// <summary>Position in the recent list (0 = most recent), or -1 when not remembered.</summary>
    public int RankOf(string? id) => id is null ? -1 : _ids.IndexOf(id);

    public void Clear() => _ids.Clear();

    public string Serialize() => JsonSerializer.Serialize(_ids);

    /// <summary>Parse a stored history; anything unreadable or off-shape yields an empty history, never an exception.</summary>
    public static ActionHistory Deserialize(string? json)
    {
        var history = new ActionHistory();
        if (string.IsNullOrWhiteSpace(json))
            return history;
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(json);
            if (ids is null)
                return history;
            foreach (var id in ids)
            {
                // Sanitized through the same gate as a live record:
                // action ids only, deduped, capped.
                if (id is not null
                    && id.StartsWith("action.", StringComparison.Ordinal)
                    && !history._ids.Contains(id))
                    history._ids.Add(id);
                if (history._ids.Count >= MaxEntries)
                    break;
            }
        }
        catch (Exception)
        {
            return new ActionHistory();
        }
        return history;
    }
}

/// <summary>
/// The history file (R1 #106): a small JSON list beside the settings
/// (AppPaths.PaletteHistoryPath), written atomically like the download
/// history. A missing or corrupt file is an empty history — this is a
/// convenience, never worth an error surface.
/// </summary>
public static class ActionHistoryStore
{
    public static ActionHistory Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? ActionHistory.Deserialize(File.ReadAllText(path))
                : new ActionHistory();
        }
        catch (Exception)
        {
            return new ActionHistory();
        }
    }

    public static void Save(string path, ActionHistory history)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, history.Serialize());
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Record a run action and persist. Returns false when the command isn't history material.</summary>
    public static bool Record(string path, PaletteCommand command)
    {
        var history = Load(path);
        if (!history.Record(command))
            return false;
        Save(path, history);
        return true;
    }
}

// ───────────────────── window MRU + tiling (R1 #103/#104) ─────────────────────

/// <summary>
/// The most-recently-used order of open windows (R1 #103): the shell
/// touches an id when its window is activated; the switcher shows the
/// survivors in this order, so the window you were just in is always
/// the preselected first stop. Ids of closed windows prune themselves
/// out on the next sort.
/// </summary>
public sealed class WindowMruList
{
    private readonly List<string> _order = new();

    public IReadOnlyList<string> Order => _order;

    public void Touch(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;
        _order.Remove(id);
        _order.Insert(0, id);
    }

    public void Remove(string id) => _order.Remove(id);

    /// <summary>The given windows in MRU order; windows the list has never seen keep their input order at the end.</summary>
    public IReadOnlyList<WindowDescriptor> Sort(IEnumerable<WindowDescriptor> windows)
    {
        var list = windows.ToList();
        var byId = new Dictionary<string, WindowDescriptor>();
        foreach (var w in list)
            byId[w.Id] = w;
        var sorted = new List<WindowDescriptor>();
        foreach (var id in _order)
            if (byId.Remove(id, out var w))
                sorted.Add(w);
        sorted.AddRange(list.Where(w => byId.ContainsKey(w.Id)));
        return sorted;
    }

    /// <summary>The window a switch most likely wants: the most recently used one that isn't <paramref name="currentId"/>.</summary>
    public WindowDescriptor? Previous(IEnumerable<WindowDescriptor> windows, string? currentId) =>
        Sort(windows).FirstOrDefault(w => w.Id != currentId);

    /// <summary>The switcher's opening selection: the previous window (index 1) when there is one, else the only window.</summary>
    public static int DefaultSelectionIndex(int windowCount) => windowCount > 1 ? 1 : 0;
}

/// <summary>Which half of a work area a tile takes (R1 #104).</summary>
public enum TileSide
{
    Left,
    Right,
}

/// <summary>
/// The pure geometry of the palette's window actions (R1 #104):
/// halves for tiling, the next-monitor cycle, and which monitor a
/// window mostly sits on. All in DIPs — the shell converts with
/// MonitorRects before anything here sees a rect.
/// </summary>
public static class WindowTiling
{
    /// <summary>The left or right half of <paramref name="workArea"/>; an odd width gives the extra pixel to the left half.</summary>
    public static ScreenRect Half(ScreenRect workArea, TileSide side)
    {
        var half = Math.Floor(workArea.Width / 2);
        return side == TileSide.Left
            ? new ScreenRect(workArea.Left, workArea.Top, workArea.Width - half, workArea.Height)
            : new ScreenRect(workArea.Left + (workArea.Width - half), workArea.Top, half, workArea.Height);
    }

    /// <summary>The monitor after <paramref name="current"/> in a cycle of <paramref name="count"/>; -1 when there are no monitors.</summary>
    public static int NextIndex(int current, int count)
    {
        if (count <= 0)
            return -1;
        if (current < 0 || current >= count)
            return 0;
        return (current + 1) % count;
    }

    /// <summary>Index of the monitor <paramref name="window"/> overlaps most (ties: the earliest); -1 when there are no monitors or no overlap at all.</summary>
    public static int IndexOfLargestOverlap(ScreenRect window, IReadOnlyList<ScreenRect> monitors)
    {
        var best = -1;
        var bestArea = 0.0;
        for (var i = 0; i < monitors.Count; i++)
        {
            var overlap = window.Intersect(monitors[i]);
            var area = overlap.Width * overlap.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = i;
            }
        }
        return best;
    }
}

// ───────────────────── go-to-profile chords (R1 #105) ─────────────────────

/// <summary>
/// Go-to-profile chords (R1 #105): Ctrl+Shift+1..9 switch to the 1st
/// through 9th profile, in profile order. In-app only — a
/// system-wide variant would consume Ctrl+Shift+digits in every app
/// (they are common browser/IDE chords), so none is registered; see
/// DECISIONS.md. The mapping lives here so the shell's key handler
/// and the help text can never disagree about it.
/// </summary>
public static class ProfileChords
{
    public const int MaxChordProfiles = 9;

    /// <summary>The chord for a 0-based profile index, or null past the 9th profile (there is no chord).</summary>
    public static ShortcutGesture? ForIndex(int index)
    {
        if (index < 0 || index >= MaxChordProfiles)
            return null;
        return new ShortcutGesture(
            GestureModifiers.Control | GestureModifiers.Shift, $"D{index + 1}");
    }

    /// <summary>True when <paramref name="gesture"/> is a profile chord; <paramref name="index"/> is the 0-based profile index.</summary>
    public static bool TryIndex(ShortcutGesture gesture, out int index)
    {
        index = -1;
        if (gesture.Modifiers != (GestureModifiers.Control | GestureModifiers.Shift))
            return false;
        if (gesture.Key.Length != 2 || gesture.Key[0] != 'D' || gesture.Key[1] is < '1' or > '9')
            return false;
        index = gesture.Key[1] - '1';
        return true;
    }

    /// <summary>The profile id a chord index lands on, or null when the list is shorter than the chord.</summary>
    public static string? ProfileIdAt(IReadOnlyList<AccountProfile> profiles, int index) =>
        index >= 0 && index < profiles.Count ? profiles[index].Id : null;

    /// <summary>How many of the given profiles have a chord (at most 9).</summary>
    public static int ChordCount(int profileCount) => Math.Clamp(profileCount, 0, MaxChordProfiles);
}

// ───────────────────── palette text (R1 #107) ─────────────────────

/// <summary>
/// The palette's spoken/shown status wording (R1 #107), composed in
/// Core so the screen-reader announcements and the visible summary
/// can never drift apart — and so both are pinned by tests.
/// </summary>
public static class PaletteText
{
    /// <summary>The line under the result list: what the query produced.</summary>
    public static string ResultSummary(int count, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return count == 0 ? "No commands available" : $"{count} commands";
        return count == 0
            ? $"No commands match '{query.Trim()}'"
            : $"{count} commands match '{query.Trim()}'";
    }

    /// <summary>The announcement for a selection change: the command and where it sits in the list.</summary>
    public static string SelectionAnnouncement(PaletteCommand command, int index, int total) =>
        $"{command.Title}, {index + 1} of {total}";

    /// <summary>The group header for the pinned history section.</summary>
    public const string RecentGroup = "Recent";
}

// ───────────────────── the generated commands page (R1 #109) ─────────────────────

/// <summary>
/// The Commands help page's list (R1 #109): rendered FROM the palette
/// registry every time it is shown — the same single source the
/// palette itself searches — so the page cannot drift from what
/// Ctrl+K actually offers. A test pins that every listed command
/// appears; generation is deterministic (categories in a fixed
/// order, commands alphabetical within a category).
/// </summary>
public static class CommandsHelp
{
    private static readonly string[] CategoryOrder =
        { "Actions", "Profiles", "Windows", "Downloads", "Settings", "Help", "Workspaces" };

    public static string Render(IEnumerable<PaletteCommand> commands)
    {
        var list = commands.ToList();
        var sb = new StringBuilder();
        sb.AppendLine("Every command the command palette (Ctrl+K) offers, grouped the way the palette groups them. This list is generated from the palette itself when you open this page, so it always matches what Ctrl+K can find.");
        foreach (var category in OrderedCategories(list))
        {
            sb.AppendLine();
            sb.AppendLine(category);
            foreach (var command in list
                         .Where(c => c.Category == category)
                         .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(c => c.Id, StringComparer.Ordinal))
            {
                var line = $"- {command.Title}";
                if (!string.IsNullOrWhiteSpace(command.GestureText))
                    line += $" ({command.GestureText})";
                if (!string.IsNullOrWhiteSpace(command.Detail))
                    line += $" — {command.Detail}";
                sb.AppendLine(line);
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static IEnumerable<string> OrderedCategories(List<PaletteCommand> commands)
    {
        var present = new HashSet<string>(commands.Select(c => c.Category), StringComparer.Ordinal);
        foreach (var category in CategoryOrder)
            if (present.Contains(category))
                yield return category;
        foreach (var category in present
                     .Where(c => !CategoryOrder.Contains(c))
                     .OrderBy(c => c, StringComparer.Ordinal))
            yield return category;
    }
}
