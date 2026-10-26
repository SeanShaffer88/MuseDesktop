using System.Text.Json;
using System.Text.RegularExpressions;

namespace MuseDesktop.Core;

// ───────────────────── app routes (R2 #111) ─────────────────────

/// <summary>
/// An app route (R2 #111): WHERE inside Muse a window is, as a path
/// only — "/chat", "/projects", "/" for home. Workspaces, session
/// snapshots, and the crash journal store routes, NEVER full page
/// URLs: a route carries no host, no query string, and no fragment,
/// so nothing a user happened to have in an address (a token, a
/// search term, a conversation id in a query) can be written to disk
/// by saving a layout. Restoring a route navigates the app's own
/// home host to that path — the page the user lands on is Muse's
/// current page for the section, exactly like typing the section
/// address by hand.
/// </summary>
public static class AppRoute
{
    /// <summary>The home route.</summary>
    public const string Home = "/";

    /// <summary>Routes are paths on the app's home host; anything longer is not a route.</summary>
    public const int MaxLength = 512;

    /// <summary>
    /// The route of an in-app page URL, or null when the URL isn't a
    /// Muse app page (another host, a sign-in provider, unparsable).
    /// The host must be exactly muse.ai — the identity providers in
    /// <see cref="UrlPolicy.InAppHosts"/> are pages the app SHOWS,
    /// not places a layout may send a window back to.
    /// </summary>
    public static string? FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != "https")
            return null;
        if (!uri.Host.Equals("muse.ai", StringComparison.OrdinalIgnoreCase))
            return null;
        var path = uri.AbsolutePath;
        return Sanitize(string.IsNullOrEmpty(path) ? Home : path);
    }

    /// <summary>
    /// Repair a stored route: a leading slash, no whitespace, no
    /// backslashes, no protocol-relative "//" start, capped length.
    /// Null when the value can't be a route at all.
    /// </summary>
    public static string? Sanitize(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return null;
        var r = route.Trim();
        if (r.Length > MaxLength)
            return null;
        if (r[0] != '/' || r.StartsWith("//", StringComparison.Ordinal))
            return null;
        foreach (var c in r)
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\' || c == '?' || c == '#')
                return null;
        return r;
    }

    /// <summary>The full URL a route navigates to (the home host + the path).</summary>
    public static string ToUrl(string? route) =>
        "https://muse.ai" + (Sanitize(route) ?? Home);

    public static bool IsHome(string? route) => (Sanitize(route) ?? Home) == Home;
}

// ───────────────────── the workspace model (R2 #111/#119/#120) ─────────────────────

/// <summary>
/// One window of a workspace (R2 #111): which profile it belongs to,
/// which app route it shows, and where it sits. <see cref="Placement"/>
/// is null when the window was saved without a remembered position
/// (the shell then uses its default spot).
/// </summary>
public sealed class WorkspaceWindow
{
    public string ProfileId { get; set; } = Profiles.DefaultId;
    public string Route { get; set; } = AppRoute.Home;
    public WindowPlacement? Placement { get; set; }
    public bool IsMain { get; set; }

    public WorkspaceWindow Clone() => new()
    {
        ProfileId = ProfileId,
        Route = Route,
        IsMain = IsMain,
        Placement = Placement is null
            ? null
            : new WindowPlacement
            {
                Left = Placement.Left,
                Top = Placement.Top,
                Width = Placement.Width,
                Height = Placement.Height,
                Maximized = Placement.Maximized,
            },
    };
}

/// <summary>
/// A named saved layout (R2 #111): a set of windows (profile +
/// placement + app route), an optional glyph (#119) and an optional
/// accent override (#115 — a <see cref="ProfileColors"/> key that
/// replaces the shell accent while this workspace is the active one).
/// </summary>
public sealed class Workspace
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Workspace";
    public string? Glyph { get; set; }
    public string? AccentColor { get; set; }
    public List<WorkspaceWindow> Windows { get; set; } = new();
    public DateTimeOffset? LastAppliedUtc { get; set; }

    public Workspace Clone() => new()
    {
        Id = Id,
        Name = Name,
        Glyph = Glyph,
        AccentColor = AccentColor,
        LastAppliedUtc = LastAppliedUtc,
        Windows = Windows.Select(w => w.Clone()).ToList(),
    };
}

