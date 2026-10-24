namespace MuseDesktop.Core;

// ───────────────────────── settings search (S5 #41) ─────────────────────────

/// <summary>One searchable Settings section: id (matching the Settings window's section containers), display title, and the words that find it.</summary>
public sealed record SettingsSectionInfo(string Id, string Title, string Keywords);

/// <summary>
/// The Settings search index (S5 #41). Settings is one scrolling window
/// of sections (not tabs), so search works at section level: a query
/// matches a section when EVERY term appears in its title or keywords;
/// matching sections stay visible (and are highlighted), the rest
/// collapse, and the window scrolls to the first match. The index lives
/// in Core so the mapping from words to sections is unit-tested against
/// the real layout instead of drifting in the shell.
/// </summary>
public static class SettingsSearch
{
    public const string BehaviorId = "behavior";
    public const string TrayId = "tray";
    public const string ProfilesId = "profiles";
    public const string AppearanceId = "appearance";
    public const string ZoomId = "zoom";
    public const string AccessibilityId = "accessibility";
    public const string DownloadsId = "downloads";
    public const string DataId = "data";
    public const string PrivacyId = "privacy";

    public static readonly IReadOnlyList<SettingsSectionInfo> Sections = new[]
    {
        new SettingsSectionInfo(BehaviorId, "Behavior",
            "start with windows startup sign in autostart start in tray minimized close to tray suspend memory timeout check for updates update setup wizard first run welcome"),
        new SettingsSectionInfo(TrayId, "Tray & notifications",
            "tray icon single click double click reopen flash taskbar attention quiet hours do not disturb notifications balloon silent night"),
        new SettingsSectionInfo(ProfilesId, "Account profiles",
            "profile account sign-in rename accent color dot switch remove add copy settings start page home custom url zoom downloads folder data size last used shortcut desktop"),
        new SettingsSectionInfo(AppearanceId, "Appearance & toolbar",
            "theme dark light follow windows system accent color shell text size font scale toolbar customize reorder hide show buttons layout reset appearance"),
        new SettingsSectionInfo(ZoomId, "Zoom",
            "zoom default percent bigger smaller text size page extended range low vision 500"),
        new SettingsSectionInfo(AccessibilityId, "Keyboard & accessibility",
            "keyboard shortcuts keys remap rebind change hotkey reduce motion animation effects announcements screen reader narrator focus dialog extended zoom range low vision accessibility"),
        new SettingsSectionInfo(DownloadsId, "Downloads",
            "downloads folder save ask where collision already exists rename history keep cap notify finished balloon"),
        new SettingsSectionInfo(DataId, "Data",
            "export import backup reset all settings defaults restore portable mode data location usb setup wizard open data folder"),
        // S7 (v0.11.0): listed LAST so the older sections keep winning
        // first-match for the queries they already owned (tests pin a
        // few); the privacy words here are distinct enough to find it.
        new SettingsSectionInfo(PrivacyId, "Privacy & security",
            "privacy security permissions microphone notifications camera granted denied revoke clear cookies cache site data per profile encrypted export password autosave autofill forms connection certificate padlock log viewer redaction privacy report blocked counts backup guide sign-ins app lock unlock pin locked"),
    };

    public static bool IsKnownSection(string? id) =>
        id is not null && Sections.Any(s => s.Id == id);

    /// <summary>
    /// Sections matching <paramref name="query"/>, in layout order.
    /// A blank query matches nothing (the shell shows everything then).
    /// Terms are whole-word prefix matches over title + keywords, so
    /// "down" finds Downloads and "sign" finds the sign-in sections.
    /// </summary>
    public static IReadOnlyList<SettingsSectionInfo> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<SettingsSectionInfo>();
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Select(t => t.ToLowerInvariant())
                         .ToList();
        if (terms.Count == 0)
            return Array.Empty<SettingsSectionInfo>();
        return Sections.Where(s => terms.All(t => Matches(s, t))).ToList();
    }

    private static bool Matches(SettingsSectionInfo section, string term)
    {
        var haystack = (section.Title + " " + section.Keywords).ToLowerInvariant();
        return haystack.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                       .Any(word => word.StartsWith(term, StringComparison.Ordinal));
    }
}

