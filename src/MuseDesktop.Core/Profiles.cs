using System.Text.RegularExpressions;

namespace MuseDesktop.Core;

/// <summary>
/// One sign-in profile (v0.3.0): a named WebView2 user-data folder. Each
/// profile is a separate Muse account session — cookies and local storage
/// never mix. The default profile keeps the folder the app has always used
/// (%LOCALAPPDATA%\MuseDesktop\WebView2) so existing sign-ins survive.
/// </summary>
public sealed class AccountProfile
{
    public string Id { get; set; } = Profiles.DefaultId;
    public string Name { get; set; } = "Default";

    /// <summary>Start this profile at the Muse home page instead of reopening its last page (v0.4.0). Kept in sync with <see cref="StartPage"/> by Profiles.EnsureValid (v0.9.0, S5 #47) so older builds still read the choice correctly.</summary>
    public bool StartAtHome { get; set; }

    /// <summary>
    /// The 3-way start-page choice (S5 #47): reopen the last page, the
    /// Muse home page, or <see cref="CustomStartUrl"/>. Files written
    /// before v0.9.0 have no mode (LastPage by default) plus the old
    /// StartAtHome bool; EnsureValid migrates the bool into the mode on
    /// first load and keeps the two in sync thereafter.
    /// </summary>
    public StartPageMode StartPage { get; set; } = StartPageMode.LastPage;

    /// <summary>
    /// The custom start page for StartPageMode.Custom (S5 #47). Stored
    /// as typed (trimmed, length-capped by EnsureValid); VALIDITY is a
    /// launch-time decision (StartPagePolicy) — an invalid value falls
    /// back to the home page with a log line, never to a broken page.
    /// </summary>
    public string? CustomStartUrl { get; set; }

    /// <summary>Optional downloads folder for this profile only (S2 #20); null = the shared downloads folder (or the system Downloads folder).</summary>
    public string? DownloadsFolder { get; set; }

    /// <summary>
    /// Soft download quota for this profile's downloads folder (R4 #140),
    /// in bytes; 0 = no cap. Warning-only by design: reaching it never
    /// blocks a download and never deletes anything.
    /// </summary>
    public long DownloadQuotaBytes { get; set; }

    /// <summary>
    /// Accent color for this profile (S3 #24): a key from
    /// <see cref="ProfileColors"/> (e.g. "Blue"), or null for no color.
    /// Shown as a small dot next to the profile's name; the app itself
    /// keeps the black/red family look.
    /// </summary>
    public string? DisplayColor { get; set; }

    /// <summary>
    /// Avatar glyph for this profile (R3 #121): a key from
    /// <see cref="WorkspaceGlyphs"/> (the same fixed text-glyph palette
    /// workspaces use), or null for no glyph. Shown next to the name in
    /// the switcher, menus, and the tray, and stamped onto the profile's
    /// desktop-shortcut icon. Sanitized like a workspace glyph — an
    /// unknown key means "no glyph".
    /// </summary>
    public string? AvatarGlyph { get; set; }

    /// <summary>
    /// The profile group this profile belongs to (R3 #127): an id from
    /// AppSettings.ProfileGroups, or null for ungrouped. Organization
    /// only — it changes nothing about the profile's data. Repaired by
    /// ProfileGroups.EnsureValid (a dangling id clears to null).
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// Archived (R3 #130): hidden from every switcher, menu, and chord
    /// list, but its data folder is untouched and it can be restored
    /// anytime from Settings. NOT a delete — nothing is removed, and an
    /// explicit launch (a desktop shortcut or link naming the profile)
    /// still opens it. The active profile is never archived: EnsureValid
    /// un-archives it as a fail-safe so the app can never be running a
    /// profile its own switchers pretend doesn't exist.
    /// </summary>
    public bool Archived { get; set; }

    // ── Per-profile lock (R3 #122) ────────────────────────────────────
    // The same salted-PBKDF2 record shape as the app lock (AppLock), one
    // record per profile, stored on the profile itself. The global app
    // lock still gates first; a profile PIN only ever gates LAUNCHING
    // that profile. Like the app lock it is a privacy screen, not
    // encryption — see ProfileLock for the operations and KNOWN_LIMITATIONS
    // for the honest wording. Never copied by copy/clone/export: credential
    // material does not travel.

