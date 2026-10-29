namespace MuseDesktop.Core;

// ───────────────────── settings pages (R7E, v1.8.6) ─────────────────────

/// <summary>
/// One Settings page (R7E): a rail entry in the Settings window —
/// its stable id, the catalog key of its short nav title, the icon
/// glyph it wears in the rail (a Segoe Fluent Icons / Segoe MDL2
/// Assets codepoint, rendered by the shell as text in that font),
/// and the ordered list of existing Settings sections
/// (<see cref="SettingsSearch"/> ids) the page carries as cards.
/// </summary>
public sealed record SettingsPageInfo(
    string Id, string NavTitleKey, string IconGlyph, IReadOnlyList<string> SectionIds);

/// <summary>
/// The Settings window's page catalog (R7E, v1.8.6). The window used
/// to be ONE scrolling column of twelve sections; SEAN directed it
/// to imitate the Muse app's own Settings instead: a left rail of
/// topics, one page at a time on the right, the page's sections
/// grouped as cards. The sections themselves — ids, controls,
/// behavior — are untouched; this catalog only says which page each
/// section lives on, in rail order. It lives in Core so the mapping
/// is unit-tested against the real section list (the same reason
/// the search index lives here): a section that belongs to no page
/// would be a section the window can never show.
/// </summary>
public static class SettingsPages
{
    public const string GeneralId = "general";
    public const string AppearanceId = "appearance";
    public const string ProfilesId = "profiles";
    public const string WorkspacesId = "workspaces";
    public const string NotificationsId = "notifications";
    public const string FocusId = "focus";
    public const string DownloadsId = "downloads";
    public const string DataId = "data";
    public const string LanguageId = "language";
    public const string AccessibilityId = "accessibility";
    public const string PrivacyId = "privacy";

    /// <summary>The pages in rail order (top to bottom).</summary>
    public static readonly IReadOnlyList<SettingsPageInfo> Pages = new[]
    {
        new SettingsPageInfo(GeneralId, "settings.nav_general", "\uE713",
            new[] { SettingsSearch.BehaviorId }),
        // Zoom folds into Appearance as its own card — the one page
        // carrying two sections (a page of one tiny card would be a
        // rail entry spent on three controls).
        new SettingsPageInfo(AppearanceId, "settings.nav_appearance", "\uE790",
            new[] { SettingsSearch.AppearanceId, SettingsSearch.ZoomId }),
        new SettingsPageInfo(ProfilesId, "settings.nav_profiles", "\uE716",
            new[] { SettingsSearch.ProfilesId }),
        new SettingsPageInfo(WorkspacesId, "settings.nav_workspaces", "\uE8A9",
            new[] { SettingsSearch.WorkspacesId }),
        // The section's own name is "Tray & notifications"; the rail
        // title is the short form, the card keeps the full title.
        new SettingsPageInfo(NotificationsId, "settings.nav_notifications", "\uE8BD",
            new[] { SettingsSearch.TrayId }),
        new SettingsPageInfo(FocusId, "settings.nav_focus", "\uE7C1",
            new[] { SettingsSearch.FocusId }),
        new SettingsPageInfo(DownloadsId, "settings.nav_downloads", "\uE896",
            new[] { SettingsSearch.DownloadsId }),
        new SettingsPageInfo(DataId, "settings.nav_data", "\uE8B7",
            new[] { SettingsSearch.DataId }),
        new SettingsPageInfo(LanguageId, "settings.nav_language", "\uE774",
            new[] { SettingsSearch.LanguageId }),
        new SettingsPageInfo(AccessibilityId, "settings.nav_accessibility", "\uE07F",
            new[] { SettingsSearch.AccessibilityId }),
        new SettingsPageInfo(PrivacyId, "settings.nav_privacy", "\uE72E",
            new[] { SettingsSearch.PrivacyId }),
    };

    /// <summary>The page the window opens on when nothing names one: the first in the rail (General).</summary>
    public static SettingsPageInfo Default => Pages[0];

    /// <summary>The page with this id, or null when the id names no page.</summary>
    public static SettingsPageInfo? PageForId(string? id) =>
        id is null ? null : Pages.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// The page carrying a Settings section (deep links and the
    /// last-place memory name sections, not pages). An unknown or
    /// missing section id resolves to <see cref="Default"/> — the
    /// same safe landing the deep-link path has always had: an
    /// unrecognized section is simply not applied, and the window
    /// opens at the top of the rail.
    /// </summary>
    public static SettingsPageInfo PageForSection(string? sectionId)
    {
        if (sectionId is not null)
        {
            foreach (var page in Pages)
                if (page.SectionIds.Contains(sectionId))
                    return page;
        }
        return Default;
    }
}