// ───────────────────────── reset all settings (S5 #42) ─────────────────────────

/// <summary>
/// Reset-all-settings support (S5 #42). The reset is deliberately
/// preference-only: it restores every user preference to the factory
/// default but PRESERVES identity and session state — the profile list
/// and which one is active, per-profile last pages, window placements,
/// and the deferred-deletion queue. Removing a profile (and its
/// sign-in data) is a separate, explicit Settings action; a "reset"
/// that silently signed the user out of every account or orphaned
/// profile folders would not be forgiving. The caller additionally
/// writes an <see cref="BackupFileName"/> export of the pre-reset
/// settings BEFORE calling <see cref="ApplyDefaults"/>, so even the
/// preferences are one Import away from coming back.
/// </summary>
public static class SettingsReset
{
    /// <summary>The timestamped backup file name for a reset made at <paramref name="now"/> (local time).</summary>
    public static string BackupFileName(DateTimeOffset now) =>
        $"settings-backup-{now:yyyyMMdd-HHmmss}.json";

    /// <summary>Copy every PREFERENCE field of a fresh default settings object into <paramref name="target"/>; identity/session fields survive untouched.</summary>
    public static void ApplyDefaults(AppSettings target)
    {
        var fresh = new AppSettings();

        // SchemaVersion is NOT a preference — it records the settings
        // format generation (App uses it to tell a pre-v0.9.0 upgrade
        // from a genuine first run), so it is preserved like identity.
        target.ZoomFactor = fresh.ZoomFactor;
        target.CloseToTray = fresh.CloseToTray;
        target.StartWithWindows = fresh.StartWithWindows;
        target.AlwaysOnTop = fresh.AlwaysOnTop;
        target.DownloadsFolder = fresh.DownloadsFolder;
        target.AskWhereToSave = fresh.AskWhereToSave;
        target.NotifyWhenDownloadCompletes = fresh.NotifyWhenDownloadCompletes;
        target.DownloadHistoryCap = fresh.DownloadHistoryCap;
        target.CollisionPolicy = fresh.CollisionPolicy;
        target.CheckForUpdates = fresh.CheckForUpdates;
        target.SuspendWhenInTray = fresh.SuspendWhenInTray;
        target.StartMinimizedToTray = fresh.StartMinimizedToTray;
        target.TraySuspendTimeout = fresh.TraySuspendTimeout;
        target.TraySingleClickRestore = fresh.TraySingleClickRestore;
        target.FlashTaskbarOnAttention = fresh.FlashTaskbarOnAttention;
        target.QuietHoursEnabled = fresh.QuietHoursEnabled;
        target.QuietHoursStartMinutes = fresh.QuietHoursStartMinutes;
        target.QuietHoursEndMinutes = fresh.QuietHoursEndMinutes;
        // S6 fields (v0.10.0): remapped shortcuts return to their
        // catalog defaults, reduce motion follows Windows again, and
        // the zoom range returns to standard.
        target.ShortcutOverrides = fresh.ShortcutOverrides;
        target.ReduceMotion = fresh.ReduceMotion;
        target.ExtendedZoomRange = fresh.ExtendedZoomRange;
        // S7 fields (v0.11.0): password autosave / general autofill
        // return to the WebView2 defaults (both on).
        target.PasswordAutosaveEnabled = fresh.PasswordAutosaveEnabled;
        target.GeneralAutofillEnabled = fresh.GeneralAutofillEnabled;
        // S5 fields.
        target.Theme = fresh.Theme;
        target.AccentColor = fresh.AccentColor;
        target.ShellFontScale = fresh.ShellFontScale;
        target.ToolbarOrder = fresh.ToolbarOrder;
        target.ToolbarHidden = fresh.ToolbarHidden;
        // A reset returns the app to its first-run state, wizard
        // included: the setup wizard offers itself again next launch.
        target.FirstRunCompleted = fresh.FirstRunCompleted;

        // NOT copied (preserved by design): SchemaVersion, Profiles,
        // ActiveProfileId, LastUrl, LastUrlByProfile,
        // WindowPlacementByProfile and the legacy Window* placement
        // fields, PendingProfileFolderDeletions, and the
        // Settings-window memory (SettingsLastSection /
        // SettingsWindowWidth / SettingsWindowHeight).
    }
}