    /// <summary>Whether this profile asks for its own PIN at launch (R3 #122).</summary>
    public bool LockEnabled { get; set; }

    /// <summary>Base64 salt for this profile's PIN record (R3 #122); see AppLock for the format.</summary>
    public string? LockSalt { get; set; }

    /// <summary>Base64 PBKDF2 hash for this profile's PIN record (R3 #122).</summary>
    public string? LockHash { get; set; }

    /// <summary>PBKDF2 iteration count for this profile's PIN record (R3 #122).</summary>
    public int LockIterations { get; set; }

    /// <summary>
    /// Per-profile zoom override (S3 #26); null = use the shared
    /// <see cref="AppSettings.ZoomFactor"/>. Sanitized through ZoomMath.
    /// </summary>
    public double? ZoomFactor { get; set; }

    // ── Conversation view (R5B #201/#205) ────────────────────────────
    // The first per-profile settings that restyle the hosted page —
    // a scoped, user-requested exception to the standing no-restyle
    // rule (see DECISIONS.md, v1.6.0). They are applied by injecting
    // CSS built by PageStyleCss; if the page's shape changes, the
    // rules simply stop matching and the page renders as shipped.

    /// <summary>Always-visible, wider conversation scrollbar (R5B #201). On by default — the page's auto-hiding overlay scrollbar is the complaint this answers.</summary>
    public bool ConversationScrollbars { get; set; } = true;

    /// <summary>Chat text scale in percent (R5B #205): 100 = the page's own size; clamped to 50–200 by ReadingComfort on load.</summary>
    public int ChatTextScalePercent { get; set; } = ReadingComfort.DefaultTextScalePercent;

    /// <summary>Reading-column max width in pixels (R5B #205): 0 = the page's own width; otherwise clamped to 480–1600.</summary>
    public int ReadingColumnWidthPx { get; set; } = ReadingComfort.DefaultColumnWidthPx;

    /// <summary>Message line spacing in percent (R5B #205): 0 = the page's own spacing; otherwise clamped to 100–200.</summary>
    public int ChatLineSpacingPercent { get; set; } = ReadingComfort.DefaultLineSpacingPercent;

    /// <summary>When this profile was last launched with (S3 #27). Session state — never copied to a new profile (S3 #29).</summary>
    public DateTimeOffset? LastUsedUtc { get; set; }

    /// <summary>
    /// Optional data size cap for this profile (S8 #75), in bytes:
    /// 0 = off (default), else one of <see cref="ProfileDataCaps"/>.
    /// When the profile's folder grows past it, cleanup deletes ONLY
    /// allowlisted disposable cache folders (see
    /// <see cref="CacheCleanup"/>) — cookies and sign-in data are never
    /// in the plan, by construction.
    /// </summary>
    public long DataCapBytes { get; set; }

    /// <summary>When the cache cleanup last ran for this profile (S8 #75), shown in the site-data viewer. Session-adjacent state — never copied.</summary>
    public DateTimeOffset? LastCacheCleanupUtc { get; set; }

    /// <summary>How many bytes the last cache cleanup freed for this profile (S8 #75).</summary>
    public long LastCacheCleanupFreedBytes { get; set; }
}

/// <summary>
/// The fixed accent palette for profiles (S3 #24). Keys are what settings
/// store; hex values are what the shell paints. Unknown keys sanitize to
/// "no color" — a hand-edited settings file can never invent a color.
/// </summary>
public static class ProfileColors
{
    public static readonly IReadOnlyList<(string Key, string Hex)> Palette = new[]
    {
        ("Red", "#E50914"),
        ("Blue", "#3B82F6"),
        ("Green", "#22A06B"),
        ("Purple", "#8B5CF6"),
        ("Orange", "#E8862E"),
        ("Gray", "#8A8A93"),
    };

    /// <summary>The canonical palette key for <paramref name="key"/> (case-insensitive), or null when it names no palette color.</summary>
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

