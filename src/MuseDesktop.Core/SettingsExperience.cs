namespace MuseDesktop.Core;

// ───────────────────────── settings search (S5 #41) ─────────────────────────

/// <summary>One searchable Settings section: id (matching the Settings window's section containers), display title, and the words that find it.</summary>
public sealed record SettingsSectionInfo(string Id, string Title, string Keywords);

/// <summary>
/// The Settings search index (S5 #41). Search works at section
/// level: a query matches a section when EVERY term fuzzy-matches
/// some word of its title or keywords; matching sections stay
/// visible (and are highlighted), the rest collapse, and the window
/// scrolls to the best match. Since R7E (v1.8.6) the window is a
/// rail of pages (<see cref="SettingsPages"/>) rather than one
/// scrolling column, but the unit of search is unchanged: the
/// matched sections show as cards, the rail highlights the best
/// match's page, and clearing the search returns to page view. The
/// index lives in Core so the mapping from words to sections is
/// unit-tested against the real layout instead of drifting in the
/// shell.
///
/// R1 (#108): matching and ranking are the command palette's ONE
/// fuzzy matcher (<see cref="FuzzyMatch"/>), replacing the S5
/// whole-word-prefix rule. Terms still match word-by-word
/// (<see cref="FuzzyMatch.WordScore"/>), so a term can never borrow
/// letters across words; results are ranked by score (a word in the
/// section's title counts for more than the same word in its keyword
/// list), with layout order breaking ties.
/// </summary>
public static class SettingsSearch
{
    public const string BehaviorId = "behavior";
    public const string TrayId = "tray";
    public const string ProfilesId = "profiles";
    public const string WorkspacesId = "workspaces";
    public const string AppearanceId = "appearance";
    public const string ZoomId = "zoom";
    public const string AccessibilityId = "accessibility";
    public const string LanguageId = "language";
    public const string DownloadsId = "downloads";
    public const string DataId = "data";
    public const string FocusId = "focus";
    public const string PrivacyId = "privacy";

    public static readonly IReadOnlyList<SettingsSectionInfo> Sections = new[]
    {
        new SettingsSectionInfo(BehaviorId, "Behavior",
            "start with windows startup sign in autostart start in tray minimized close to tray suspend memory timeout check for updates update updates channel release pre-release prerelease beta snooze remind later frequency daily weekly manual rollback previous version what's new setup wizard first run welcome protocol links musedesktop links url handler command line launching"),
        new SettingsSectionInfo(TrayId, "Tray & notifications",
            "tray icon single click double click reopen flash taskbar attention quiet hours do not disturb notifications balloon silent night"),
        new SettingsSectionInfo(ProfilesId, "Account profiles",
            "profile account sign-in rename accent color dot switch remove add copy settings start page home custom url zoom downloads folder data size last used shortcut desktop storage cap limit cache cleanup avatar glyph icon pin lock password schedule automatic time clone duplicate health cookies backup export import bundle group folder template usage launches archive hide restore"),
        new SettingsSectionInfo(WorkspacesId, "Workspaces & sessions",
            "workspace workspaces session sessions layout windows arrangement save apply startup launch auto-save restore snapshot crash glyph icon accent duplicate rename delete"),
        new SettingsSectionInfo(AppearanceId, "Appearance & toolbar",
            "theme dark light follow windows system accent color shell text size font scale toolbar customize reorder hide show buttons layout reset appearance mica acrylic backdrop material titlebar tint title bar profile color density compact spacing monochrome grayscale overlay badge custom picker hex opacity schedule time of day mini"),
        new SettingsSectionInfo(ZoomId, "Zoom",
            "zoom default percent bigger smaller text size page extended range low vision 500"),
        new SettingsSectionInfo(AccessibilityId, "Keyboard & accessibility",
            "keyboard shortcuts keys remap rebind change hotkey global show hide anywhere dictate reduce motion animation effects announcements screen reader narrator focus dialog extended zoom range low vision accessibility"),
        // R7 (v1.8.0): language & announcements sit between
        // accessibility and downloads in the layout; its words
        // (language, español, announcements, spacing) are its own, so
        // the older sections keep their established matches.
        new SettingsSectionInfo(LanguageId, "Language & announcements",
            "language español spanish english translation translate pack import export locale region system default announcements announce spoken narrator screen reader mute quiet types downloads updates timers focus text spacing line height low vision rtl right-to-left"),
        new SettingsSectionInfo(DownloadsId, "Downloads",
            "downloads folder save ask where collision already exists rename history keep cap notify finished balloon sort rules subfolder file type organize archive dated bandwidth limit speed quiet hours schedule hold large duplicate already downloaded warn again hash sha-256 verify note retry automatic attempts quota soft cap storage batch links paste list"),
        new SettingsSectionInfo(DataId, "Data",
            "export import backup backups rotation restore portable mode data location usb setup wizard open data folder reset museDesktop troubleshooting won't start safe mode storage size cap cleanup diagnostics help support bundle"),
        // R5 (v1.5.0): also late in the list for the same reason as
        // privacy below — its words (focus, timer, notes) must not
        // steal the queries the older sections already owned.
        new SettingsSectionInfo(FocusId, "Focus & productivity",
            "focus timer pomodoro session break reminder reading list saved pages notes quick note hotkey command schedule automatic startup daily weekly digest stats dashboard productivity tiling"),
        // S7 (v0.11.0): listed LAST so the older sections keep winning
        // first-match for the queries they already owned (tests pin a
        // few); the privacy words here are distinct enough to find it.
        new SettingsSectionInfo(PrivacyId, "Privacy & security",
            "privacy security permissions microphone notifications camera granted denied revoke clear cookies cache site data per profile encrypted export password autosave autofill forms connection certificate padlock log viewer redaction privacy report blocked counts backup guide sign-ins app lock unlock pin locked microphone test notification test voice dictation test"),
    };