/// <summary>
/// The workspace glyphs (R2 #119): a fixed set of text symbols, so a
/// glyph renders the same in the tray menu (drawn by Windows), the
/// palette, and the main menu. Keys are what settings store; unknown
/// keys sanitize to "no glyph" — a hand-edited file can't invent one.
/// </summary>
public static class WorkspaceGlyphs
{
    public static readonly IReadOnlyList<(string Key, string Symbol)> Palette = new[]
    {
        ("Star", "★"),
        ("Circle", "●"),
        ("Diamond", "◆"),
        ("Square", "■"),
        ("Triangle", "▲"),
        ("Heart", "♥"),
        ("Note", "♪"),
        ("Sun", "☀"),
        ("Moon", "☾"),
        ("Flag", "⚑"),
        ("Cross", "✚"),
        ("House", "⌂"),
    };

    /// <summary>The canonical palette key for <paramref name="key"/> (case-insensitive), or null when it names no glyph.</summary>
    public static string? Sanitize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var trimmed = key.Trim();
        foreach (var (k, _) in Palette)
            if (string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase))
                return k;
        return null;
    }

    /// <summary>The display symbol for a glyph key, or null when unknown/none.</summary>
    public static string? SymbolFor(string? key)
    {
        var canonical = Sanitize(key);
        if (canonical is null)
            return null;
        foreach (var (k, symbol) in Palette)
            if (k == canonical)
                return symbol;
        return null;
    }

    /// <summary>The workspace's name as shown in menus: a glyph prefix when it has one (the same trick as <see cref="Profiles.MenuLabel"/>).</summary>
    public static string MenuLabel(Workspace workspace) =>
        SymbolFor(workspace.Glyph) is { } symbol ? $"{symbol} {workspace.Name}" : workspace.Name;
}

/// <summary>Invariants and helpers for the workspace list stored in settings (R2 #111/#120).</summary>
public static class Workspaces
{
    /// <summary>Hard cap on saved workspaces; EnsureValid truncates beyond it.</summary>
    public const int MaxWorkspaces = 16;

    /// <summary>Hard cap on windows in one workspace — an arrangement, not a window farm.</summary>
    public const int MaxWindowsPerWorkspace = 12;