// ───────────────────────── per-profile start page (S5 #47) ─────────────────────────

/// <summary>Where a profile starts (S5 #47): the 3-way extension of the old StartAtHome bool.</summary>
public enum StartPageMode
{
    /// <summary>Reopen the page the profile was last on (the long-standing default).</summary>
    LastPage,

    /// <summary>Always the Muse home page (the old StartAtHome = true).</summary>
    Home,

    /// <summary>A specific muse.ai page, validated by <see cref="StartPagePolicy"/>.</summary>
    Custom,
}

public static class StartPagePolicy
{
    /// <summary>
    /// A custom start page must be a page this app would itself show:
    /// an absolute HTTPS URL on an in-app host (muse.ai or a sign-in
    /// host), passing the ordinary top-level navigation policy. Chat
    /// URLs carry no tokens in the path, and the value is only ever
    /// logged through <see cref="UrlPolicy.RedactForLog"/>.
    /// </summary>
    public static bool IsValidCustomUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme == "https"
               && UrlPolicy.ClassifyNavigation(url) == LinkDisposition.InApp
               && UrlPolicy.IsInAppHost(uri.Host);
    }

    /// <summary>
    /// The URL a launch opens for <paramref name="profile"/>.
    /// <paramref name="CustomFellBack"/> is true when Custom mode named
    /// a URL that fails validation — the caller logs the fallback (the
    /// URL redacted) and the user lands on Home, never on a broken or
    /// off-policy page.
    /// </summary>
    public static (string Url, bool CustomFellBack) Resolve(AccountProfile? profile, string? lastUrl)
    {
        switch (profile?.StartPage)
        {
            case StartPageMode.Home:
                return (UrlPolicy.HomeUrl, false);
            case StartPageMode.Custom:
                var custom = profile.CustomStartUrl?.Trim();
                return IsValidCustomUrl(custom)
                    ? (custom!, false)
                    : (UrlPolicy.HomeUrl, true);
            default:
                return (lastUrl ?? UrlPolicy.HomeUrl, false);
        }
    }
}

// ───────────────────────── portable mode (S5 #49) ─────────────────────────

/// <summary>
/// Where MuseDesktop's own files live (S5 #49). Default ("installed")
/// mode is the long-standing per-user root under %APPDATA%. Portable
/// mode is opt-in by marker file: when <c>portable.txt</c> sits beside
/// the exe, settings and download history (and the log, which already
/// lives under the same root) go to a <c>data</c> folder beside the exe
/// instead — the zip build becomes carry-on-a-USB-stick software.
/// Nothing is migrated automatically: a portable launch starts fresh
/// in its data folder, and removing the marker returns to the installed
/// data untouched. The WebView2 sign-in profiles deliberately stay in
/// %LOCALAPPDATA% in this version — see DECISIONS.md.
/// </summary>
public sealed record SettingsLocation(bool IsPortable, string RootDir)
{
    public const string MarkerFileName = "portable.txt";
    public const string DataFolderName = "data";

    public string SettingsPath => Path.Combine(RootDir, "settings.json");
    public string DownloadsHistoryPath => Path.Combine(RootDir, "downloads.json");
    public string LogDir => Path.Combine(RootDir, "logs");

    /// <summary>Pure resolution: portable iff the marker is present beside the exe.</summary>
    public static SettingsLocation Resolve(string exeDirectory, string installedRoot, bool markerExists) =>
        markerExists
            ? new SettingsLocation(true, Path.Combine(exeDirectory, DataFolderName))
            : new SettingsLocation(false, installedRoot);

    public static SettingsLocation Installed(string root) => new(false, root);
}