    /// <summary>The hex color for a palette key, or null when unknown/none.</summary>
    public static string? HexFor(string? key)
    {
        var canonical = Sanitize(key);
        if (canonical is null)
            return null;
        foreach (var (k, hex) in Palette)
            if (k == canonical)
                return hex;
        return null;
    }
}

/// <summary>Invariants and helpers for the profile list stored in settings.</summary>
public static class Profiles
{
    public const string DefaultId = "default";

    /// <summary>Hard cap on stored profiles (S3 #30: raised from 8 to 32; EnsureValid truncates beyond it).</summary>
    public const int MaxProfiles = 32;

    /// <summary>Beyond this many profiles the Settings UI warns before adding another (S3 #30) — each profile keeps its own sign-in data on disk.</summary>
    public const int SoftProfileWarningThreshold = 8;

    /// <summary>Cap for the pending folder-deletion list (S3 #28) — it can never hold more ids than profiles could exist.</summary>
    public const int MaxPendingDeletions = MaxProfiles;

    private static readonly Regex IdShape = new("^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.Compiled);

    public static bool IsValidId(string? id) => id is not null && IdShape.IsMatch(id);

    /// <summary>A fresh unique-ish id for a new profile.</summary>
    public static string NewId() => "p" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>
    /// Repair the list in place: never empty, ids valid and unique, the
    /// default profile present, the active id pointing at a real profile.
    /// Called from AppSettings.Sanitized so every load is safe.
    /// </summary>
    public static void EnsureValid(AppSettings settings, bool extendedZoomRange = false)
    {
        settings.Profiles ??= new List<AccountProfile>();
        var seen = new HashSet<string>();
        settings.Profiles.RemoveAll(p =>
            p is null || !IsValidId(p.Id) || !seen.Add(p.Id));
        if (!settings.Profiles.Any(p => p.Id == DefaultId))
            settings.Profiles.Insert(0, new AccountProfile());
        foreach (var p in settings.Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
                p.Name = p.Id == DefaultId ? "Default" : "Profile";
            p.Name = p.Name.Trim();
            if (p.Name.Length > 40)
                p.Name = p.Name[..40];
            p.DownloadsFolder = string.IsNullOrWhiteSpace(p.DownloadsFolder)
                ? null
                : p.DownloadsFolder.Trim();
            // R4 #140: a hand-edited negative quota reads as "no cap".
            if (p.DownloadQuotaBytes < 0)
                p.DownloadQuotaBytes = 0;
            // S3 #24: unknown color keys become "no color".
            p.DisplayColor = ProfileColors.Sanitize(p.DisplayColor);
            // S5 #47: migrate + sync the start-page choice. An undefined
            // mode value (hand-edited file) reads as LastPage; the legacy
            // bool wins when the mode was never set (pre-v0.9.0 files),
            // then the bool is rewritten FROM the mode so the two can
            // never disagree after a load.
            if (!Enum.IsDefined(p.StartPage))
                p.StartPage = StartPageMode.LastPage;
            if (p.StartPage == StartPageMode.LastPage && p.StartAtHome)
                p.StartPage = StartPageMode.Home;
            p.StartAtHome = p.StartPage == StartPageMode.Home;
            p.CustomStartUrl = string.IsNullOrWhiteSpace(p.CustomStartUrl)
                ? null
                : p.CustomStartUrl.Trim();
            if (p.CustomStartUrl is { Length: > 2048 })
                p.CustomStartUrl = p.CustomStartUrl[..2048];
            // S3 #26: a zoom override is clamped like any zoom; a value
            // that can't be a zoom at all (NaN, zero, negative) means
            // "no override", never a broken factor.
            if (p.ZoomFactor is { } z)
                p.ZoomFactor = double.IsNaN(z) || z <= 0 ? null : ZoomMath.Clamp(z, extendedZoomRange);
            // S8 #75: the data cap must be one of the offered sizes —
            // anything else (hand-edited, negative) means off, never a
            // surprise cleanup; a negative freed-bytes stamp is nonsense.
            p.DataCapBytes = ProfileDataCaps.Sanitize(p.DataCapBytes);
            if (p.LastCacheCleanupFreedBytes < 0)
                p.LastCacheCleanupFreedBytes = 0;
            // R3 #121: unknown avatar glyph keys become "no glyph" —
            // the same repair ProfileColors gives unknown colors.
            p.AvatarGlyph = WorkspaceGlyphs.Sanitize(p.AvatarGlyph);
            // R5B #205: the reading-comfort numbers clamp to their
            // ranges (0 stays 0 = "the page's own" for width/spacing).
            p.ChatTextScalePercent = ReadingComfort.ClampTextScale(p.ChatTextScalePercent);
            p.ReadingColumnWidthPx = ReadingComfort.ClampColumnWidth(p.ReadingColumnWidthPx);
            p.ChatLineSpacingPercent = ReadingComfort.ClampLineSpacing(p.ChatLineSpacingPercent);
            // R3 #122: a profile lock whose stored record no longer
            // parses turns the lock OFF (fail-open, exactly like the
            // app lock) — a corrupt record must never lock its owner
            // out of their own profile.
            ProfileLock.Repair(p);
        }
        while (settings.Profiles.Count > MaxProfiles)
            settings.Profiles.RemoveAt(settings.Profiles.Count - 1);
        if (!settings.Profiles.Any(p => p.Id == settings.ActiveProfileId))
            settings.ActiveProfileId = DefaultId;
        // R3 #130 fail-safe: the profile the app is running can never
        // be archived — switchers hide archived profiles, and the app
        // must never look like it's running a profile that doesn't
        // exist. (A hand-edited file or a stale clone could set both.)
        var active = settings.Profiles.FirstOrDefault(p => p.Id == settings.ActiveProfileId);
        if (active is { Archived: true })
            active.Archived = false;
    }

    /// <summary>Folder name (under %LOCALAPPDATA%\MuseDesktop) for a profile's WebView2 data.</summary>
    public static string FolderName(string profileId) =>
        profileId == DefaultId ? "WebView2" : $"WebView2-{profileId}";

    /// <summary>The effective zoom for a launch (S3 #26): the profile's override when it has one, else the shared zoom — always clamped.</summary>
    public static double EffectiveZoom(AccountProfile? profile, double sharedZoom) =>
        ZoomMath.Clamp(profile?.ZoomFactor ?? sharedZoom);

    /// <summary>The effective zoom honoring the extended-range setting (S6 #57): same source choice, wider clamp.</summary>
    public static double EffectiveZoom(AccountProfile? profile, double sharedZoom, bool extendedRange) =>
        ZoomMath.Clamp(profile?.ZoomFactor ?? sharedZoom, extendedRange);

    /// <summary>
    /// The profile's name as shown in menus (S3 #24/#25): a "●" prefix
    /// when the profile has an accent color, so the dot survives into
    /// plain-text surfaces like the tray tooltip.
    /// </summary>
    public static string MenuLabel(AccountProfile profile) =>
        profile.DisplayColor is null ? profile.Name : $"● {profile.Name}";

    /// <summary>
    /// The label switchers show (R3 #121): when the profile has an
    /// avatar glyph, the glyph replaces the color dot as the leading
    /// mark (a profile gets ONE leading mark; the color still paints
    /// the dot/swatch beside it on surfaces that draw one). Without a
    /// glyph this is exactly <see cref="MenuLabel"/>.
    /// </summary>
    public static string DisplayLabel(AccountProfile profile) =>
        profile.AvatarGlyph is { } glyph && WorkspaceGlyphs.SymbolFor(glyph) is { } symbol
            ? $"{symbol} {profile.Name}"
            : MenuLabel(profile);

    /// <summary>The profiles switchers may show (R3 #130): every non-archived profile, in stored order.</summary>
    public static IReadOnlyList<AccountProfile> Visible(IEnumerable<AccountProfile> profiles) =>
        profiles.Where(p => !p.Archived).ToList();

    /// <summary>The profiles switchers may show for these settings (R3 #130).</summary>
    public static IReadOnlyList<AccountProfile> VisibleProfiles(AppSettings settings) =>
        Visible(settings.Profiles);

    /// <summary>
    /// Create a profile carrying <paramref name="source"/>'s SETTINGS
    /// (S3 #29): start page (including the S5 #47 mode + custom URL),
    /// downloads folder, zoom override, color.
    /// Session-adjacent state is never copied — the new profile starts
    /// with no last-used stamp and (by construction, since last pages live
    /// in AppSettings.LastUrlByProfile under the profile's own id) no
    /// restored page and an empty WebView2 folder.
    /// </summary>
    public static AccountProfile CopySettingsFrom(AccountProfile source, string newId, string newName) =>
        new()
        {
            Id = newId,
            Name = newName,
            StartAtHome = source.StartAtHome,
            StartPage = source.StartPage,
            CustomStartUrl = source.CustomStartUrl,
            DownloadsFolder = source.DownloadsFolder,
            DisplayColor = source.DisplayColor,
            ZoomFactor = source.ZoomFactor,
            // S8 #75: the data cap is a setting and copies; the cleanup
            // stamps are this profile's own history and do not.
            DataCapBytes = source.DataCapBytes,
            // R4 #140: the downloads quota is a setting too — it copies.
            DownloadQuotaBytes = source.DownloadQuotaBytes,
            // R3 #121/#127: appearance and organization copy too — the
            // avatar glyph and the group membership are part of how the
            // profile looks and where it sits. What NEVER copies (here
            // or in Clone): the lock record, the archived flag, and any
            // sign-in data (which lives in the profile's own folder and
            // is never read by this code at all).
            AvatarGlyph = source.AvatarGlyph,
            GroupId = source.GroupId,
            // R5B #201/#205: the conversation-view choices are
            // appearance and copy with it.
            ConversationScrollbars = source.ConversationScrollbars,
            ChatTextScalePercent = source.ChatTextScalePercent,
            ReadingColumnWidthPx = source.ReadingColumnWidthPx,
            ChatLineSpacingPercent = source.ChatLineSpacingPercent,
            LastUsedUtc = null,
        };

    /// <summary>
    /// Clone a profile (R3 #124): a brand-new profile carrying the
    /// source's settings + appearance (via <see cref="CopySettingsFrom"/>)
    /// under a deduplicated "… copy" name, inserted right after the
    /// source. The clone starts un-archived with NO lock record, no
    /// schedule (rules name profile ids; the new id has none yet), no
    /// usage history, and an EMPTY data folder — sign-in data is never
    /// copied, by construction. Returns null when the source doesn't
    /// exist or the profile cap is reached.
    /// </summary>
    public static AccountProfile? Clone(AppSettings settings, string sourceId)
    {
        var source = settings.Profiles.FirstOrDefault(p => p.Id == sourceId);
        if (source is null || settings.Profiles.Count >= MaxProfiles)
            return null;
        var taken = new HashSet<string>(settings.Profiles.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);
        // Keep the suffixed name inside the 40-char cap EnsureValid
        // enforces, so the clone's name survives a save unchanged.
        var stem = source.Name.Length > 34 ? source.Name[..34].TrimEnd() : source.Name;
        var name = stem + " copy";
        for (var n = 2; taken.Contains(name); n++)
            name = $"{stem} copy {n}";
        var clone = CopySettingsFrom(source, NewId(), name);
        settings.Profiles.Insert(settings.Profiles.IndexOf(source) + 1, clone);
        return clone;
    }

    // The Windows file-name-hostile set, spelled out: a shortcut is a
    // Windows artifact, so this must not depend on the host OS's rules
    // (Path.GetInvalidFileNameChars on Linux allows ':' '*' '?').
    private static readonly char[] WindowsInvalidFileNameChars =
        { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>Desktop shortcut file name for a profile (S3 #23): the name with any file-name-hostile characters removed.</summary>
    public static string ShortcutFileName(AccountProfile profile)
    {
        var safe = new string((profile.Name ?? "")
            .Select(c => c < 32 || WindowsInvalidFileNameChars.Contains(c) ? ' ' : c).ToArray()).Trim();
        while (safe.Contains("  "))
            safe = safe.Replace("  ", " ");
        if (safe.Length == 0)
            safe = "Profile";
        return $"MuseDesktop - {safe}.lnk";
    }

    /// <summary>
    /// Which profile a launch uses (S3 #23): an explicit --profile request
    /// that names an existing profile wins; a request naming nothing real
    /// falls back to the saved active profile and reports the fallback so
    /// the caller can log it. A null request is the ordinary launch.
    /// </summary>
    public static (string ProfileId, bool FellBack) ResolveLaunchProfile(AppSettings settings, string? requestedId)
    {
        if (requestedId is null)
            return (settings.ActiveProfileId, false);
        if (IsValidId(requestedId) && settings.Profiles.Any(p => p.Id == requestedId))
            return (requestedId, false);
        return (settings.ActiveProfileId, true);
    }

    // ── Pending folder deletions (S3 #28) ─────────────────────────────
    // Removing a profile deletes its data folder right away when the app
    // can. When it can't (the folder belongs to the running profile, or
    // the delete fails), the id is queued here and the NEXT launch —
    // before any WebView2 environment exists — retries the delete.

    /// <summary>Queue a removed profile's folder for deletion at next startup. No-op for live profiles or invalid ids.</summary>
    public static void QueuePendingDeletion(AppSettings settings, string profileId)
    {
        if (!IsValidId(profileId))
            return;
        if (settings.Profiles.Any(p => p.Id == profileId))
            return;
        settings.PendingProfileFolderDeletions ??= new List<string>();
        if (!settings.PendingProfileFolderDeletions.Contains(profileId))
            settings.PendingProfileFolderDeletions.Add(profileId);
        SanitizePendingDeletions(settings);
    }

    /// <summary>
    /// Repair the pending-deletion list: valid ids only, deduped, never
    /// naming a profile that exists (again) — deleting a live profile's
    /// folder would sign its user out — and capped. Called from
    /// AppSettings.Sanitized so every load is safe.
    /// </summary>
    public static void SanitizePendingDeletions(AppSettings settings)
    {
        settings.PendingProfileFolderDeletions ??= new List<string>();
        var live = new HashSet<string>(settings.Profiles.Select(p => p.Id));
        var seen = new HashSet<string>();
        settings.PendingProfileFolderDeletions.RemoveAll(id =>
            !IsValidId(id) || live.Contains(id) || !seen.Add(id));
        while (settings.PendingProfileFolderDeletions.Count > MaxPendingDeletions)
            settings.PendingProfileFolderDeletions.RemoveAt(settings.PendingProfileFolderDeletions.Count - 1);
    }
}

/// <summary>
/// On-disk facts about one profile's data folder (S3 #27): how big it is
/// and the Settings line describing it. Measuring is defensive — a folder
/// that doesn't exist yet is "no data", and files that vanish or are
/// locked mid-walk are skipped, never fatal.
/// </summary>
public static class ProfileDataInfo
{
    /// <summary>Total size in bytes of a profile folder (recursive), or null when the folder doesn't exist.</summary>
    public static long? MeasureFolderBytes(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath))
                return null;
            long total = 0;
            var stack = new Stack<string>();
            stack.Push(folderPath);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        stack.Push(sub);
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        try { total += new FileInfo(file).Length; }
                        catch { /* locked or vanished mid-walk — skip the file */ }
                    }
                }
                catch { /* unreadable directory — skip its subtree */ }
            }
            return total;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The Settings line for a profile: "Data: 12.4 MB · Last used: 2026-10-24", or "No data on this PC yet" when the folder doesn't exist.</summary>
    public static string Describe(string folderPath, AccountProfile profile)
    {
        var bytes = MeasureFolderBytes(folderPath);
        if (bytes is null)
            return "No data on this PC yet";
        var text = $"Data: {DownloadText.FormatBytes(bytes.Value)}";
        if (profile.LastUsedUtc is { } used)
            text += $" · Last used: {used.ToLocalTime():yyyy-MM-dd}";
        return text;
    }
}