    private static readonly Regex IdShape = new("^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.Compiled);

    public static bool IsValidId(string? id) => id is not null && IdShape.IsMatch(id);

    /// <summary>A fresh unique-ish id for a new workspace.</summary>
    public static string NewId() => "w" + Guid.NewGuid().ToString("N")[..10];

    public static Workspace? Find(AppSettings settings, string? id) =>
        id is null ? null : settings.Workspaces.FirstOrDefault(w => w.Id == id);

    /// <summary>
    /// Repair the workspace state in place: ids valid and unique,
    /// names trimmed and capped, glyph/accent sanitized to the fixed
    /// palettes, windows repaired by <see cref="SanitizeWindows"/>,
    /// and the startup/active pointers naming real workspaces or
    /// nothing. Called from AppSettings.Sanitized so every load —
    /// file, import, encrypted import — is safe.
    /// </summary>
    public static void EnsureValid(AppSettings settings)
    {
        settings.Workspaces ??= new List<Workspace>();
        var profileIds = new HashSet<string>(settings.Profiles.Select(p => p.Id));
        var seen = new HashSet<string>();
        settings.Workspaces.RemoveAll(w =>
            w is null || !IsValidId(w.Id) || !seen.Add(w.Id));
        foreach (var w in settings.Workspaces)
        {
            if (string.IsNullOrWhiteSpace(w.Name))
                w.Name = "Workspace";
            w.Name = w.Name.Trim();
            if (w.Name.Length > 40)
                w.Name = w.Name[..40];
            w.Glyph = WorkspaceGlyphs.Sanitize(w.Glyph);
            w.AccentColor = ProfileColors.Sanitize(w.AccentColor);
            w.Windows = SanitizeWindows(w.Windows, profileIds);
        }
        while (settings.Workspaces.Count > MaxWorkspaces)
            settings.Workspaces.RemoveAt(settings.Workspaces.Count - 1);
        if (settings.StartupWorkspaceId is not null && Find(settings, settings.StartupWorkspaceId) is null)
            settings.StartupWorkspaceId = null;
        if (settings.ActiveWorkspaceId is not null && Find(settings, settings.ActiveWorkspaceId) is null)
            settings.ActiveWorkspaceId = null;
        if (settings.PendingWorkspaceApplyId is not null && Find(settings, settings.PendingWorkspaceApplyId) is null)
            settings.PendingWorkspaceApplyId = null;
    }

    /// <summary>
    /// Repair a window list (shared by workspaces, the session
    /// snapshot, and the crash journal — all three store the same
    /// shape): drop windows for profiles that don't exist, repair
    /// routes (a bad route reads as Home, never as a broken window),
    /// clamp placements to plausible sizes, cap the count, and leave
    /// exactly one main window (the first flagged one; the first
    /// window when none is flagged).
    /// </summary>
    public static List<WorkspaceWindow> SanitizeWindows(
        List<WorkspaceWindow>? windows, IReadOnlyCollection<string> validProfileIds)
    {
        var result = new List<WorkspaceWindow>();
        if (windows is null)
            return result;
        foreach (var w in windows)
        {
            if (result.Count >= MaxWindowsPerWorkspace)
                break;
            if (w is null || !validProfileIds.Contains(w.ProfileId))
                continue;
            var copy = w.Clone();
            copy.Route = AppRoute.Sanitize(copy.Route) ?? AppRoute.Home;
            if (copy.Placement is { } p)
            {
                p.Width = p.Width is >= 480 and <= 10000 ? p.Width : 1100;
                p.Height = p.Height is >= 320 and <= 10000 ? p.Height : 760;
            }
            result.Add(copy);
        }
        var mainSeen = false;
        foreach (var w in result)
        {
            if (w.IsMain && mainSeen)
                w.IsMain = false;
            else if (w.IsMain)
                mainSeen = true;
        }
        if (!mainSeen && result.Count > 0)
            result[0].IsMain = true;
        return result;
    }

    /// <summary>Save the current arrangement as a new workspace (R2 #111): the shell supplies the captured windows.</summary>
    public static Workspace Capture(string name, IEnumerable<WorkspaceWindow> windows) => new()
    {
        Id = NewId(),
        Name = name,
        Windows = windows.Select(w => w.Clone()).ToList(),
    };

    /// <summary>The next "Workspace N" name that isn't taken — the one-click save's label (rename lives in Settings, #120).</summary>
    public static string NextDefaultName(AppSettings settings)
    {
        var taken = new HashSet<string>(settings.Workspaces.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? "Workspace" : $"Workspace {n}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Duplicate a workspace (R2 #120): settings and windows deep-copy;
    /// the copy is a fresh workspace (new id, no last-applied stamp).
    /// The name gains " copy" (then " copy 2", …) so the pair is
    /// tellable apart in menus.
    /// </summary>
    public static Workspace Duplicate(Workspace source, AppSettings settings)
    {
        var taken = new HashSet<string>(settings.Workspaces.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);
        var name = $"{source.Name} copy";
        for (var n = 2; taken.Contains(name); n++)
            name = $"{source.Name} copy {n}";
        if (name.Length > 40)
            name = name[..40];
        var copy = source.Clone();
        copy.Id = NewId();
        copy.Name = name;
        copy.LastAppliedUtc = null;
        return copy;
    }
}

// ───────────────────── the apply planner (R2 #112) ─────────────────────

/// <summary>One currently open window, as the apply planner sees it (built live by the shell).</summary>
public sealed record OpenWindowState(string Id, string ProfileId, string Route, bool IsMain);

public enum WorkspaceApplyKind
{
    /// <summary>Open/close/navigate in this process to match the target.</summary>
    Apply,

    /// <summary>
    /// The target belongs to another profile: this process can only
    /// ever show the active profile's windows (a profile switch
    /// restarts the app), so the shell asks, persists a pending
    /// apply, switches profile, and applies after the restart.
    /// </summary>
    SwitchProfileFirst,
}

/// <summary>
/// What applying a workspace takes (R2 #112), computed purely from
/// the target and the open windows so the shell only executes.
/// Matching is by (profile, route) as a multiset: two windows on the
/// same route are two windows, and the plan keeps as many of them as
/// the target has. The main window is never closed — it navigates.
/// </summary>
public sealed class WorkspaceApplyPlan
{
    public required WorkspaceApplyKind Kind { get; init; }

    /// <summary>For <see cref="WorkspaceApplyKind.SwitchProfileFirst"/>: the profile the target belongs to.</summary>
    public string? RequiredProfileId { get; init; }

    /// <summary>The target's main-window entry (the shell navigates/places the main window to it); null = leave the main window alone.</summary>
    public WorkspaceWindow? MainWindow { get; init; }

    /// <summary>True when the main window's current route differs from the target's.</summary>
    public bool MainRouteChanges { get; init; }

    /// <summary>Target windows to open (all secondary; the main window is never in this list).</summary>
    public IReadOnlyList<WorkspaceWindow> ToOpen { get; init; } = Array.Empty<WorkspaceWindow>();

    /// <summary>Currently open secondary windows to close (ids from <see cref="OpenWindowState"/>).</summary>
    public IReadOnlyList<string> ToCloseWindowIds { get; init; } = Array.Empty<string>();

    /// <summary>Currently open windows that already match the target and stay.</summary>
    public IReadOnlyList<string> KeptWindowIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Target windows naming a profile other than the target's own:
    /// impossible in an arrangement saved from real windows (they all
    /// share the active profile), possible in a hand-edited file —
    /// they are skipped, and counted so the shell can say so.
    /// </summary>
    public int SkippedOtherProfileCount { get; init; }

    /// <summary>
    /// True when the plan closes at least one window while a download
    /// is in flight (R2 #112): the shell must ask first — closing a
    /// window never cancels a download (the tracker owns it), but the
    /// user may not realize one is running.
    /// </summary>
    public bool RequiresDownloadConfirmation { get; init; }

    /// <summary>True when the plan changes nothing at all.</summary>
    public bool IsNoOp => Kind == WorkspaceApplyKind.Apply
                          && !MainRouteChanges
                          && ToOpen.Count == 0
                          && ToCloseWindowIds.Count == 0;
}

public static class WorkspaceApplyPlanner
{
    public static WorkspaceApplyPlan Plan(
        IReadOnlyList<WorkspaceWindow> target,
        IReadOnlyList<OpenWindowState> current,
        string activeProfileId,
        bool downloadsInFlight)
    {
        // The target's own profile: its main window's (the first
        // flagged, else the first entry). An empty target belongs to
        // whoever is active — applying it just closes secondaries.
        var primaryProfile = target.Count == 0
            ? activeProfileId
            : (target.FirstOrDefault(w => w.IsMain) ?? target[0]).ProfileId;

        if (target.Count > 0 && primaryProfile != activeProfileId)
        {
            return new WorkspaceApplyPlan
            {
                Kind = WorkspaceApplyKind.SwitchProfileFirst,
                RequiredProfileId = primaryProfile,
            };
        }

        var applicable = target.Where(w => w.ProfileId == activeProfileId).ToList();
        var skipped = target.Count - applicable.Count;

        var mainEntry = applicable.FirstOrDefault(w => w.IsMain) ?? applicable.FirstOrDefault();
        var currentMain = current.FirstOrDefault(w => w.IsMain);
        var mainRouteChanges = mainEntry is not null
                               && (currentMain is null || currentMain.Route != mainEntry.Route);

        var kept = new List<string>();
        if (currentMain is not null && mainEntry is not null)
            kept.Add(currentMain.Id);

        // Multiset-match the secondary entries against the open
        // secondaries by route, in target order.
        var pool = current.Where(w => !w.IsMain).ToList();
        var toOpen = new List<WorkspaceWindow>();
        foreach (var entry in applicable.Where(w => !ReferenceEquals(w, mainEntry)))
        {
            var match = pool.FirstOrDefault(w => w.Route == entry.Route);
            if (match is not null)
            {
                pool.Remove(match);
                kept.Add(match.Id);
            }
            else
            {
                toOpen.Add(entry);
            }
        }
        var toClose = pool.Select(w => w.Id).ToList();

        return new WorkspaceApplyPlan
        {
            Kind = WorkspaceApplyKind.Apply,
            MainWindow = mainEntry,
            MainRouteChanges = mainRouteChanges,
            ToOpen = toOpen,
            ToCloseWindowIds = toClose,
            KeptWindowIds = kept,
            SkippedOtherProfileCount = skipped,
            RequiresDownloadConfirmation = toClose.Count > 0 && downloadsInFlight,
        };
    }
}

// ───────────────────── session snapshot (R2 #114) ─────────────────────

/// <summary>
/// A one-off snapshot of the open windows (R2 #114): profile + app
/// route + placement per window — the same shape a workspace holds,
/// without the name/glyph/accent. Deliberately separate from the
/// crash journal: a snapshot is the user's deliberate save, the
/// journal is the app's own bookkeeping, and neither overwrites the
/// other.
/// </summary>
public sealed class SessionSnapshot
{
    public DateTimeOffset TakenUtc { get; set; }
    public List<WorkspaceWindow> Windows { get; set; } = new();

    public static SessionSnapshot Capture(IEnumerable<WorkspaceWindow> windows, DateTimeOffset now) => new()
    {
        TakenUtc = now,
        Windows = windows.Select(w => w.Clone()).ToList(),
    };

    /// <summary>Repair against the profiles that exist now (same rules as a workspace's windows).</summary>
    public SessionSnapshot Sanitized(IReadOnlyCollection<string> validProfileIds) => new()
    {
        TakenUtc = TakenUtc,
        Windows = Workspaces.SanitizeWindows(Windows, validProfileIds),
    };
}

/// <summary>The snapshot file: a small JSON beside the settings, written atomically like the other stores. A missing or corrupt file is "no snapshot", never an error.</summary>
public static class SessionSnapshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static SessionSnapshot? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path), Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(string path, SessionSnapshot snapshot)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

// ───────────────────── crash journal + restore policy (R2 #117/#118) ─────────────────────

/// <summary>
/// The session journal (R2 #117/#118): the running app's own record
/// of the windows it has open, rewritten as the arrangement changes.
/// It exists so the NEXT launch can tell a clean exit from a crash
/// and — when auto-save is on, or once after a crash — offer the
/// windows back. Routes only, like everything in this file.
///
/// Lifecycle: a launch writes a fresh journal early
/// (<see cref="CleanExit"/> false, <see cref="WasRestored"/> saying
/// whether this session's windows came from a restore); the first
/// successful page load sets <see cref="BecameHealthy"/>; a graceful
/// exit rewrites it with <see cref="CleanExit"/> true. A crash leaves
/// CleanExit false — which is the whole detection mechanism.
/// </summary>
public sealed class SessionJournal
{
    public List<WorkspaceWindow> Windows { get; set; } = new();
    public bool CleanExit { get; set; }
    public bool BecameHealthy { get; set; }
    public bool WasRestored { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>What the next launch should do with the previous session's windows (R2 #117).</summary>
public enum CrashRestoreDecision
{
    /// <summary>Start normally (the startup workspace, if any, is the shell's next step).</summary>
    None,

    /// <summary>Ask once: "restore the windows from before the crash?"</summary>
    OfferRestore,

    /// <summary>Auto-save is on: bring the windows back without asking.</summary>
    RestoreSilently,

    /// <summary>
    /// The previous session was itself a restore and never became
    /// healthy before dying — restoring again is what crashed it,
    /// so don't. The loop can never repeat: this decision consumes
    /// the journal, and the new session starts fresh.
    /// </summary>
    SuppressRestoreLoop,
}

public static class CrashRestorePolicy
{
    public static CrashRestoreDecision Decide(SessionJournal? journal, bool autoSaveEnabled)
    {
        if (journal is null || journal.Windows.Count == 0)
            return CrashRestoreDecision.None;
        if (!journal.CleanExit)
        {
            // Unclean exit. The loop guard comes first and applies
            // whether the restore would be offered or silent: a
            // session that was restored and died before its first
            // successful load is evidence the restore itself is the
            // problem.
            if (journal.WasRestored && !journal.BecameHealthy)
                return CrashRestoreDecision.SuppressRestoreLoop;
            return autoSaveEnabled
                ? CrashRestoreDecision.RestoreSilently
                : CrashRestoreDecision.OfferRestore;
        }
        // Clean exit: only auto-save brings windows back, and the
        // restore that follows marks the new journal WasRestored — a
        // crash before it becomes healthy lands in the guard above.
        return autoSaveEnabled
            ? CrashRestoreDecision.RestoreSilently
            : CrashRestoreDecision.None;
    }
}

/// <summary>The journal file: same atomic-write pattern as the other stores; a missing/corrupt file reads as "no journal" (a normal start).</summary>
public static class SessionJournalStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static SessionJournal? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<SessionJournal>(File.ReadAllText(path), Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(string path, SessionJournal journal)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(journal, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