    public static bool IsKnownSection(string? id) =>
        id is not null && Sections.Any(s => s.Id == id);

    /// <summary>
    /// Sections matching <paramref name="query"/>, best match first.
    /// A blank query matches nothing (the shell shows everything then).
    /// Every term must fuzzy-match a word of the title or keywords, so
    /// "down" finds Downloads and "dwnloads" finds it too.
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
        var scored = new List<(SettingsSectionInfo Section, int Index, int Score)>();
        for (var i = 0; i < Sections.Count; i++)
        {
            var section = Sections[i];
            var total = 0;
            var allMatch = true;
            foreach (var term in terms)
            {
                var title = FuzzyMatch.WordScore(term, section.Title);
                var keywords = FuzzyMatch.WordScore(term, section.Keywords);
                if (!title.IsMatch && !keywords.IsMatch)
                {
                    allMatch = false;
                    break;
                }
                // A term found in the section's own title counts for
                // more than the same term buried in its keyword list.
                total += Math.Max(title.Score, keywords.Score - 25);
            }
            if (allMatch)
                scored.Add((section, i, total));
        }
        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Index)
            .Select(s => s.Section)
            .ToList();
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
        // R7 fields (v1.8.0): the language returns to "follow Windows",
        // every announcement type speaks again, and text spacing
        // returns to Standard. Imported language PACKS are files in
        // the packs folder, not preferences — a reset leaves them
        // installed (deleting a user's translation files is not a
        // preference reset's business).
        target.UiLanguage = fresh.UiLanguage;
        target.AnnouncementKindEnabled = fresh.AnnouncementKindEnabled;
        target.ShellTextSpacingScale = fresh.ShellTextSpacingScale;
        // R7B field (v1.8.2): the token ring's estimated limit is a
        // preference and resets to the default window.
        target.EstimatedContextLimitTokens = fresh.EstimatedContextLimitTokens;
        // R7C field (v1.8.3): the approval beep returns to on.
        target.ApprovalBeepEnabled = fresh.ApprovalBeepEnabled;
        // S7 fields (v0.11.0): the autofill pair return to the
        // current defaults — since R7F (v1.8.8) both are OFF:
        // password autosave is never on at all (Sanitized forces it
        // regardless), and general autofill starts off on a fresh
        // install.
        target.PasswordAutosaveEnabled = fresh.PasswordAutosaveEnabled;
        target.GeneralAutofillEnabled = fresh.GeneralAutofillEnabled;
        // S11 fields (v1.0.0): the global hotkey returns to on with
        // its default gesture, and musedesktop:// links register
        // again (the sync applies at next startup / Settings OK).
        target.GlobalHotkeyEnabled = fresh.GlobalHotkeyEnabled;
        target.GlobalHotkeyGesture = fresh.GlobalHotkeyGesture;
        target.RegisterProtocolLinks = fresh.RegisterProtocolLinks;
        // R2 fields (v1.2.0): the startup-workspace choice and the
        // auto-save opt-in are preferences and reset; the workspace
        // LIST itself is user data like the profile list, and the
        // active/pending pointers are session state — all three are
        // preserved below (a dangling startup pointer after a list
        // edit is Sanitized's job, not the reset's).
        target.StartupWorkspaceId = fresh.StartupWorkspaceId;
        target.AutoSaveSession = fresh.AutoSaveSession;
        // R3 fields (v1.3.0): the ask-vs-auto schedule choice is a
        // preference and resets (to ask-first). NOT preferences, so
        // preserved below: ProfileGroups and ProfileSwitchRules are
        // the user's own organization, per-profile PIN records live on
        // the profiles themselves, and the schedule hold is session
        // state like the other state records.
        target.ProfileScheduleAutoSwitch = fresh.ProfileScheduleAutoSwitch;
        // R5 fields (v1.5.0): the focus/session timer shapes and the
        // quick-note hotkey are preferences and reset. The command
        // schedules are the user's own automation, not preferences —
        // like the workspace list they are preserved below (a reset
        // of PREFERENCES must not silently delete someone's rules).
        target.FocusTimerAutoStart = fresh.FocusTimerAutoStart;
        target.FocusTimerMinutes = fresh.FocusTimerMinutes;
        target.FocusBreakMinutes = fresh.FocusBreakMinutes;
        target.FocusBreakReminders = fresh.FocusBreakReminders;
        target.SessionWorkMinutes = fresh.SessionWorkMinutes;
        target.SessionBreakMinutes = fresh.SessionBreakMinutes;
        target.SessionLongBreakMinutes = fresh.SessionLongBreakMinutes;
        target.SessionCyclesBeforeLongBreak = fresh.SessionCyclesBeforeLongBreak;
        target.QuickNoteHotkeyEnabled = fresh.QuickNoteHotkeyEnabled;
        target.QuickNoteHotkeyGesture = fresh.QuickNoteHotkeyGesture;
        // R5B field (v1.6.0): drafts autosave returns to on. The
        // per-profile conversation-view choices live on the profiles
        // (preserved below, like every per-profile setting), and the
        // bookmarks / drafts / snippets stores are user data in
        // their own files — a preferences reset touches none of them.
        target.DraftsAutosaveEnabled = fresh.DraftsAutosaveEnabled;
        // S9 fields (v0.13.0): the update channel returns to stable
        // only, the check frequency to every launch, any snooze is
        // cleared, and What's New shows again. NOT preferences, so
        // preserved below: LastUpdateCheckUtc, LastSeenVersion, and
        // the kept/previous installer records — a reset must not
        // strand a rollback or re-show old release notes.
        target.UpdateChannel = fresh.UpdateChannel;
        target.UpdateCheckFrequency = fresh.UpdateCheckFrequency;
        target.UpdateSnoozedUntilUtc = fresh.UpdateSnoozedUntilUtc;
        target.ShowWhatsNew = fresh.ShowWhatsNew;
        // S5 fields.
        target.Theme = fresh.Theme;
        target.AccentColor = fresh.AccentColor;
        target.ShellFontScale = fresh.ShellFontScale;
        target.ToolbarOrder = fresh.ToolbarOrder;
        target.ToolbarHidden = fresh.ToolbarHidden;
        // R6 fields: the round-2 face resets with the rest of it —
        // backdrop, title bar tint, density, tray styles, the custom
        // accent, and the theme schedule times.
        target.Backdrop = fresh.Backdrop;
        target.TitleBarProfileTint = fresh.TitleBarProfileTint;
        target.Density = fresh.Density;
        target.TrayIconStyle = fresh.TrayIconStyle;
        target.TrayCountOverlay = fresh.TrayCountOverlay;
        target.CustomAccent = fresh.CustomAccent;
        target.ThemeLightStartMinutes = fresh.ThemeLightStartMinutes;
        target.ThemeDarkStartMinutes = fresh.ThemeDarkStartMinutes;
        // A reset returns the app to its first-run state, wizard
        // included: the setup wizard offers itself again next launch.
        target.FirstRunCompleted = fresh.FirstRunCompleted;

        // NOT copied (preserved by design): SchemaVersion, Profiles,
        // ActiveProfileId, LastUrl, LastUrlByProfile,
        // WindowPlacementByProfile and the legacy Window* placement
        // fields, PendingProfileFolderDeletions, ProfileGroups,
        // ProfileSwitchRules, the schedule hold, CommandSchedules
        // (R5: the user's automation), and the Settings-window memory
        // (SettingsLastSection / SettingsWindowWidth /
        // SettingsWindowHeight).
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
