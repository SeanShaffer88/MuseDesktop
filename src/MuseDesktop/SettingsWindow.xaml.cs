using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>Profile accent color key → brush for the profile list dot (S3 #24); "no color" paints transparent.
/// Media types are fully qualified: the WinForms global usings make the short names ambiguous with System.Drawing.</summary>
public sealed class ProfileColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key && ProfileColors.HexFor(key) is { } hex
            ? new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex))
            : System.Windows.Media.Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Edits a CLONE of the app settings (see AppSettings.Clone): OK adopts the
/// clone, Cancel discards it. The live actions live in the Privacy &amp;
/// security section (S7): clear-data, permissions, and site data run
/// against the WebView2 profiles through the main window's
/// <see cref="WebPrivacyGateway"/> — they act on profile data, not on
/// settings, so they take effect immediately and Cancel can't undo them
/// (each one confirms first and says exactly what it will do).
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _working;
    private readonly DiagnosticLog _log;
    private readonly WebPrivacyGateway? _privacy;
    private readonly Action<AppSettings, string> _switchProfile;
    private readonly Func<string>? _notificationTest;
    private readonly Func<string>? _globalHotkeyStatus;
    private readonly Action<AppSettings, string>? _applyWorkspace;
    private readonly Func<string>? _quickNoteHotkeyStatus;
    private readonly Action? _openWeeklyDigest;
    private readonly Action? _openStatsDashboard;

    /// <summary>Workspace glyph choices (R2 #119), parallel to WorkspaceGlyphCombo's items: null ("No icon") then the palette keys.</summary>
    private readonly List<string?> _glyphKeys = new();

    /// <summary>True while workspace controls are being filled from the working settings (their change handlers must not write back).</summary>
    private bool _syncingWorkspaceControls;

    public SettingsWindow(AppSettings working, DiagnosticLog log, WebPrivacyGateway? privacy,
        Action<AppSettings, string> switchProfile, Func<string>? notificationTest = null,
        Func<string>? globalHotkeyStatus = null,
        Action<AppSettings, string>? applyWorkspace = null,
        Func<string>? quickNoteHotkeyStatus = null,
        Action? openWeeklyDigest = null,
        Action? openStatsDashboard = null)
    {
        _working = working;
        _log = log;
        _privacy = privacy;
        _switchProfile = switchProfile;
        _notificationTest = notificationTest;
        _globalHotkeyStatus = globalHotkeyStatus;
        _applyWorkspace = applyWorkspace;
        _quickNoteHotkeyStatus = quickNoteHotkeyStatus;
        _openWeeklyDigest = openWeeklyDigest;
        _openStatsDashboard = openStatsDashboard;
        InitializeComponent();

        // S5 #48: reopen at the remembered size; the last section is
        // restored on Loaded (once layout exists to scroll).
        Width = _working.SettingsWindowWidth;
        if (_working.SettingsWindowHeight > 0)
            Height = _working.SettingsWindowHeight;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += OnSettingsLoaded;

        // The section registry (S5 #41/#48): id ↔ container ↔ header,
        // in layout order — shared by search and the scroll tracker.
        _sections = new (string, System.Windows.Controls.StackPanel, System.Windows.Controls.TextBlock)[]
        {
            (SettingsSearch.BehaviorId, SectionBehavior, HeaderBehavior),
            (SettingsSearch.TrayId, SectionTray, HeaderTray),
            (SettingsSearch.ProfilesId, SectionProfiles, HeaderProfiles),
            (SettingsSearch.WorkspacesId, SectionWorkspaces, HeaderWorkspaces),
            (SettingsSearch.AppearanceId, SectionAppearance, HeaderAppearance),
            (SettingsSearch.ZoomId, SectionZoom, HeaderZoom),
            (SettingsSearch.AccessibilityId, SectionAccessibility, HeaderAccessibility),
            (SettingsSearch.LanguageId, SectionLanguage, HeaderLanguage),
            (SettingsSearch.DownloadsId, SectionDownloads, HeaderDownloads),
            (SettingsSearch.DataId, SectionData, HeaderData),
            (SettingsSearch.FocusId, SectionFocus, HeaderFocus),
            (SettingsSearch.PrivacyId, SectionPrivacy, HeaderPrivacy),
        };

        // S6 #57: the zoom lists come from the step table for the
        // working extended-range choice; the toggle rebuilds them.
        RebuildZoomCombos();
        // S6 #53: key capture for the shortcut editor runs at window
        // level (tunneling), active only while a capture is armed.
        PreviewKeyDown += OnSettingsPreviewKeyDown;

        // S5 (#43/#44/#46): theme choices in ThemeMode order, accent
        // keys parallel to AccentCombo's items, text-scale factors.
        ThemeCombo.Items.Add(Strings.Get("ui.dark"));
        ThemeCombo.Items.Add(Strings.Get("ui.light"));
        ThemeCombo.Items.Add(Strings.Get("ui.follow_windows"));
        // R6 #159: appended last — the older indices are the stored
        // ThemeMode values and must never shift.
        ThemeCombo.Items.Add(Strings.Get("ui.follow_schedule"));
        // R6 appearance round 2: the enum-ordered choice lists.
        BackdropCombo.Items.Add(Strings.Get("ui.automatic_mica"));
        BackdropCombo.Items.Add(Strings.Get("ui.none_solid"));
        BackdropCombo.Items.Add(Strings.Get("ui.mica"));
        BackdropCombo.Items.Add(Strings.Get("ui.acrylic"));
        DensityCombo.Items.Add(Strings.Get("ui.comfortable"));
        DensityCombo.Items.Add(Strings.Get("ui.compact"));
        TrayStyleCombo.Items.Add(Strings.Get("ui.color"));
        TrayStyleCombo.Items.Add(Strings.Get("ui.monochrome"));
        TrayStyleCombo.Items.Add(Strings.Get("ui.profile_colored"));
        TrayOverlayCombo.Items.Add(Strings.Get("ui.number_badge"));
        TrayOverlayCombo.Items.Add(Strings.Get("ui.dot"));
        TrayOverlayCombo.Items.Add(Strings.Get("ui.none"));
        CustomAccentText.TextChanged += (_, _) => RefreshCustomAccentSwatch();
        foreach (var (key, _, _) in AccentPalette.Palette)
        {
            _accentKeys.Add(key);
            AccentCombo.Items.Add(key);
        }
        foreach (var factor in ShellTextScale.Allowed)
            TextScaleCombo.Items.Add(ShellTextScale.FormatPercent(factor));

        // S3 #24: accent-color choices — "None" then the fixed palette.
        _colorKeys.Add(null);
        ProfileColorCombo.Items.Add(Strings.Get("ui.none"));
        foreach (var (key, _) in ProfileColors.Palette)
        {
            _colorKeys.Add(key);
            ProfileColorCombo.Items.Add(key);
        }

        // R2 #119/#115: workspace glyph + accent choices — the glyph
        // list shows its symbol beside the key; the accent list is the
        // profile palette under a "use the app accent" first entry,
        // parallel to _colorKeys (null then the palette keys).
        _glyphKeys.Add(null);
        WorkspaceGlyphCombo.Items.Add(Strings.Get("ui.no_icon"));
        foreach (var (key, symbol) in WorkspaceGlyphs.Palette)
        {
            _glyphKeys.Add(key);
            WorkspaceGlyphCombo.Items.Add($"{symbol} {key}");
        }
        WorkspaceAccentCombo.Items.Add(Strings.Get("ui.use_app_accent"));
        foreach (var (key, _) in ProfileColors.Palette)
            WorkspaceAccentCombo.Items.Add(key);

        // R3 #121/#128: the profile avatar choices are the same glyph
        // palette (shown as "symbol key", like the workspace list), and
        // the template choices are the three built-in starters.
        _avatarGlyphKeys.Add(null);
        ProfileGlyphCombo.Items.Add(Strings.Get("ui.no_icon"));
        foreach (var (key, symbol) in WorkspaceGlyphs.Palette)
        {
            _avatarGlyphKeys.Add(key);
            ProfileGlyphCombo.Items.Add($"{symbol} {key}");
        }
        foreach (var template in ProfileTemplates.All)
        {
            _templateKeys.Add(template.Key);
            ProfileTemplateCombo.Items.Add($"{template.Name} — {template.Description}");
        }
        if (ProfileTemplateCombo.Items.Count > 0)
            ProfileTemplateCombo.SelectedIndex = 0;

        // S2 (#16/#21): collision-policy and history-cap choices.
        CollisionPolicyCombo.Items.Add(Strings.Get("ui.save_as_a_new_name"));
        CollisionPolicyCombo.Items.Add(Strings.Get("ui.ask_where_to_save"));
        foreach (var cap in DownloadHistoryStore.AllowedCaps)
            HistoryCapCombo.Items.Add(cap.ToString());

        // R4 (#139): the auto-retry attempt choices, 1–5.
        for (var attempts = DownloadRetryPolicy.MinMaxAttempts;
             attempts <= DownloadRetryPolicy.MaxMaxAttempts; attempts++)
            RetryAttemptsCombo.Items.Add(attempts.ToString());

        // S8 #75: the per-profile data-cap choices (Off / 500 MB /
        // 1 GB / 2 GB), parallel to ProfileDataCaps.Allowed.
        foreach (var cap in ProfileDataCaps.Allowed)
            ProfileDataCapCombo.Items.Add(ProfileDataCaps.Label(cap));

        // R5B (#205): the reading-comfort choices, parallel to the
        // value tables below.
        foreach (var v in ChatTextScaleChoices)
            ChatTextScaleCombo.Items.Add($"{v}%");
        foreach (var v in ReadingColumnChoices)
            ReadingColumnCombo.Items.Add(v == 0 ? Strings.Get("ui.page_default") : $"{v} px");
        foreach (var v in LineSpacingChoices)
            LineSpacingCombo.Items.Add(v == 0 ? Strings.Get("ui.page_default") : $"{v}%");

        // S7 #61: the clear-data choices, titled by Core's plan model
        // so the UI and the confirm dialog always agree.
        foreach (var choice in ClearChoices)
            ClearChoiceCombo.Items.Add(Strings.Get(ClearChoiceKey(choice)));
        ClearScopeCombo.Items.Add(Strings.Get("ui.this_profile_only"));
        ClearScopeCombo.Items.Add(Strings.Get("ui.all_profiles"));
        ClearChoiceCombo.SelectedIndex = 2; // All site data — the pre-v0.11.0 behavior, one click away
        ClearScopeCombo.SelectedIndex = 0;

        // S4 (#34/#38): suspend-timeout and quiet-hours choices. The
        // quiet-hours combos offer whole hours only — minute precision
        // would be settings-file-only precision theatre.
        foreach (var option in SuspendTimeoutChoices)
            SuspendTimeoutCombo.Items.Add(Strings.Get(option.Label));
        // S9 (#83/#88): update frequency + channel choices.
        foreach (var option in UpdateFrequencyChoices)
            UpdateFrequencyCombo.Items.Add(Strings.Get(option.Label));
        foreach (var option in UpdateChannelChoices)
            UpdateChannelCombo.Items.Add(Strings.Get(option.Label));
        for (var hour = 0; hour < 24; hour++)
        {
            QuietStartCombo.Items.Add($"{hour:00}:00");
            QuietEndCombo.Items.Add($"{hour:00}:00");
        }

        LoadControls();
    }

    /// <summary>Clear-data choices in display order (S7 #61), parallel to ClearChoiceCombo's items.</summary>
    private static readonly ClearDataChoice[] ClearChoices =
    {
        ClearDataChoice.Cookies, ClearDataChoice.Cache, ClearDataChoice.AllSiteData,
    };

    /// <summary>Suspend-timeout choices in display order (S4 #34), parallel to SuspendTimeoutCombo's items.</summary>
    private static readonly (SuspendTimeout Value, string Label)[] SuspendTimeoutChoices =
    {
        (SuspendTimeout.OneMinute, "settings.suspend.1minute"),
        (SuspendTimeout.FiveMinutes, "settings.suspend.5minutes"),
        (SuspendTimeout.FifteenMinutes, "settings.suspend.15minutes"),
        (SuspendTimeout.Never, "settings.suspend.never"),
    };

    /// <summary>Update check-frequency choices in display order (S9 #83), parallel to UpdateFrequencyCombo's items.</summary>
    private static readonly (UpdateCheckFrequency Value, string Label)[] UpdateFrequencyChoices =
    {
        (UpdateCheckFrequency.EveryLaunch, "settings.update_freq.every_launch"),
        (UpdateCheckFrequency.Daily, "settings.update_freq.daily"),
        (UpdateCheckFrequency.Weekly, "settings.update_freq.weekly"),
        (UpdateCheckFrequency.ManualOnly, "settings.update_freq.manual"),
    };

    /// <summary>Update channel choices in display order (S9 #88), parallel to UpdateChannelCombo's items.</summary>
    private static readonly (UpdateChannel Value, string Label)[] UpdateChannelChoices =
    {
        (UpdateChannel.Stable, "settings.update_channel.stable"),
        (UpdateChannel.IncludePrereleases, "settings.update_channel.prereleases"),
    };

    /// <summary>Chat text-scale choices in percent (R5B #205), parallel to ChatTextScaleCombo's items.</summary>
    private static readonly int[] ChatTextScaleChoices = { 50, 60, 70, 80, 90, 100, 110, 125, 150, 175, 200 };

    /// <summary>Reading-column choices in pixels (R5B #205; 0 = page default), parallel to ReadingColumnCombo's items.</summary>
    private static readonly int[] ReadingColumnChoices = { 0, 560, 640, 720, 800, 960, 1200, 1600 };

    /// <summary>Line-spacing choices in percent (R5B #205; 0 = page default), parallel to LineSpacingCombo's items.</summary>
    private static readonly int[] LineSpacingChoices = { 0, 100, 110, 125, 140, 160, 180, 200 };

    /// <summary>(Re)fill every control from the working settings — at open and after an import.</summary>
    private void LoadControls()
    {
        // R5B #204: the drafts switch (write-through; the handler is
        // the harvest for this one).
        DraftsAutosaveCheck.IsChecked = _working.DraftsAutosaveEnabled;
        StartWithWindowsCheck.IsChecked = _working.StartWithWindows;
        StartMinimizedCheck.IsChecked = _working.StartMinimizedToTray;
        CloseToTrayCheck.IsChecked = _working.CloseToTray;
        SuspendInTrayCheck.IsChecked = _working.SuspendWhenInTray;
        var timeoutIndex = Array.FindIndex(SuspendTimeoutChoices, c => c.Value == _working.TraySuspendTimeout);
        SuspendTimeoutCombo.SelectedIndex = timeoutIndex >= 0 ? timeoutIndex : 1;
        TraySingleClickCheck.IsChecked = _working.TraySingleClickRestore;
        FlashOnAttentionCheck.IsChecked = _working.FlashTaskbarOnAttention;
        QuietHoursCheck.IsChecked = _working.QuietHoursEnabled;
        QuietStartCombo.SelectedIndex = Math.Clamp(_working.QuietHoursStartMinutes / 60, 0, 23);
        QuietEndCombo.SelectedIndex = Math.Clamp(_working.QuietHoursEndMinutes / 60, 0, 23);
        CheckUpdatesCheck.IsChecked = _working.CheckForUpdates;
        // S9: update frequency/channel, What's New opt-out, snooze state.
        var freqIndex = Array.FindIndex(UpdateFrequencyChoices, c => c.Value == _working.UpdateCheckFrequency);
        UpdateFrequencyCombo.SelectedIndex = freqIndex >= 0 ? freqIndex : 0;
        var channelIndex = Array.FindIndex(UpdateChannelChoices, c => c.Value == _working.UpdateChannel);
        UpdateChannelCombo.SelectedIndex = channelIndex >= 0 ? channelIndex : 0;
        ShowWhatsNewCheck.IsChecked = _working.ShowWhatsNew;
        RefreshUpdateSnoozeNote();
        AskWhereToSaveCheck.IsChecked = _working.AskWhereToSave;
        NotifyDownloadCompleteCheck.IsChecked = _working.NotifyWhenDownloadCompletes;
        CollisionPolicyCombo.SelectedIndex = _working.CollisionPolicy == DownloadCollisionPolicy.AskWhereToSave ? 1 : 0;
        HistoryCapCombo.SelectedItem = DownloadHistoryStore.SanitizeCap(_working.DownloadHistoryCap).ToString();
        DownloadsFolderText.Text = _working.DownloadsFolder ?? "";

        // R4: downloads round 2 controls.
        AutoSortDownloadsCheck.IsChecked = _working.AutoSortDownloads;
        SortCategoriesCheck.IsChecked = _working.DownloadSortUseCategories;
        SortRulesText.Text = DownloadSortRule.FormatLines(_working.DownloadSortRules);
        SortRulesNote.Text = "";
        BandwidthLimitText.Text = _working.DownloadBandwidthLimitKBps.ToString();
        DownloadQuietCheck.IsChecked = _working.DownloadQuietHoursEnabled;
        DownloadQuietStartText.Text = TimeOfDayText.Format(_working.DownloadQuietStartMinutes);
        DownloadQuietEndText.Text = TimeOfDayText.Format(_working.DownloadQuietEndMinutes);
        DownloadQuietLargeText.Text = _working.DownloadQuietLargeFileMB.ToString();
        DuplicateGuardCheck.IsChecked = _working.DownloadDuplicateGuard;
        AutoRetryCheck.IsChecked = _working.DownloadAutoRetryEnabled;
        RetryAttemptsCombo.SelectedItem =
            DownloadRetryPolicy.SanitizeMaxAttempts(_working.DownloadAutoRetryMaxAttempts).ToString();
        ArchiveFolderText.Text = _working.DownloadArchiveFolder ?? "";
        ArchiveDaysText.Text = _working.DownloadArchiveAfterDays.ToString();
        var activeProfile = _working.Profiles.FirstOrDefault(p => p.Id == _working.ActiveProfileId);
        ProfileQuotaText.Text = activeProfile is null
            ? "0"
            : (activeProfile.DownloadQuotaBytes / (1024d * 1024d)).ToString("0.##");

        // S6: extended zoom range (its handler rebuilds the step lists
        // when it changes), reduce motion, the shortcut editor rows.
        ExtendedZoomCheck.IsChecked = _working.ExtendedZoomRange;
        RebuildZoomCombos();
        ReduceMotionCombo.SelectedIndex = (int)_working.ReduceMotion;
        RebuildShortcutRows();

        // R7: language picker, announcement switches, text spacing.
        PopulateLanguageControls();

        // S11: the global hotkey editor (#96 — its status line reports
        // the LIVE registration, which the main window owns) and the
        // musedesktop:// links toggle (#99 — its status line reads the
        // actual registration, not the clone).
        RefreshGlobalHotkeyControls();
        ProtocolLinksCheck.IsChecked = _working.RegisterProtocolLinks;
        ProtocolStatus.Text = ProtocolRegistration.StatusText(_working);

        // R5: focus & productivity — timer shapes as plain numbers,
        // the quick-note hotkey editor (same capture pattern as S11),
        // and the scheduled-command list.
        FocusTimerAutoStartCheck.IsChecked = _working.FocusTimerAutoStart;
        FocusTimerMinutesText.Text = _working.FocusTimerMinutes.ToString();
        FocusBreakMinutesText.Text = _working.FocusBreakMinutes.ToString();
        FocusBreakRemindersCheck.IsChecked = _working.FocusBreakReminders;
        SessionWorkMinutesText.Text = _working.SessionWorkMinutes.ToString();
        SessionBreakMinutesText.Text = _working.SessionBreakMinutes.ToString();
        SessionLongBreakMinutesText.Text = _working.SessionLongBreakMinutes.ToString();
        SessionCyclesText.Text = _working.SessionCyclesBeforeLongBreak.ToString();
        RefreshQuickNoteHotkeyControls();
        RebuildScheduleRows();

        var current = ZoomMath.FormatPercent(_working.ZoomFactor);
        ZoomCombo.SelectedItem = ZoomCombo.Items.Contains(current) ? current : "100%";

        // S5: appearance controls + the toolbar editor + the data note.
        ThemeCombo.SelectedIndex = (int)_working.Theme;
        var accentIndex = _accentKeys.IndexOf(_working.AccentColor);
        AccentCombo.SelectedIndex = accentIndex >= 0 ? accentIndex : 0;
        TextScaleCombo.SelectedItem = ShellTextScale.FormatPercent(_working.ShellFontScale);
        // R6: appearance round 2 controls.
        ThemeLightFromText.Text = Core.ThemeScheduleMath.FormatMinutes(_working.ThemeLightStartMinutes);
        ThemeDarkFromText.Text = Core.ThemeScheduleMath.FormatMinutes(_working.ThemeDarkStartMinutes);
        CustomAccentCheck.IsChecked = _working.CustomAccent is not null;
        CustomAccentText.Text = _working.CustomAccent?.TrimStart('#') ?? "";
        RefreshCustomAccentSwatch();
        BackdropCombo.SelectedIndex = (int)_working.Backdrop;
        TitleBarTintCheck.IsChecked = _working.TitleBarProfileTint;
        DensityCombo.SelectedIndex = (int)_working.Density;
        TrayStyleCombo.SelectedIndex = (int)_working.TrayIconStyle;
        TrayOverlayCombo.SelectedIndex = (int)_working.TrayCountOverlay;
        RebuildToolbarEditor();
        PortableNote.Text = AppPaths.IsPortable
            ? Strings.Format("set.portable_on", AppPaths.RootDir)
            : Strings.Format("set.portable_off", AppPaths.RootDir);

        RefreshProfileCombo(_working.ActiveProfileId);

        // R2: workspaces & sessions — the management list plus the
        // auto-save opt-in (#118; harvested like the other plain
        // checkboxes, the workspace fields write through like the
        // profile fields do).
        AutoSaveSessionCheck.IsChecked = _working.AutoSaveSession;
        RefreshWorkspaceCombo(_working.ActiveWorkspaceId);

        // S7: privacy section state — the autofill toggles (#68), the
        // clear-data plan note (#61), the app lock (#64), the privacy
        // report (#67), and the two live reads (#62/#63, fire-and-
        // forget: they fill their rows when WebView2 answers).
        PasswordAutosaveCheck.IsChecked = _working.PasswordAutosaveEnabled;
        GeneralAutofillCheck.IsChecked = _working.GeneralAutofillEnabled;
        UpdateClearPlanNote();
        RefreshLockUi();
        RefreshPrivacyReport();
        _ = RefreshPermissionsAsync();
        _ = RefreshSiteDataAsync();
        RefreshBackupRows(); // S8 #79: the rotating-backup list

        // S5 #46: the Settings window's own text follows the scale
        // (re-applied after every LoadControls — the design sizes are
        // remembered, so this never compounds).
        ShellTextScaling.Apply(this, _working.ShellFontScale);
    }

    /// <summary>The snooze line + its cancel button (S9 #83): visible only while a snooze is actually in force.</summary>
    private void RefreshUpdateSnoozeNote()
    {
        if (_working.UpdateSnoozedUntilUtc is { } until && DateTimeOffset.UtcNow < until)
        {
            UpdateSnoozeNote.Text = UpdateSchedule.DescribeSnooze(until);
            StopSnoozeButton.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateSnoozeNote.Text = "";
            StopSnoozeButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnStopUpdateSnooze(object sender, RoutedEventArgs e)
    {
        _working.UpdateSnoozedUntilUtc = null;
        RefreshUpdateSnoozeNote();
    }

    /// <summary>Read the plain controls back into the working settings (OK and Export share this).</summary>
    private void HarvestControls()
    {
        _working.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _working.StartMinimizedToTray = StartMinimizedCheck.IsChecked == true;
        _working.CloseToTray = CloseToTrayCheck.IsChecked == true;
        _working.SuspendWhenInTray = SuspendInTrayCheck.IsChecked == true;
        if (SuspendTimeoutCombo.SelectedIndex >= 0 && SuspendTimeoutCombo.SelectedIndex < SuspendTimeoutChoices.Length)
            _working.TraySuspendTimeout = SuspendTimeoutChoices[SuspendTimeoutCombo.SelectedIndex].Value;
        _working.TraySingleClickRestore = TraySingleClickCheck.IsChecked == true;
        _working.FlashTaskbarOnAttention = FlashOnAttentionCheck.IsChecked == true;
        _working.QuietHoursEnabled = QuietHoursCheck.IsChecked == true;
        if (QuietStartCombo.SelectedIndex >= 0)
            _working.QuietHoursStartMinutes = QuietStartCombo.SelectedIndex * 60;
        if (QuietEndCombo.SelectedIndex >= 0)
            _working.QuietHoursEndMinutes = QuietEndCombo.SelectedIndex * 60;
        _working.CheckForUpdates = CheckUpdatesCheck.IsChecked == true;
        _working.AutoSaveSession = AutoSaveSessionCheck.IsChecked == true;
        // S9: frequency + channel + What's New (the snooze itself is
        // written by its own button / the update window, not harvested).
        if (UpdateFrequencyCombo.SelectedIndex >= 0 && UpdateFrequencyCombo.SelectedIndex < UpdateFrequencyChoices.Length)
            _working.UpdateCheckFrequency = UpdateFrequencyChoices[UpdateFrequencyCombo.SelectedIndex].Value;
        if (UpdateChannelCombo.SelectedIndex >= 0 && UpdateChannelCombo.SelectedIndex < UpdateChannelChoices.Length)
            _working.UpdateChannel = UpdateChannelChoices[UpdateChannelCombo.SelectedIndex].Value;
        _working.ShowWhatsNew = ShowWhatsNewCheck.IsChecked == true;
        // S11: links toggle (the hotkey's enabled box is harvested
        // with its editor below; its gesture is write-through in the
        // capture handler, like the shortcut overrides).
        _working.RegisterProtocolLinks = ProtocolLinksCheck.IsChecked == true;
        _working.GlobalHotkeyEnabled = GlobalHotkeyCheck.IsChecked == true;
        // R5: focus & productivity harvest — lenient like the R4
        // fields: a junk number keeps the previous (sanitized) value.
        // The schedules list edits the working list in place (its
        // Add/Edit/Remove handlers), so there is nothing to harvest;
        // the quick-note gesture is write-through in its capture
        // handler, like the show/hide gesture.
        _working.FocusTimerAutoStart = FocusTimerAutoStartCheck.IsChecked == true;
        if (int.TryParse(FocusTimerMinutesText.Text, out var focusMinutes))
            _working.FocusTimerMinutes = focusMinutes;
        if (int.TryParse(FocusBreakMinutesText.Text, out var focusBreak))
            _working.FocusBreakMinutes = focusBreak;
        _working.FocusBreakReminders = FocusBreakRemindersCheck.IsChecked == true;
        if (int.TryParse(SessionWorkMinutesText.Text, out var workMinutes))
            _working.SessionWorkMinutes = workMinutes;
        if (int.TryParse(SessionBreakMinutesText.Text, out var breakMinutes))
            _working.SessionBreakMinutes = breakMinutes;
        if (int.TryParse(SessionLongBreakMinutesText.Text, out var longBreakMinutes))
            _working.SessionLongBreakMinutes = longBreakMinutes;
        if (int.TryParse(SessionCyclesText.Text, out var cycles))
            _working.SessionCyclesBeforeLongBreak = cycles;
        _working.QuickNoteHotkeyEnabled = QuickNoteHotkeyCheck.IsChecked == true;
        _working.AskWhereToSave = AskWhereToSaveCheck.IsChecked == true;
        _working.NotifyWhenDownloadCompletes = NotifyDownloadCompleteCheck.IsChecked == true;
        _working.CollisionPolicy = CollisionPolicyCombo.SelectedIndex == 1
            ? DownloadCollisionPolicy.AskWhereToSave
            : DownloadCollisionPolicy.AutoRename;
        if (HistoryCapCombo.SelectedItem is string capText && int.TryParse(capText, out var cap))
            _working.DownloadHistoryCap = cap;
        _working.DownloadsFolder = string.IsNullOrWhiteSpace(DownloadsFolderText.Text)
            ? null
            : DownloadsFolderText.Text.Trim();
        // R4: downloads round 2 harvest. Every field parses leniently —
        // a junk value keeps the previous (sanitized) one, never a crash.
        _working.AutoSortDownloads = AutoSortDownloadsCheck.IsChecked == true;
        _working.DownloadSortUseCategories = SortCategoriesCheck.IsChecked == true;
        _working.DownloadSortRules = DownloadSortRule.ParseLines(SortRulesText.Text, out var ruleErrors);
        SortRulesNote.Text = ruleErrors.Count > 0 ? Strings.Format("set.sort_rule_problem", ruleErrors[0]) : "";
        if (int.TryParse(BandwidthLimitText.Text, out var kbps))
            _working.DownloadBandwidthLimitKBps = kbps;
        _working.DownloadQuietHoursEnabled = DownloadQuietCheck.IsChecked == true;
        if (TimeOfDayText.TryParseMinutes(DownloadQuietStartText.Text, out var quietStart))
            _working.DownloadQuietStartMinutes = quietStart;
        if (TimeOfDayText.TryParseMinutes(DownloadQuietEndText.Text, out var quietEnd))
            _working.DownloadQuietEndMinutes = quietEnd;
        if (int.TryParse(DownloadQuietLargeText.Text, out var largeMb))
            _working.DownloadQuietLargeFileMB = largeMb;
        _working.DownloadDuplicateGuard = DuplicateGuardCheck.IsChecked == true;
        _working.DownloadAutoRetryEnabled = AutoRetryCheck.IsChecked == true;
        if (RetryAttemptsCombo.SelectedItem is string attemptsText && int.TryParse(attemptsText, out var attempts))
            _working.DownloadAutoRetryMaxAttempts = attempts;
        _working.DownloadArchiveFolder = string.IsNullOrWhiteSpace(ArchiveFolderText.Text)
            ? null
            : ArchiveFolderText.Text.Trim();
        if (int.TryParse(ArchiveDaysText.Text, out var archiveDays))
            _working.DownloadArchiveAfterDays = archiveDays;
        var quotaProfile = _working.Profiles.FirstOrDefault(p => p.Id == _working.ActiveProfileId);
        if (quotaProfile is not null &&
            double.TryParse(ProfileQuotaText.Text, out var quotaMb) && quotaMb >= 0)
            quotaProfile.DownloadQuotaBytes = (long)(quotaMb * 1024 * 1024);
        if (ZoomCombo.SelectedItem is string pct &&
            int.TryParse(pct.TrimEnd('%'), out var value))
        {
            _working.ZoomFactor = value / 100.0;
        }
        // S6: reduce motion + extended zoom (shortcut overrides are
        // write-through in the editor handlers, like the toolbar).
        _working.ExtendedZoomRange = ExtendedZoomCheck.IsChecked == true;
        _working.ReduceMotion = ReduceMotionCombo.SelectedIndex switch
        {
            1 => ReduceMotionSetting.On,
            2 => ReduceMotionSetting.Off,
            _ => ReduceMotionSetting.FollowSystem,
        };
        // R7: language (the combo's parallel code list; index 0 =
        // follow Windows), the four announcement switches (folded
        // into the OFF-exceptions map), and the spacing scale.
        if (LanguageCombo.SelectedIndex >= 0 && LanguageCombo.SelectedIndex < _languageCodes.Count)
            _working.UiLanguage = _languageCodes[LanguageCombo.SelectedIndex];
        var switches = AnnouncementSwitches.With(_working.AnnouncementKindEnabled,
            AnnouncementKind.Downloads, AnnouncementDownloadsCheck.IsChecked == true);
        switches = AnnouncementSwitches.With(switches,
            AnnouncementKind.Updates, AnnouncementUpdatesCheck.IsChecked == true);
        switches = AnnouncementSwitches.With(switches,
            AnnouncementKind.Timers, AnnouncementTimersCheck.IsChecked == true);
        switches = AnnouncementSwitches.With(switches,
            AnnouncementKind.Focus, AnnouncementFocusCheck.IsChecked == true);
        _working.AnnouncementKindEnabled = switches;
        if (TextSpacingCombo.SelectedIndex >= 0 && TextSpacingCombo.SelectedIndex < ShellTextSpacing.Options.Count)
            _working.ShellTextSpacingScale = ShellTextSpacing.Options[TextSpacingCombo.SelectedIndex];
        // S5 appearance (the toolbar layout itself is write-through —
        // see the toolbar editor handlers).
        if (ThemeCombo.SelectedIndex >= 0 && ThemeCombo.SelectedIndex <= (int)Core.ThemeMode.FollowSchedule)
            _working.Theme = (Core.ThemeMode)ThemeCombo.SelectedIndex;
        // R6 appearance round 2. Times that don't parse keep the
        // working values (the fields re-populate with the kept times
        // next open) — a typo must not silently reset the schedule.
        if (Core.ThemeScheduleMath.TryParseTime(ThemeLightFromText.Text, out var lightFrom))
            _working.ThemeLightStartMinutes = lightFrom;
        if (Core.ThemeScheduleMath.TryParseTime(ThemeDarkFromText.Text, out var darkFrom))
            _working.ThemeDarkStartMinutes = darkFrom;
        _working.CustomAccent = CustomAccentCheck.IsChecked == true
            ? Core.CustomAccent.TryNormalize(CustomAccentText.Text)
            : null;
        if (BackdropCombo.SelectedIndex >= 0)
            _working.Backdrop = (Core.BackdropPreference)BackdropCombo.SelectedIndex;
        _working.TitleBarProfileTint = TitleBarTintCheck.IsChecked == true;
        if (DensityCombo.SelectedIndex >= 0)
            _working.Density = (Core.ShellDensity)DensityCombo.SelectedIndex;
        if (TrayStyleCombo.SelectedIndex >= 0)
            _working.TrayIconStyle = (Core.TrayIconStyle)TrayStyleCombo.SelectedIndex;
        if (TrayOverlayCombo.SelectedIndex >= 0)
            _working.TrayCountOverlay = (Core.TrayOverlayStyle)TrayOverlayCombo.SelectedIndex;
        if (AccentCombo.SelectedIndex >= 0 && AccentCombo.SelectedIndex < _accentKeys.Count)
            _working.AccentColor = _accentKeys[AccentCombo.SelectedIndex];
        if (TextScaleCombo.SelectedItem is string scaleText &&
            int.TryParse(scaleText.TrimEnd('%'), out var scaleValue))
        {
            _working.ShellFontScale = scaleValue / 100.0;
        }
        // S7: the autofill toggles (#68) and the app lock's tray-hide
        // switch (#64). The lock's on/off comes from its checkbox too,
        // but the credential fields are write-through from the lock
        // dialogs — and the switch only counts with a record present
        // (AppLock.IsLockActive enforces the same rule everywhere).
        _working.PasswordAutosaveEnabled = PasswordAutosaveCheck.IsChecked == true;
        _working.GeneralAutofillEnabled = GeneralAutofillCheck.IsChecked == true;
        _working.AppLockEnabled = AppLockEnabledCheck.IsChecked == true && AppLock.HasValidRecord(_working);
        _working.AppLockOnTrayHide = AppLockTrayHideCheck.IsChecked == true;
        // S5 #48: remember where Settings was left (size + section).
        if (ActualWidth > 0)
            _working.SettingsWindowWidth = ActualWidth;
        if (ActualHeight > 0)
            _working.SettingsWindowHeight = ActualHeight;
        if (_currentSectionId is not null)
            _working.SettingsLastSection = _currentSectionId;
    }

    // ───────────────────────── profiles (v0.3.0) ─────────────────────────

    private AccountProfile? SelectedProfile =>
        ProfileCombo.SelectedItem is AccountProfile p ? p : null;

    private void RefreshProfileCombo(string selectId)
    {
        ProfileCombo.ItemsSource = null;
        ProfileCombo.ItemsSource = _working.Profiles;
        // No DisplayMemberPath here: the combo's ItemTemplate (color dot +
        // name, R3) already renders each profile, and WPF throws
        // InvalidOperationException when both are set — the v1.7.0 field
        // crash on opening Settings. Guarded by an R7 test.
        ProfileCombo.SelectedItem = _working.Profiles.FirstOrDefault(p => p.Id == selectId)
                                    ?? _working.Profiles.FirstOrDefault();
        RefreshCopyFromCombo();
        UpdateProfileButtons();
    }

    /// <summary>Rebuild the "copy settings from" choices (S3 #29), keeping the current pick by profile id.</summary>
    private void RefreshCopyFromCombo()
    {
        _copyFromIds.Clear();
        _copyFromIds.Add(null);
        var items = new List<string> { Strings.Get("set.dont_copy") };
        foreach (var p in _working.Profiles)
        {
            _copyFromIds.Add(p.Id);
            items.Add(p.Name);
        }
        CopyFromCombo.ItemsSource = items;
        var keep = _copyFromId is null ? 0 : _copyFromIds.IndexOf(_copyFromId);
        CopyFromCombo.SelectedIndex = keep >= 0 ? keep : 0;
    }

    private void OnCopyFromChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var idx = CopyFromCombo.SelectedIndex;
        _copyFromId = idx >= 0 && idx < _copyFromIds.Count ? _copyFromIds[idx] : null;
    }

    private void UpdateProfileButtons()
    {
        var selected = SelectedProfile;
        SwitchProfileButton.IsEnabled = selected is not null && selected.Id != _working.ActiveProfileId;
        RemoveProfileButton.IsEnabled = selected is not null
                                         && selected.Id != _working.ActiveProfileId
                                         && _working.Profiles.Count > 1;
        CreateShortcutButton.IsEnabled = selected is not null;
        // S5 #47: the 3-way start-page choice (replaces the old
        // StartAtHome checkbox; EnsureValid keeps the bool in sync).
        _syncingStartPage = true;
        ProfileStartLastRadio.IsChecked = selected?.StartPage == StartPageMode.LastPage;
        ProfileStartHomeRadio.IsChecked = selected?.StartPage == StartPageMode.Home;
        ProfileStartCustomRadio.IsChecked = selected?.StartPage == StartPageMode.Custom;
        ProfileCustomUrlText.Text = selected?.CustomStartUrl ?? "";
        _syncingStartPage = false;
        ProfileStartLastRadio.IsEnabled = selected is not null;
        ProfileStartHomeRadio.IsEnabled = selected is not null;
        ProfileStartCustomRadio.IsEnabled = selected is not null;
        UpdateStartPageControls();
        // S2 #20: the selected profile's downloads-folder override.
        ProfileDownloadsFolderText.Text = selected?.DownloadsFolder ?? "";
        BrowseProfileFolderButton.IsEnabled = selected is not null;
        ClearProfileFolderButton.IsEnabled = selected is not null && !string.IsNullOrWhiteSpace(selected.DownloadsFolder);
        // S3 #24/#26/#27: name, color, per-profile zoom, data facts.
        _syncingProfileControls = true;
        ProfileNameText.Text = selected?.Name ?? "";
        ProfileNameText.IsEnabled = selected is not null;
        var colorIndex = selected?.DisplayColor is { } color ? _colorKeys.IndexOf(color) : 0;
        ProfileColorCombo.SelectedIndex = colorIndex >= 0 ? colorIndex : 0;
        ProfileColorCombo.IsEnabled = selected is not null;
        ProfileZoomCheck.IsChecked = selected?.ZoomFactor is not null;
        ProfileZoomCheck.IsEnabled = selected is not null;
        var zoomShown = selected?.ZoomFactor ?? _working.ZoomFactor;
        ProfileZoomCombo.SelectedItem = ZoomMath.FormatPercent(zoomShown);
        ProfileZoomCombo.IsEnabled = selected is not null && selected.ZoomFactor is not null;
        // S8 #75: the data cap rides the same sync guard.
        ProfileDataCapCombo.SelectedItem = ProfileDataCaps.Label(selected?.DataCapBytes ?? 0);
        ProfileDataCapCombo.IsEnabled = selected is not null;
        // R5B #201/#205: the conversation-view controls ride it too.
        // A stored value between steps (hand-edited file) selects
        // nothing — the value itself stays until a step is picked.
        ProfileScrollbarsCheck.IsChecked = selected?.ConversationScrollbars ?? true;
        ProfileScrollbarsCheck.IsEnabled = selected is not null;
        ChatTextScaleCombo.SelectedIndex = selected is null
            ? -1 : Array.IndexOf(ChatTextScaleChoices, selected.ChatTextScalePercent);
        ReadingColumnCombo.SelectedIndex = selected is null
            ? -1 : Array.IndexOf(ReadingColumnChoices, selected.ReadingColumnWidthPx);
        LineSpacingCombo.SelectedIndex = selected is null
            ? -1 : Array.IndexOf(LineSpacingChoices, selected.ChatLineSpacingPercent);
        ChatTextScaleCombo.IsEnabled = ReadingColumnCombo.IsEnabled
            = LineSpacingCombo.IsEnabled = selected is not null;
        _syncingProfileControls = false;
        ProfileDataText.Text = selected is null
            ? ""
            : ProfileDataInfo.Describe(AppPaths.ProfileDir(selected.Id), selected);
        ProfileCapNote.Text = selected is null ? "" : DescribeCap(selected);
        UpdateProfileRound2Controls(selected);
        // R5B #206: the snippet editor follows the selected profile.
        RefreshSnippetList();
    }

    /// <summary>The #75 cap line for the selected profile: the cap, the promise, and the last cleanup (never silent).</summary>
    private static string DescribeCap(AccountProfile profile)
    {
        var cleaned = profile.LastCacheCleanupUtc is { } utc
            ? Strings.Format("settings.cap_last_cleanup", utc.LocalDateTime,
                DownloadText.FormatBytes(profile.LastCacheCleanupFreedBytes))
            : "";
        return profile.DataCapBytes <= 0
            ? Strings.Get("settings.cap_none") + cleaned
            : Strings.Format("settings.cap_over", ProfileDataCaps.Label(profile.DataCapBytes)) + cleaned;
    }

    private void OnProfileDataCapChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingProfileControls)
            return;
        var selected = SelectedProfile;
        var index = ProfileDataCapCombo.SelectedIndex;
        if (selected is null || index < 0 || index >= ProfileDataCaps.Allowed.Count)
            return;
        selected.DataCapBytes = ProfileDataCaps.Allowed[index];
        ProfileCapNote.Text = DescribeCap(selected);
        _log.Info($"Profile {selected.Id} data cap set to {ProfileDataCaps.Label(selected.DataCapBytes)}");
    }

    private void OnBrowseProfileDownloadsFolder(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } selected)
            return;
        var dialog = new OpenFolderDialog
        {
            Title = Strings.Format("set.profile_downloads_title", selected.Name),
            InitialDirectory = Directory.Exists(ProfileDownloadsFolderText.Text)
                ? ProfileDownloadsFolderText.Text
                : AppPaths.DownloadsHint,
        };
        var invoker = FocusReturn.Capture();
        var chosen = dialog.ShowDialog(this) == true;
        FocusReturn.Restore(invoker);
        if (chosen)
        {
            selected.DownloadsFolder = dialog.FolderName;
            ProfileDownloadsFolderText.Text = dialog.FolderName;
            ClearProfileFolderButton.IsEnabled = true;
        }
    }

    private void OnClearProfileDownloadsFolder(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } selected)
            return;
        selected.DownloadsFolder = null;
        ProfileDownloadsFolderText.Text = "";
        ClearProfileFolderButton.IsEnabled = false;
    }

    // ── S5 #47: start-page write-through, like the other per-profile controls ──
    private bool _syncingStartPage;

    private void OnProfileStartModeChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingStartPage)
            return;
        if (SelectedProfile is not { } selected)
            return;
        selected.StartPage = ProfileStartCustomRadio.IsChecked == true
            ? StartPageMode.Custom
            : ProfileStartHomeRadio.IsChecked == true
                ? StartPageMode.Home
                : StartPageMode.LastPage;
        selected.StartAtHome = selected.StartPage == StartPageMode.Home;
        UpdateStartPageControls();
    }

    private void OnProfileCustomUrlLostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } selected)
            return;
        var text = ProfileCustomUrlText.Text.Trim();
        selected.CustomStartUrl = text.Length == 0 ? null : text;
        UpdateStartPageControls();
    }

    /// <summary>The custom-URL box is editable only in Custom mode, and its note always says plainly whether the address will be used or will fall back to Home.</summary>
    private void UpdateStartPageControls()
    {
        var selected = SelectedProfile;
        ProfileCustomUrlText.IsEnabled = selected is not null && selected.StartPage == StartPageMode.Custom;
        ProfileCustomUrlNote.Text = selected is null || selected.StartPage != StartPageMode.Custom
            ? ""
            : StartPagePolicy.IsValidCustomUrl(selected.CustomStartUrl)
                ? Strings.Get("set.startpage_ok")
                : Strings.Get("set.startpage_bad");
    }

    // ── S3: color palette keys parallel to ProfileColorCombo items ──
    private readonly List<string?> _colorKeys = new();
    private readonly List<string?> _copyFromIds = new();
    private string? _copyFromId;
    private bool _syncingProfileControls;

    // ── R3: avatar glyph keys parallel to ProfileGlyphCombo items
    // (null = "No icon", then the workspace-glyph palette keys — the
    // same list the workspace section builds), group ids parallel to
    // ProfileGroupCombo items (null = "No group"), and template keys
    // parallel to ProfileTemplateCombo items. ──
    private readonly List<string?> _avatarGlyphKeys = new();
    private readonly List<string?> _groupIds = new();
    private readonly List<string> _templateKeys = new();

    /// <summary>Rename (S3 #24): write-through on focus loss, like the other per-profile controls.</summary>
    private void OnProfileNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } selected)
            return;
        var name = ProfileNameText.Text.Trim();
        if (name.Length == 0)
        {
            ProfileNameText.Text = selected.Name; // a profile always has a name
            return;
        }
        if (name.Length > 40)
            name = name[..40];
        if (name != selected.Name)
        {
            selected.Name = name;
            RefreshProfileCombo(selected.Id); // refresh the list label
        }
    }

    private void OnProfileColorChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingProfileControls)
            return;
        if (SelectedProfile is { } selected)
        {
            var idx = ProfileColorCombo.SelectedIndex;
            selected.DisplayColor = idx >= 0 && idx < _colorKeys.Count ? _colorKeys[idx] : null;
            RefreshProfileCombo(selected.Id); // refresh the dot in the list
        }
    }

    private void OnProfileZoomChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingProfileControls)
            return;
        if (SelectedProfile is not { } selected)
            return;
        if (ProfileZoomCheck.IsChecked == true)
        {
            selected.ZoomFactor = ProfileZoomCombo.SelectedItem is string pct
                                  && int.TryParse(pct.TrimEnd('%'), out var value)
                ? value / 100.0
                : _working.ZoomFactor;
        }
        else
        {
            selected.ZoomFactor = null;
        }
        UpdateProfileButtons();
    }

    private void OnProfileZoomComboChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingProfileControls)
            return;
        if (SelectedProfile is { } selected && selected.ZoomFactor is not null
            && ProfileZoomCombo.SelectedItem is string pct
            && int.TryParse(pct.TrimEnd('%'), out var value))
        {
            selected.ZoomFactor = value / 100.0;
        }
    }

    /// <summary>The conversation-view controls (R5B #201/#205): write-through to the selected profile, like the zoom controls; the page picks the values up when Settings closes with OK.</summary>
    private void OnProfileViewChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingProfileControls)
            return;
        if (SelectedProfile is not { } selected)
            return;
        selected.ConversationScrollbars = ProfileScrollbarsCheck.IsChecked == true;
        if (ChatTextScaleCombo.SelectedIndex >= 0 && ChatTextScaleCombo.SelectedIndex < ChatTextScaleChoices.Length)
            selected.ChatTextScalePercent = ChatTextScaleChoices[ChatTextScaleCombo.SelectedIndex];
        if (ReadingColumnCombo.SelectedIndex >= 0 && ReadingColumnCombo.SelectedIndex < ReadingColumnChoices.Length)
            selected.ReadingColumnWidthPx = ReadingColumnChoices[ReadingColumnCombo.SelectedIndex];
        if (LineSpacingCombo.SelectedIndex >= 0 && LineSpacingCombo.SelectedIndex < LineSpacingChoices.Length)
            selected.ChatLineSpacingPercent = LineSpacingChoices[LineSpacingCombo.SelectedIndex];
    }

    /// <summary>The drafts switch (R5B #204): write-through to the working settings — Cancel in Settings still discards it, like every preference here.</summary>
    private void OnDraftsAutosaveChanged(object sender, RoutedEventArgs e) =>
        _working.DraftsAutosaveEnabled = DraftsAutosaveCheck.IsChecked == true;

    // ── prompt snippets (R5B #206) ──────────────────────────────────
    // Snippets live in their OWN store (snippets.json), not in the
    // settings clone — so these edits are LIVE, like the Privacy
    // section's actions: they save as they're made and Cancel in
    // Settings does not undo them. The status line says so by
    // reporting each save as it happens.

    /// <summary>The snippet currently loaded in the editor, if any.</summary>
    private string? _selectedSnippetId;

    /// <summary>Reload the selected profile's snippet list from the store, keeping the editor's selection where it still exists.</summary>
    private void RefreshSnippetList()
    {
        var profileId = SelectedProfile?.Id;
        IReadOnlyList<PromptSnippet> snippets = profileId is null
            ? Array.Empty<PromptSnippet>()
            : PromptSnippetStore.Load(AppPaths.SnippetsPath).EntriesFor(profileId);
        SnippetList.ItemsSource = snippets;
        SnippetList.DisplayMemberPath = nameof(PromptSnippet.Name);
        var keep = _selectedSnippetId is null
            ? null
            : snippets.FirstOrDefault(s => s.Id == _selectedSnippetId);
        SnippetList.SelectedItem = keep;
        if (keep is null && _selectedSnippetId is not null)
        {
            _selectedSnippetId = null;
            SnippetNameText.Text = "";
            SnippetBodyText.Text = "";
        }
        var enabled = profileId is not null;
        SnippetList.IsEnabled = SnippetNameText.IsEnabled = SnippetBodyText.IsEnabled
            = SaveSnippetButton.IsEnabled = NewSnippetButton.IsEnabled = enabled;
        DeleteSnippetButton.IsEnabled = enabled && keep is not null;
    }

    private void OnSnippetSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SnippetList.SelectedItem is not PromptSnippet snippet)
            return; // losing the selection (a refresh) never wipes the editor
        _selectedSnippetId = snippet.Id;
        SnippetNameText.Text = snippet.Name;
        SnippetBodyText.Text = snippet.Body;
        DeleteSnippetButton.IsEnabled = true;
        SnippetStatus.Text = "";
    }

    private void OnSaveSnippet(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } selected)
            return;
        var all = PromptSnippetStore.Load(AppPaths.SnippetsPath);
        var now = DateTimeOffset.UtcNow;
        PromptSnippet? added = null;
        var ok = _selectedSnippetId is { } id && all.Find(selected.Id, id) is not null
            ? all.Update(selected.Id, id, SnippetNameText.Text, SnippetBodyText.Text, now)
            : (added = all.Add(selected.Id, SnippetNameText.Text, SnippetBodyText.Text, now)) is not null;
        if (!ok)
        {
            SnippetStatus.Text = PromptSnippets.CleanName(SnippetNameText.Text) is null
                ? Strings.Get("set.snippet_name")
                : Strings.Format("set.snippet_text", PromptSnippets.MaxBodyLength);
            return;
        }
        PromptSnippetStore.Save(AppPaths.SnippetsPath, all);
        if (added is not null)
            _selectedSnippetId = added.Id;
        SnippetStatus.Text = Strings.Get("ui.snippet_saved_it_s_in_ctrl_k_and_the");
        RefreshSnippetList();
    }

    private void OnNewSnippet(object sender, RoutedEventArgs e)
    {
        _selectedSnippetId = null;
        SnippetList.SelectedItem = null;
        SnippetNameText.Text = "";
        SnippetBodyText.Text = "";
        SnippetStatus.Text = "";
        SnippetNameText.Focus();
    }

    private void OnDeleteSnippet(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } selected || _selectedSnippetId is not { } id)
            return;
        var all = PromptSnippetStore.Load(AppPaths.SnippetsPath);
        if (all.Remove(selected.Id, id))
        {
            PromptSnippetStore.Save(AppPaths.SnippetsPath, all);
            _selectedSnippetId = null;
            SnippetNameText.Text = "";
            SnippetBodyText.Text = "";
            SnippetStatus.Text = Strings.Get("ui.snippet_deleted");
        }
        RefreshSnippetList();
    }

    /// <summary>Per-profile desktop shortcut (S3 #23).</summary>
    private void OnCreateProfileShortcut(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } selected)
            return;
        var error = ProfileShortcut.CreateOnDesktop(selected, _log);
        ProfileNote.Text = error ?? Strings.Format("set.shortcut_created", Profiles.ShortcutFileName(selected));
    }

    private void OnProfileSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateProfileButtons();

    private void OnAddProfile(object sender, RoutedEventArgs e)
    {
        if (_working.Profiles.Count >= Profiles.MaxProfiles)
        {
            var capInvoker = FocusReturn.Capture();
            System.Windows.MessageBox.Show(this,
                Strings.Format("set.profiles_cap", Profiles.MaxProfiles),
                Strings.Get("set.profiles_caption"), MessageBoxButton.OK, MessageBoxImage.Information);
            FocusReturn.Restore(capInvoker);
            return;
        }
        // S3 #30: past the soft threshold, adding is a deliberate choice —
        // every profile keeps its own sign-in data on this PC.
        if (_working.Profiles.Count >= Profiles.SoftProfileWarningThreshold)
        {
            var warnInvoker = FocusReturn.Capture();
            var warn = System.Windows.MessageBox.Show(this,
                Strings.Format("set.profiles_warn", _working.Profiles.Count),
                Strings.Get("set.profiles_caption"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (warn != MessageBoxResult.Yes)
            {
                FocusReturn.Restore(warnInvoker);
                return;
            }
        }
        var name = string.IsNullOrWhiteSpace(NewProfileNameText.Text)
            ? Strings.Format("set.profile_default_name", _working.Profiles.Count + 1)
            : NewProfileNameText.Text.Trim();
        // S3 #29: optionally seed the new profile's SETTINGS from an
        // existing one — never its sign-in or session state.
        var source = _copyFromId is null
            ? null
            : _working.Profiles.FirstOrDefault(p => p.Id == _copyFromId);
        var profile = source is null
            ? new AccountProfile { Id = Profiles.NewId(), Name = name }
            : Profiles.CopySettingsFrom(source, Profiles.NewId(), name);
        _working.Profiles.Add(profile);
        NewProfileNameText.Text = "";
        RefreshProfileCombo(profile.Id);
    }

    private void OnRemoveProfile(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProfile;
        if (selected is null || selected.Id == _working.ActiveProfileId)
            return;
        _working.Profiles.Remove(selected);
        RefreshProfileCombo(_working.ActiveProfileId);
    }

    private void OnSwitchProfile(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProfile;
        if (selected is null || selected.Id == _working.ActiveProfileId)
            return;
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Format("set.switch_body", selected.Name),
            Strings.Get("msg.switch_profile"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        _switchProfile(_working, selected.Id);
        // The app is exiting for the restart; this window goes with it.
    }

    // ─────────────────── R3: profiles round 2 (#121–#130) ───────────────────

    /// <summary>The schedule day checkboxes in Mon..Sun order (their masks are ProfileSchedule.MaskFor).</summary>
    private System.Windows.Controls.CheckBox[] ScheduleDayChecks() => new[]
    {
        SchedMon, SchedTue, SchedWed, SchedThu, SchedFri, SchedSat, SchedSun,
    };

    private static readonly DayOfWeek[] ScheduleDayOrder =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    };

    /// <summary>
    /// Fill every R3 control from the selected profile + working
    /// settings (called at the end of UpdateProfileButtons). All writes
    /// run under the profile sync guard so the change handlers below
    /// never write back.
    /// </summary>
    private void UpdateProfileRound2Controls(AccountProfile? selected)
    {
        _syncingProfileControls = true;
        // #121 avatar.
        var glyphIndex = selected?.AvatarGlyph is { } glyph ? _avatarGlyphKeys.IndexOf(glyph) : 0;
        ProfileGlyphCombo.SelectedIndex = glyphIndex >= 0 ? glyphIndex : 0;
        ProfileGlyphCombo.IsEnabled = selected is not null;
        // #127 group membership + the group list itself.
        RefreshProfileGroupCombo(selected);
        // #130 archive: the profile in use can't be archived (its
        // checkbox is disabled; Core un-archives it as a fail-safe too).
        ProfileArchivedCheck.IsChecked = selected?.Archived == true;
        ProfileArchivedCheck.IsEnabled = selected is not null && selected.Id != _working.ActiveProfileId;
        // #122 PIN status.
        var hasPin = selected is not null && ProfileLock.HasValidRecord(selected);
        ProfilePinStatus.Text = selected is null ? ""
            : hasPin && selected.LockEnabled ? Strings.Get("set.pin_on")
            : hasPin ? Strings.Get("set.pin_set_off")
            : Strings.Get("set.pin_off");
        SetProfilePinButton.Content = hasPin ? Strings.Get("set.change_pin") : Strings.Get("set_pin");
        SetProfilePinButton.IsEnabled = selected is not null;
        RemoveProfilePinButton.IsEnabled = hasPin;
        // #123 schedule editor.
        var rule = selected is null ? null : ProfileSchedule.RuleFor(_working, selected.Id);
        ProfileScheduleCheck.IsChecked = rule is not null;
        ProfileScheduleCheck.IsEnabled = selected is not null;
        ProfileScheduleStartText.Text = ProfileSchedule.FormatMinutes(rule?.StartMinutes ?? 9 * 60);
        ProfileScheduleEndText.Text = ProfileSchedule.FormatMinutes(rule?.EndMinutes ?? 17 * 60);
        var checks = ScheduleDayChecks();
        // No rule yet: the day boxes pre-show Weekdays, matching the
        // 09:00–17:00 defaults above — ticking the schedule box then
        // creates that canonical rule in one click.
        var shownMask = rule?.DaysMask ?? ProfileSchedule.WeekdaysMask;
        for (var i = 0; i < checks.Length; i++)
            checks[i].IsChecked = (shownMask & ProfileSchedule.MaskFor(ScheduleDayOrder[i])) != 0;
        UpdateScheduleControlsEnabled(selected, rule is not null);
        ProfileScheduleText.Text = selected is null ? ""
            : rule is not null
                ? Strings.Format("set.schedule_line", ProfileSchedule.Describe(rule), selected.Archived ? Strings.Get("set.schedule_archived") : _working.ProfileScheduleAutoSwitch ? Strings.Get("set.schedule_auto") : Strings.Get("set.schedule_ask"))
                : Strings.Get("set.schedule_none");
        ScheduleAutoCheck.IsChecked = _working.ProfileScheduleAutoSwitch;
        _syncingProfileControls = false;
        // Health is a snapshot, not a live value: changing the
        // selection clears it rather than showing another profile's.
        ProfileHealthText.Text = "";
        RefreshProfileUsageText(selected);
        CloneProfileButton.IsEnabled = selected is not null;
        ExportProfileButton.IsEnabled = selected is not null;
    }

    /// <summary>Enable/disable the schedule time + day editors with the schedule switch.</summary>
    private void UpdateScheduleControlsEnabled(AccountProfile? selected, bool on)
    {
        var enabled = selected is not null && on;
        ProfileScheduleStartText.IsEnabled = enabled;
        ProfileScheduleEndText.IsEnabled = enabled;
        foreach (var check in ScheduleDayChecks())
            check.IsEnabled = enabled;
    }

    /// <summary>The #129 usage line for the selected profile plus the all-profiles total (counts read fresh from the local tally file).</summary>
    private void RefreshProfileUsageText(AccountProfile? selected)
    {
        var usage = ProfileUsageStore.Load(AppPaths.ProfileUsagePath);
        var report = ProfileUsageReport.Build(_working, usage);
        if (selected is null)
        {
            ProfileUsageText.Text = "";
            return;
        }
        var row = report.Rows.FirstOrDefault(r => r.ProfileId == selected.Id);
        var mine = row is null || row.Launches == 0
            ? Strings.Get("set.usage_never")
            : Strings.Format(row.Launches == 1 ? "set.usage_launch_one" : "set.usage_launch_many", row.Launches)
              + (row.LastUsedUtc is { } used ? Strings.Format("set.usage_last", used.ToLocalTime()) : "");
        ProfileUsageText.Text =
            Strings.Format("set.usage_line", selected.Name, mine, report.TotalLaunches) +
            Strings.Get("set.usage_note");
    }

    // #121: avatar glyph write-through.
    private void OnProfileGlyphChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingProfileControls)
            return;
        if (SelectedProfile is { } selected)
        {
            var idx = ProfileGlyphCombo.SelectedIndex;
            selected.AvatarGlyph = idx >= 0 && idx < _avatarGlyphKeys.Count ? _avatarGlyphKeys[idx] : null;
        }
    }

    /// <summary>Rebuild the group combo (R3 #127): "No group" then the working group list, selecting the profile's group.</summary>
    private void RefreshProfileGroupCombo(AccountProfile? selected)
    {
        _groupIds.Clear();
        _groupIds.Add(null);
        var items = new List<string> { Strings.Get("set.no_group") };
        foreach (var g in _working.ProfileGroups)
        {
            _groupIds.Add(g.Id);
            items.Add(g.Name);
        }
        ProfileGroupCombo.ItemsSource = items;
        var index = selected?.GroupId is { } gid ? _groupIds.IndexOf(gid) : 0;
        ProfileGroupCombo.SelectedIndex = index >= 0 ? index : 0;
        ProfileGroupCombo.IsEnabled = selected is not null;
        DeleteGroupButton.IsEnabled = _working.ProfileGroups.Count > 0;
    }

    private void OnProfileGroupChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingProfileControls)
            return;
        if (SelectedProfile is { } selected)
        {
            var idx = ProfileGroupCombo.SelectedIndex;
            selected.GroupId = idx >= 0 && idx < _groupIds.Count ? _groupIds[idx] : null;
        }
    }

    private void OnAddProfileGroup(object sender, RoutedEventArgs e)
    {
        var group = ProfileGroups.Add(_working, NewGroupText.Text);
        if (group is null)
        {
            ProfileNote.Text = Strings.Format("set.groups_cap", ProfileGroups.MaxGroups);
            return;
        }
        NewGroupText.Text = "";
        // Adding a group while a profile is selected files it straight in.
        if (SelectedProfile is { } selected)
            selected.GroupId = group.Id;
        _log.Info($"Profile group added: {group.Name}");
        UpdateProfileButtons();
    }

    private void OnDeleteProfileGroup(object sender, RoutedEventArgs e)
    {
        var idx = ProfileGroupCombo.SelectedIndex;
        var groupId = idx >= 0 && idx < _groupIds.Count ? _groupIds[idx] : null;
        var group = groupId is null ? null : _working.ProfileGroups.FirstOrDefault(g => g.Id == groupId);
        if (group is null)
        {
            ProfileNote.Text = Strings.Get("ui.pick_a_group_in_the_list_first_then");
            return;
        }
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Format("set.group_delete_body", group.Name),
            Strings.Get("set.group_delete_caption"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        FocusReturn.Restore(invoker);
        if (confirm != MessageBoxResult.Yes)
            return;
        ProfileGroups.Remove(_working, group.Id);
        _log.Info($"Profile group deleted: {group.Name} (members ungrouped)");
        UpdateProfileButtons();
    }

    // #130: archive write-through.
    private void OnProfileArchivedChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingProfileControls)
            return;
        if (SelectedProfile is not { } selected)
            return;
        if (selected.Id == _working.ActiveProfileId)
        {
            ProfileArchivedCheck.IsChecked = false; // the profile in use can't hide
            return;
        }
        selected.Archived = ProfileArchivedCheck.IsChecked == true;
        _log.Info(selected.Archived
            ? $"Profile {selected.Id} archived (hidden from switchers, data kept)"
            : $"Profile {selected.Id} restored from archive");
        ProfileNote.Text = selected.Archived
            ? Strings.Format("set.profile_archived_note", selected.Name)
            : Strings.Format("set.profile_restored_note", selected.Name);
        UpdateProfileButtons();
    }

    // #122: profile PIN set/change/remove — the app lock's flow, one profile at a time.
    private void OnSetProfilePin(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } selected)
            return;
        if (ProfileLock.TryGetRecord(selected, out var existing) && existing is not null)
        {
            var current = new PasswordPromptWindow(
                Strings.Format("set.pin_current_first", selected.Name), confirm: false) { Owner = this };
            if (FocusReturn.ShowDialog(current) != true)
                return;
            if (!ProfileLock.Verify(current.Password, selected))
            {
                ProfileNote.Text = Strings.Get("ui.that_s_not_this_profile_s_current_pin");
                return;
            }
        }
        var prompt = new PasswordPromptWindow(
            Strings.Format("set.pin_choose", selected.Name),
            confirm: true, AppLock.MinCredentialLength, AppLock.MaxCredentialLength) { Owner = this };
        if (FocusReturn.ShowDialog(prompt) != true)
            return;
        ProfileLock.SetCredential(selected, prompt.Password);
        ProfileNote.Text = Strings.Format("set.pin_set_note", selected.Name);
        _log.Info($"Profile PIN set for profile {selected.Id}");
        UpdateProfileButtons();
    }

    private void OnRemoveProfilePin(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } selected)
            return;
        var current = new PasswordPromptWindow(
            Strings.Format("set.pin_remove_prompt", selected.Name), confirm: false) { Owner = this };
        if (FocusReturn.ShowDialog(current) != true)
            return;
        if (!ProfileLock.Verify(current.Password, selected))
        {
            ProfileNote.Text = Strings.Get("ui.that_s_not_this_profile_s_current_pin");
            return;
        }
        ProfileLock.RemoveCredential(selected);
        ProfileNote.Text = Strings.Format("set.pin_removed_note", selected.Name);
        _log.Info($"Profile PIN removed for profile {selected.Id}");
        UpdateProfileButtons();
    }

    // #123: schedule editor write-through (checkbox, times on focus loss, day ticks).
    private void OnProfileScheduleChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingProfileControls)
            return;
        if (SelectedProfile is not { } selected)
            return;
        if (ProfileScheduleCheck.IsChecked != true)
        {
            ProfileSchedule.RemoveRule(_working, selected.Id);
            _log.Info($"Profile schedule removed for profile {selected.Id}");
            UpdateProfileButtons();
            return;
        }
        var start = ProfileSchedule.ParseMinutes(ProfileScheduleStartText.Text);
        var end = ProfileSchedule.ParseMinutes(ProfileScheduleEndText.Text);
        var mask = 0;
        var checks = ScheduleDayChecks();
        for (var i = 0; i < checks.Length; i++)
            if (checks[i].IsChecked == true)
                mask |= ProfileSchedule.MaskFor(ScheduleDayOrder[i]);
        if (start is null || end is null)
        {
            ProfileScheduleText.Text = Strings.Get("ui.times_must_be_24_hour_clock_like_09_00");
            return;
        }
        if (start == end)
        {
            ProfileScheduleText.Text = Strings.Get("ui.the_start_and_end_times_are_the_same");
            return;
        }
        if (mask == 0)
        {
            ProfileScheduleText.Text = Strings.Get("ui.tick_at_least_one_day_the_schedule_wasn");
            return;
        }
        var rule = new ProfileSwitchRule
        {
            ProfileId = selected.Id,
            DaysMask = mask,
            StartMinutes = start.Value,
            EndMinutes = end.Value,
        };
        ProfileSchedule.SetRule(_working, rule);
        _log.Info($"Profile schedule set for profile {selected.Id}: {ProfileSchedule.Describe(rule)}");
        UpdateProfileButtons();
    }

    private void OnScheduleAutoChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingProfileControls)
            return;
        _working.ProfileScheduleAutoSwitch = ScheduleAutoCheck.IsChecked == true;
        UpdateProfileButtons();
    }

    // #124: clone the selected profile (settings + appearance; never sign-in data).
    private void OnCloneProfile(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } selected)
            return;
        var clone = Profiles.Clone(_working, selected.Id);
        if (clone is null)
        {
            ProfileNote.Text = Strings.Format("set.profiles_cap_clone", Profiles.MaxProfiles);
            return;
        }
        _log.Info($"Profile {selected.Id} cloned to {clone.Id} (settings and appearance only)");
        RefreshProfileCombo(clone.Id);
        ProfileNote.Text = Strings.Format("set.profile_cloned", clone.Name);
    }

    // #128: create from a template (same disk warning as adding by hand past the soft threshold).
    private void OnAddFromTemplate(object sender, RoutedEventArgs e)
    {
        var idx = ProfileTemplateCombo.SelectedIndex;
        if (idx < 0 || idx >= _templateKeys.Count)
            return;
        if (_working.Profiles.Count >= Profiles.MaxProfiles)
        {
            ProfileNote.Text = Strings.Format("set.profiles_cap", Profiles.MaxProfiles);
            return;
        }
        if (_working.Profiles.Count >= Profiles.SoftProfileWarningThreshold)
        {
            var warnInvoker = FocusReturn.Capture();
            var warn = System.Windows.MessageBox.Show(this,
                Strings.Format("set.profiles_warn", _working.Profiles.Count),
                Strings.Get("set.profiles_caption"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            FocusReturn.Restore(warnInvoker);
            if (warn != MessageBoxResult.Yes)
                return;
        }
        var created = ProfileTemplates.Create(_working, _templateKeys[idx]);
        if (created is null)
        {
            ProfileNote.Text = Strings.Get("ui.that_template_couldn_t_be_used");
            return;
        }
        _log.Info($"Profile created from the {_templateKeys[idx]} template: {created.Id}");
        RefreshProfileCombo(created.Id);
    }

    // #125: counts-only health snapshot (folder walk + cache plan + cookie count + backup catalog, all reduced to numbers by the providers).
    private async void OnRefreshProfileHealth(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } selected)
            return;
        RefreshProfileHealthButton.IsEnabled = false;
        try
        {
            var dir = AppPaths.ProfileDir(selected.Id);
            var dataBytes = ProfileDataInfo.MeasureFolderBytes(dir);
            long? cacheBytes = null;
            try
            {
                cacheBytes = CacheCleanup.BuildPlan(dir, 0).Candidates.Sum(c => c.Bytes);
            }
            catch (Exception) { /* the plan is best-effort — "couldn't be measured" is the honest line */ }
            int? cookieCount = null;
            if (_privacy is not null)
            {
                var summary = await _privacy.GetCookieSummaryAsync(selected.Id);
                cookieCount = summary?.Count;
            }
            var lastBackup = BackupRotation.Catalog(BackupService.ExistingNames())
                .FirstOrDefault(i => i.Kind == BackupRotation.SettingsKind)?.Stamp;
            var report = ProfileHealth.Build(selected,
                new ProfileHealthFacts(dataBytes, cacheBytes, cookieCount, lastBackup));
            ProfileHealthText.Text =
                string.Join("\n", report.Lines.Select(l => $"{l.Label}: {l.Value}"))
                + "\n" + Strings.Get("set.health_counts_note");
        }
        finally
        {
            RefreshProfileHealthButton.IsEnabled = true;
        }
    }

    // #129: delete the local tally file (all profiles at once — it is one small file by design).
    private void OnClearProfileUsage(object sender, RoutedEventArgs e)
    {
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Get("set.usage_clear_body"),
            Strings.Get("clear_usage_counts"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        FocusReturn.Restore(invoker);
        if (confirm != MessageBoxResult.Yes)
            return;
        ProfileUsageStore.Clear(AppPaths.ProfileUsagePath);
        _log.Info("Profile usage counts cleared from Settings");
        UpdateProfileButtons();
    }

    // #126: export the selected profile as a bundle (settings + appearance only).
    private void OnExportProfile(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } selected)
            return;
        HarvestControls();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"musedesktop-profile-{selected.Name}.json",
            Filter = Strings.Get("dlg.filter_json"),
            Title = Strings.Get("ui.export_profile_bundle"),
        };
        var invoker = FocusReturn.Capture();
        var chosen = dialog.ShowDialog(this) == true;
        FocusReturn.Restore(invoker);
        if (!chosen)
            return;
        invoker = FocusReturn.Capture();
        var encrypt = System.Windows.MessageBox.Show(this,
            ProfileBundle.ContentsNote + Strings.Get("set.encrypt_bundle_ask"),
            Strings.Get("set.export_profile_caption"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        FocusReturn.Restore(invoker);
        if (encrypt == MessageBoxResult.Cancel)
            return;
        string? password = null;
        if (encrypt == MessageBoxResult.Yes)
        {
            var prompt = new PasswordPromptWindow(
                Strings.Get("set.bundle_password_choose"),
                confirm: true) { Owner = this };
            if (FocusReturn.ShowDialog(prompt) != true)
                return;
            password = prompt.Password;
        }
        try
        {
            File.WriteAllText(dialog.FileName, ProfileBundle.Export(selected, password));
            ProfileNote.Text = Strings.Format("set.bundle_saved", dialog.FileName, ProfileBundle.ContentsNote);
            _log.Info($"Profile bundle exported for profile {selected.Id} (encrypted: {password is not null})");
        }
        catch (Exception ex)
        {
            _log.Warn($"Profile bundle export failed: {ex.Message}");
            ProfileNote.Text = Strings.Format("set.file_write_failed", ex.Message);
        }
    }

    // #126: import a bundle as a NEW profile (never an overwrite, never sign-in data).
    private void OnImportProfile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Strings.Get("dlg.filter_json"),
            Title = Strings.Get("ui.import_profile_bundle"),
        };
        var invoker = FocusReturn.Capture();
        var chosen = dialog.ShowDialog(this) == true;
        FocusReturn.Restore(invoker);
        if (!chosen)
            return;
        string text;
        try
        {
            text = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            ProfileNote.Text = Strings.Format("set.file_read_failed", ex.Message);
            return;
        }
        string? password = null;
        if (ProfileBundle.NeedsPassword(text))
        {
            var prompt = new PasswordPromptWindow(
                Strings.Get("set.bundle_password_prompt"),
                confirm: false) { Owner = this };
            if (FocusReturn.ShowDialog(prompt) != true)
                return;
            password = prompt.Password;
        }
        if (!ProfileBundle.TryImport(text, password, out var imported, out var error) || imported is null)
        {
            ProfileNote.Text = error ?? Strings.Get("set.bundle_import_failed");
            return;
        }
        if (_working.Profiles.Count >= Profiles.MaxProfiles)
        {
            ProfileNote.Text = Strings.Format("set.profiles_cap_import", Profiles.MaxProfiles);
            return;
        }
        _working.Profiles.Add(imported);
        _log.Info($"Profile bundle imported as new profile {imported.Id} (settings and appearance only)");
        RefreshProfileCombo(imported.Id);
        ProfileNote.Text = Strings.Format("set.bundle_imported", imported.Name, ProfileBundle.ContentsNote);
    }

    // ───────────────────────── R2: workspaces & sessions ─────────────────────────

    /// <summary>The workspace the section is editing (the combo's selection).</summary>
    private Workspace? SelectedWorkspace => WorkspaceCombo.SelectedItem as Workspace;

    /// <summary>Rebuild the workspace list (R2 #120), keeping the selection by id — the profiles combo's pattern.</summary>
    private void RefreshWorkspaceCombo(string? selectId)
    {
        WorkspaceCombo.ItemsSource = null;
        WorkspaceCombo.ItemsSource = _working.Workspaces;
        WorkspaceCombo.DisplayMemberPath = nameof(Workspace.Name);
        WorkspaceCombo.SelectedItem = _working.Workspaces.FirstOrDefault(w => w.Id == selectId)
                                      ?? _working.Workspaces.FirstOrDefault();
        UpdateWorkspaceControls();
    }

    /// <summary>Fill the section's controls from the selected workspace (guarded, like the profile controls).</summary>
    private void UpdateWorkspaceControls()
    {
        var selected = SelectedWorkspace;
        _syncingWorkspaceControls = true;
        WorkspaceNameText.Text = selected?.Name ?? "";
        WorkspaceNameText.IsEnabled = selected is not null;
        var glyphIndex = selected?.Glyph is { } glyph ? _glyphKeys.IndexOf(glyph) : 0;
        WorkspaceGlyphCombo.SelectedIndex = glyphIndex >= 0 ? glyphIndex : 0;
        WorkspaceGlyphCombo.IsEnabled = selected is not null;
        var accentIndex = selected?.AccentColor is { } accent ? _colorKeys.IndexOf(accent) : 0;
        WorkspaceAccentCombo.SelectedIndex = accentIndex >= 0 ? accentIndex : 0;
        WorkspaceAccentCombo.IsEnabled = selected is not null;
        WorkspaceStartupCheck.IsChecked = selected is not null && _working.StartupWorkspaceId == selected.Id;
        WorkspaceStartupCheck.IsEnabled = selected is not null;
        _syncingWorkspaceControls = false;
        WorkspaceApplyButton.IsEnabled = selected is not null && _applyWorkspace is not null;
        WorkspaceDuplicateButton.IsEnabled = selected is not null
                                             && _working.Workspaces.Count < Workspaces.MaxWorkspaces;
        WorkspaceDeleteButton.IsEnabled = selected is not null;
        WorkspaceNote.Text = selected is null
            ? Strings.Get("set.workspaces_none")
            : DescribeWorkspace(selected);
    }

    /// <summary>The facts line for a workspace: its windows and their profiles, and when it last ran.</summary>
    private string DescribeWorkspace(Workspace ws)
    {
        var profiles = ws.Windows
            .Select(w => _working.Profiles.FirstOrDefault(p => p.Id == w.ProfileId)?.Name ?? w.ProfileId)
            .Distinct()
            .ToList();
        var text = Strings.Format("set.ws_windows", ws.Windows.Count)
                   + (profiles.Count > 0 ? Strings.Format("set.ws_profile", string.Join(", ", profiles)) : "");
        if (ws.LastAppliedUtc is { } applied)
            text += Strings.Format("set.ws_last_applied", applied.LocalDateTime);
        if (_working.StartupWorkspaceId == ws.Id)
            text += Strings.Get("set.ws_opens_at_launch");
        return text;
    }

    private void OnWorkspaceSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateWorkspaceControls();

    private void OnWorkspaceNameLostFocus(object sender, RoutedEventArgs e)
    {
        var selected = SelectedWorkspace;
        if (selected is null)
            return;
        var name = WorkspaceNameText.Text.Trim();
        if (name.Length == 0)
        {
            WorkspaceNameText.Text = selected.Name;
            return;
        }
        if (name.Length > 40)
            name = name[..40];
        if (name != selected.Name)
        {
            selected.Name = name;
            _log.Info($"Workspace {selected.Id} renamed to '{name}'");
            RefreshWorkspaceCombo(selected.Id);
        }
    }

    private void OnWorkspaceGlyphChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingWorkspaceControls || SelectedWorkspace is not { } selected)
            return;
        var index = WorkspaceGlyphCombo.SelectedIndex;
        selected.Glyph = index >= 0 && index < _glyphKeys.Count ? _glyphKeys[index] : null;
    }

    private void OnWorkspaceAccentChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingWorkspaceControls || SelectedWorkspace is not { } selected)
            return;
        var index = WorkspaceAccentCombo.SelectedIndex;
        selected.AccentColor = index >= 0 && index < _colorKeys.Count ? _colorKeys[index] : null;
    }

    private void OnWorkspaceStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingWorkspaceControls || SelectedWorkspace is not { } selected)
            return;
        if (WorkspaceStartupCheck.IsChecked == true)
            _working.StartupWorkspaceId = selected.Id;
        else if (_working.StartupWorkspaceId == selected.Id)
            _working.StartupWorkspaceId = null;
        UpdateWorkspaceControls();
    }

    private void OnDuplicateWorkspace(object sender, RoutedEventArgs e)
    {
        if (SelectedWorkspace is not { } selected)
            return;
        var copy = Workspaces.Duplicate(selected, _working);
        _working.Workspaces.Add(copy);
        _log.Info($"Workspace duplicated: '{selected.Name}' → '{copy.Name}'");
        RefreshWorkspaceCombo(copy.Id);
    }

    /// <summary>
    /// Delete a workspace (R2 #120) with the profiles' deferred-safety
    /// shape: the removal happens on the working clone only — Cancel
    /// in this window undoes it, OK makes it stick — and nothing else
    /// is touched (no window closes, no folder exists to delete). A
    /// startup pointer at the deleted workspace clears here so the
    /// section never shows a choice that names nothing.
    /// </summary>
    private void OnDeleteWorkspace(object sender, RoutedEventArgs e)
    {
        if (SelectedWorkspace is not { } selected)
            return;
        _working.Workspaces.Remove(selected);
        if (_working.StartupWorkspaceId == selected.Id)
            _working.StartupWorkspaceId = null;
        if (_working.ActiveWorkspaceId == selected.Id)
            _working.ActiveWorkspaceId = null;
        _log.Info($"Workspace deleted: '{selected.Name}' (applies on OK)");
        RefreshWorkspaceCombo(_working.Workspaces.FirstOrDefault()?.Id);
    }

    /// <summary>Apply the selected workspace now (R2 #120): harvest first (so the apply sees the section's own edits), hand the clone to the main window like the profile switch does, and close with OK so the adoption there runs on the same values.</summary>
    private void OnApplyWorkspace(object sender, RoutedEventArgs e)
    {
        if (SelectedWorkspace is not { } selected || _applyWorkspace is null)
            return;
        HarvestControls();
        _applyWorkspace(_working, selected.Id);
        DialogResult = true;
    }

    private void OnBrowseDownloadsFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Strings.Get("ui.choose_where_musedesktop_saves_downloads"),
            InitialDirectory = Directory.Exists(DownloadsFolderText.Text)
                ? DownloadsFolderText.Text
                : AppPaths.DownloadsHint,
        };
        var invoker = FocusReturn.Capture();
        var chosen = dialog.ShowDialog(this) == true;
        FocusReturn.Restore(invoker);
        if (chosen)
            DownloadsFolderText.Text = dialog.FolderName;
    }

    /// <summary>Pick the archive folder (R4 #138); empty text means the per-folder default.</summary>
    private void OnBrowseArchiveFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Strings.Get("ui.choose_where_archived_downloads_go"),
            InitialDirectory = Directory.Exists(ArchiveFolderText.Text)
                ? ArchiveFolderText.Text
                : AppPaths.DownloadsHint,
        };
        var invoker = FocusReturn.Capture();
        var chosen = dialog.ShowDialog(this) == true;
        FocusReturn.Restore(invoker);
        if (chosen)
            ArchiveFolderText.Text = dialog.FolderName;
    }

    // ───────────────────────── S7: privacy & security ─────────────────────────

    private ClearDataChoice SelectedClearChoice =>
        ClearChoiceCombo.SelectedIndex >= 0 && ClearChoiceCombo.SelectedIndex < ClearChoices.Length
            ? ClearChoices[ClearChoiceCombo.SelectedIndex]
            : ClearDataChoice.AllSiteData;

    private ClearDataScope SelectedClearScope =>
        ClearScopeCombo.SelectedIndex == 1 ? ClearDataScope.AllProfiles : ClearDataScope.ActiveProfile;

    private string ActiveProfileName =>
        _working.Profiles.FirstOrDefault(p => p.Id == _working.ActiveProfileId)?.Name ?? "Default";

    private void OnClearChoiceChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateClearPlanNote();

    /// <summary>The note under the clear controls always states the current choice's consequence + scope (#61) — before anything is deleted.</summary>
    private void UpdateClearPlanNote()
    {
        if (ClearPlanNote is null)
            return;
        ClearPlanNote.Text = ClearDataPlan.Describe(SelectedClearChoice) + " " +
            ClearDataPlan.DescribeScope(SelectedClearScope, ActiveProfileName, _working.Profiles.Count);
    }

    private async void OnClearDataGranular(object sender, RoutedEventArgs e)
    {
        if (_privacy is null)
        {
            ClearResultNote.Text = Strings.Get("ui.the_web_view_isn_t_running_yet_try");
            return;
        }
        var choice = SelectedClearChoice;
        var scope = SelectedClearScope;
        var targets = scope == ClearDataScope.AllProfiles
            ? _working.Profiles.Select(p => (p.Id, p.Name)).ToList()
            : new List<(string, string)> { (_working.ActiveProfileId, ActiveProfileName) };
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            $"{ClearDataPlan.Title(choice)} — {ClearDataPlan.DescribeScope(scope, ActiveProfileName, _working.Profiles.Count)}\n\n" +
            ClearDataPlan.Describe(choice),
            Strings.Get("set.clear_data_caption"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        FocusReturn.Restore(invoker);
        if (confirm != MessageBoxResult.Yes)
            return;
        ClearNowButton.IsEnabled = false;
        try
        {
            var result = await _privacy.ClearAsync(choice, targets);
            ClearResultNote.Text = result.Summary;
            _ = RefreshSiteDataAsync();
            // The pre-v0.11.0 flow, preserved: a successful clear that
            // signed the RUNNING profile out closes Settings (adopting
            // the edits) while the main window lands on Home.
            if (result.Success && result.ActiveProfileTouched && ClearDataPlan.SignsOut(choice))
                DialogResult = true;
        }
        finally
        {
            ClearNowButton.IsEnabled = true;
        }
    }

    // #62: the running profile's saved permission choices, read live.
    private void OnRefreshPermissions(object sender, RoutedEventArgs e) => _ = RefreshPermissionsAsync();

    // ───────────────────── R7: language, packs, announcements, spacing ─────────────────────

    /// <summary>Language codes parallel to LanguageCombo's items; index 0 is null — "follow Windows" (#162).</summary>
    private readonly List<string?> _languageCodes = new();

    /// <summary>The clear-data combo's title key for a choice (the localized twin of ClearDataPlan.Title).</summary>
    private static string ClearChoiceKey(ClearDataChoice choice) => choice switch
    {
        ClearDataChoice.Cookies => "settings.clear.cookies",
        ClearDataChoice.Cache => "settings.clear.cache",
        _ => "settings.clear.all",
    };

    /// <summary>
    /// Fill the language section (#162/#165/#169/#170): the picker
    /// (System default, the built-in languages, then any imported
    /// pack that adds a language of its own — a pack replacing a
    /// built-in doesn't duplicate its row; picking the row picks the
    /// language, and the pack wins inside it), the four announcement
    /// switches from the working switches map, and the spacing combo.
    /// </summary>
    private void PopulateLanguageControls()
    {
        LanguageCombo.Items.Clear();
        _languageCodes.Clear();
        LanguageCombo.Items.Add(Strings.Get("ui.system_default"));
        _languageCodes.Add(null);
        foreach (var info in LanguageCatalog.BuiltIn)
        {
            LanguageCombo.Items.Add(info.NativeName == info.EnglishName
                ? info.NativeName
                : $"{info.NativeName} — {info.EnglishName}");
            _languageCodes.Add(info.Code);
        }
        foreach (var (code, pack) in Strings.Packs.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (_languageCodes.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)))
                continue;
            LanguageCombo.Items.Add(pack.DisplayName);
            _languageCodes.Add(code);
        }
        var selected = _working.UiLanguage is { } preference
            ? _languageCodes.FindIndex(c => string.Equals(c, preference, StringComparison.OrdinalIgnoreCase))
            : 0;
        LanguageCombo.SelectedIndex = selected >= 0 ? selected : 0;

        AnnouncementDownloadsCheck.IsChecked =
            AnnouncementSwitches.IsEnabled(_working.AnnouncementKindEnabled, AnnouncementKind.Downloads);
        AnnouncementUpdatesCheck.IsChecked =
            AnnouncementSwitches.IsEnabled(_working.AnnouncementKindEnabled, AnnouncementKind.Updates);
        AnnouncementTimersCheck.IsChecked =
            AnnouncementSwitches.IsEnabled(_working.AnnouncementKindEnabled, AnnouncementKind.Timers);
        AnnouncementFocusCheck.IsChecked =
            AnnouncementSwitches.IsEnabled(_working.AnnouncementKindEnabled, AnnouncementKind.Focus);

        var scale = ShellTextSpacing.Sanitize(_working.ShellTextSpacingScale);
        TextSpacingCombo.SelectedIndex = ShellTextSpacing.Options
            .Select((value, index) => (value, index))
            .First(t => t.value == scale).index;
        RefreshLanguagePackStatus();
    }

    /// <summary>The line under the pack buttons: what packs are installed, and a named warning for any file that failed validation at launch.</summary>
    private void RefreshLanguagePackStatus()
    {
        var parts = new List<string>();
        if (Strings.Packs.Count > 0)
            parts.Add(Strings.Format("settings.packs_installed",
                string.Join(", ", Strings.Packs.Values.Select(p => p.DisplayName))));
        if (Strings.SkippedPackFileNames.Count > 0)
            parts.Add(Strings.Format("settings.packs_skipped",
                string.Join(", ", Strings.SkippedPackFileNames)));
        LanguagePackStatus.Text = string.Join(" ", parts);
    }

    /// <summary>Export a blank pack (#165): the full English source as a template a translator can fill in.</summary>
    private void OnExportLanguagePack(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Strings.Get("dlg.export_pack_title"),
            Filter = Strings.Get("dlg.language_pack_filter"),
            FileName = LanguagePackFile.FileNameFor("xx-XX"),
            DefaultExt = ".json",
        };
        var invoker = FocusReturn.Capture();
        var chosen = dialog.ShowDialog(this) == true;
        FocusReturn.Restore(invoker);
        if (!chosen)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, LanguagePackFile.ExportTemplate(StringCatalog.Source));
            LanguagePackStatus.Text = Strings.Get("settings.pack_exported");
        }
        catch (Exception ex)
        {
            _log.Warn($"Language pack export failed: {ex.Message}");
            LanguagePackStatus.Text = Strings.Get("settings.pack_export_failed");
        }
    }

    /// <summary>
    /// Import a pack (#165): the file is validated by Core before
    /// anything is written — a valid pack lands in the packs folder
    /// (a live, file-level action like the Privacy section's, so it
    /// stands even if the dialog is later cancelled) and the picker
    /// gains it at once; an invalid one changes nothing and the
    /// validator's plain-words reason is shown as-is.
    /// </summary>
    private void OnImportLanguagePack(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.Get("dlg.import_pack_title"),
            Filter = Strings.Get("dlg.language_pack_filter"),
            DefaultExt = ".json",
        };
        var invoker = FocusReturn.Capture();
        var chosen = dialog.ShowDialog(this) == true;
        FocusReturn.Restore(invoker);
        if (!chosen)
            return;
        string json;
        try
        {
            json = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            _log.Warn($"Language pack read failed: {ex.Message}");
            LanguagePackStatus.Text = Strings.Get("settings.pack_read_failed");
            return;
        }
        if (!LanguagePackFile.TryLoad(json, StringCatalog.Source, out var pack, out var error) || pack is null)
        {
            System.Windows.MessageBox.Show(this, error ?? Strings.Get("settings.pack_invalid"),
                Strings.Get("msg.language_pack"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            Directory.CreateDirectory(AppPaths.LanguagePacksDir);
            File.WriteAllText(
                Path.Combine(AppPaths.LanguagePacksDir, LanguagePackFile.FileNameFor(pack.Language)),
                LanguagePackFile.Serialize(pack));
        }
        catch (Exception ex)
        {
            _log.Warn($"Language pack install failed: {ex.Message}");
            LanguagePackStatus.Text = Strings.Get("settings.pack_install_failed");
            return;
        }
        // Rebuild the localizer so the picker (and Diagnostics) see
        // the pack now; the working language choice is preserved —
        // the rebuild reads the app's stored settings, and the
        // picker's own selection is restored from the clone after.
        if (System.Windows.Application.Current is App app)
            Strings.Initialize(app.Settings);
        var chosenLanguage = _working.UiLanguage;
        PopulateLanguageControls();
        _working.UiLanguage = chosenLanguage;
        if (chosenLanguage is not null)
        {
            var index = _languageCodes.FindIndex(c =>
                string.Equals(c, chosenLanguage, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                LanguageCombo.SelectedIndex = index;
        }
        LanguagePackStatus.Text = Strings.Format("settings.pack_imported", pack.DisplayName);
    }

    // ───────────────────── guided self-tests (S10 #92) ─────────────────────

    /// <summary>
    /// Test microphone: run the mic-test window on the live profile's
    /// environment, then show the outcome line plus what Muse's own
    /// saved microphone choice is. Announced (S6 live regions) as well
    /// as shown — the result is the point of the test.
    /// </summary>
    private async void OnTestMicrophone(object sender, RoutedEventArgs e)
    {
        if (_privacy?.LiveEnvironment is not { } environment)
        {
            SelfTestResultText.Text = Strings.Get("ui.the_web_view_is_still_starting_give_it");
            return;
        }
        var dialog = new MicTestWindow(environment, _log) { Owner = this };
        FocusReturn.ShowDialog(dialog);
        if (dialog.ResultLine is not { } line)
            return;
        var savedLine = await SavedMicLineAsync();
        SelfTestResultText.Text = line + "\n" + savedLine;
        Announcer.Polite(line);
    }

    /// <summary>Muse's saved microphone choice for muse.ai (the S7 permission record), as one plain line.</summary>
    private async Task<string> SavedMicLineAsync()
    {
        if (_privacy is null || !_privacy.IsLiveReady)
            return SelfTestLines.SavedMic(null);
        try
        {
            var rows = await _privacy.GetPermissionsAsync();
            var mic = rows.FirstOrDefault(r =>
                r.Kind.Equals("Microphone", StringComparison.OrdinalIgnoreCase)
                && r.Host.Equals("muse.ai", StringComparison.OrdinalIgnoreCase));
            return SelfTestLines.SavedMic(mic?.State);
        }
        catch
        {
            return SelfTestLines.SavedMic(null);
        }
    }

    /// <summary>
    /// Send test notification: the main window performs it (it owns the
    /// tray) and returns the Core result line — sent even under quiet
    /// hours/DND because the user asked, with the suppression named.
    /// </summary>
    private void OnTestNotification(object sender, RoutedEventArgs e)
    {
        if (_notificationTest is null)
        {
            SelfTestResultText.Text = Strings.Get("ui.the_test_notification_isn_t_available");
            return;
        }
        var line = _notificationTest();
        SelfTestResultText.Text = line;
        Announcer.Polite(line);
    }

    private async Task RefreshPermissionsAsync()
    {
        PermissionRows.Children.Clear();
        if (_privacy is null || !_privacy.IsLiveReady)
        {
            PermissionsNote.Text = Strings.Get("ui.the_web_view_isn_t_running_yet_so_saved") +
                Strings.Get("ui.own_policy_stands");
            return;
        }
        try
        {
            var rows = await _privacy.GetPermissionsAsync();
            PermissionsNote.Text = rows.Count == 0
                ? Strings.Get("set.perms_none")
                : Strings.Get("set.perms_saved");
            foreach (var row in rows)
            {
                var line = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Margin = new Thickness(0, 2, 0, 2),
                };
                line.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = $"{row.Kind} — {row.Host} — {row.State}",
                    Width = 218,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                line.Children.Add(PermissionButton(Strings.Get("set.perm_allow"), row, CoreWebView2PermissionState.Allow));
                line.Children.Add(PermissionButton(Strings.Get("set.perm_block"), row, CoreWebView2PermissionState.Deny));
                line.Children.Add(PermissionButton(Strings.Get("set.perm_forget"), row, CoreWebView2PermissionState.Default));
                PermissionRows.Children.Add(line);
            }
        }
        catch (Exception ex)
        {
            PermissionsNote.Text = Strings.Format("set.perms_read_failed", ex.Message);
        }
    }

    private System.Windows.Controls.Button PermissionButton(string label, PermissionRow row, CoreWebView2PermissionState state)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = label,
            Width = 58,
            Margin = new Thickness(4, 0, 0, 0),
            Tag = (row, state),
            ToolTip = Strings.Format("set.perm_tooltip", label, row.Kind, row.Host),
        };
        button.Click += OnPermissionButton;
        return button;
    }

    private async void OnPermissionButton(object sender, RoutedEventArgs e)
    {
        if (_privacy is null || sender is not System.Windows.Controls.Button { Tag: (PermissionRow row, CoreWebView2PermissionState state) })
            return;
        try
        {
            await _privacy.SetPermissionAsync(row.Kind, row.Origin, state);
            await RefreshPermissionsAsync();
        }
        catch (Exception ex)
        {
            PermissionsNote.Text = Strings.Format("set.perms_change_failed", ex.Message);
        }
    }

    // #63: per-profile site data (folder size + cookie counts).
    private void OnRefreshSiteData(object sender, RoutedEventArgs e) => _ = RefreshSiteDataAsync();

    private async Task RefreshSiteDataAsync()
    {
        SiteDataRows.Children.Clear();
        foreach (var profile in _working.Profiles.ToList())
        {
            var label = new System.Windows.Controls.TextBlock
            {
                Text = Strings.Format("set.sitedata_measuring", profile.Name),
                Width = 300,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var clearButton = new System.Windows.Controls.Button
            {
                Content = Strings.Get("ui.clear_this_profile"),
                Width = 118,
                Margin = new Thickness(4, 0, 0, 0),
                Tag = profile.Id,
                ToolTip = Strings.Format("set.sitedata_clear_tip", profile.Name),
            };
            clearButton.Click += OnClearOneProfile;
            // S8 #75: the manual cache cleanup — disposable caches
            // only, from the same allowlist the automatic cap uses.
            var cleanButton = new System.Windows.Controls.Button
            {
                Content = Strings.Get("ui.clean_caches"),
                Width = 92,
                Margin = new Thickness(4, 0, 0, 0),
                Tag = profile.Id,
                ToolTip = Strings.Format("set.sitedata_clean_tip", profile.Name),
            };
            cleanButton.Click += OnCleanProfileCaches;
            var line = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2),
            };
            line.Children.Add(label);
            line.Children.Add(clearButton);
            line.Children.Add(cleanButton);
            SiteDataRows.Children.Add(line);

            var bytes = ProfileDataInfo.MeasureFolderBytes(AppPaths.ProfileDir(profile.Id));
            var sizeText = bytes is { } b ? DownloadText.FormatBytes(b) : Strings.Get("settings.no_data_folder_yet");
            var cookieText = Strings.Get("settings.cookie_unavailable");
            if (_privacy is not null && await _privacy.GetCookieSummaryAsync(profile.Id) is { } cookies)
            {
                cookieText = cookies.Count == 0
                    ? Strings.Get("settings.no_cookies")
                    : Strings.Format("settings.cookies_across", cookies.Count, cookies.Domains.Count);
            }
            // S8 #75: the cap and its last cleanup ride along so the
            // cleanup is visible here, never silent.
            var capText = profile.DataCapBytes > 0 ? Strings.Format("settings.cap_suffix", ProfileDataCaps.Label(profile.DataCapBytes)) : "";
            if (profile.LastCacheCleanupUtc is { } cleanedUtc)
                capText += Strings.Format("settings.caches_cleaned", cleanedUtc.LocalDateTime, DownloadText.FormatBytes(profile.LastCacheCleanupFreedBytes));
            label.Text = Strings.Format("settings.profile_data_line", profile.Name, sizeText, cookieText, capText);
        }
    }

    /// <summary>
    /// Manual cache cleanup (S8 #75): the plan comes from Core's
    /// allowlist model — only disposable cache folders, never cookies
    /// or session storage — runs off the UI thread, stamps the
    /// profile, and refreshes the rows so the result is visible.
    /// </summary>
    private async void OnCleanProfileCaches(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string profileId })
            return;
        var profile = _working.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
            return;
        var profileDir = AppPaths.ProfileDir(profile.Id);
        var outcome = await Task.Run(() => CacheCleanupRunner.RunManual(profileDir, _log));
        if (outcome.DeletedCount > 0)
        {
            profile.LastCacheCleanupUtc = DateTimeOffset.UtcNow;
            profile.LastCacheCleanupFreedBytes = outcome.FreedBytes;
        }
        ClearResultNote.Text = outcome.DeletedCount == 0
            ? Strings.Format("settings.clean_none", profile.Name)
            : Strings.Format("settings.clean_done", profile.Name, outcome.DeletedCount,
                DownloadText.FormatBytes(outcome.FreedBytes));
        _ = RefreshSiteDataAsync();
        UpdateProfileButtons();
    }

    private async void OnClearOneProfile(object sender, RoutedEventArgs e)
    {
        if (_privacy is null || sender is not System.Windows.Controls.Button { Tag: string profileId })
            return;
        var profile = _working.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
            return;
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Format("set.clear_profile_body", profile.Name) + ClearDataPlan.Describe(ClearDataChoice.AllSiteData),
            Strings.Get("set.clear_profile_caption"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        FocusReturn.Restore(invoker);
        if (confirm != MessageBoxResult.Yes)
            return;
        var result = await _privacy.ClearAsync(
            ClearDataChoice.AllSiteData, new List<(string, string)> { (profile.Id, profile.Name) });
        ClearResultNote.Text = result.Summary;
        _ = RefreshSiteDataAsync();
        if (result.Success && result.ActiveProfileTouched)
            DialogResult = true; // the running profile signed out — same close-and-adopt as the old clear
    }

    // #67: the counts-only privacy report (+ the #62 recent denials).
    private void RefreshPrivacyReport()
    {
        PrivacyStats stats;
        try { stats = PrivacyStatsStore.Load(AppPaths.PrivacyStatsPath); }
        catch { stats = new PrivacyStats(); }
        var sb = new StringBuilder();
        var since = stats.SinceUtc is { } sinceUtc ? Strings.Format("set.privacy_since", sinceUtc.LocalDateTime) : Strings.Get("set.privacy_on");
        sb.AppendLine(Strings.Format("set.privacy_blocked_line", since));
        sb.AppendLine(Strings.Format("set.privacy_nav", stats.Total(PrivacyEventCategory.BlockedNavigation)));
        sb.AppendLine(Strings.Format("set.privacy_newwin", stats.Total(PrivacyEventCategory.BlockedNewWindow)));
        sb.AppendLine(Strings.Format("set.privacy_perm", stats.Total(PrivacyEventCategory.DeniedPermission)));
        var rows = stats.Rows().Take(6).ToList();
        if (rows.Count > 0)
        {
            sb.AppendLine(Strings.Get("set.privacy_most_common"));
            foreach (var row in rows)
            {
                var what = row.Category switch
                {
                    nameof(PrivacyEventCategory.BlockedNavigation) => Strings.Get("set.privacy_cat_nav"),
                    nameof(PrivacyEventCategory.BlockedNewWindow) => Strings.Get("set.privacy_cat_newwin"),
                    nameof(PrivacyEventCategory.DeniedPermission) => Strings.Get("set.privacy_cat_perm"),
                    _ => row.Category,
                };
                sb.AppendLine(Strings.Format("set.privacy_row", row.Count, what, row.Kind) +
                              (string.IsNullOrEmpty(row.Host) ? "" : Strings.Format("set.privacy_row_host", row.Host)));
            }
        }
        PrivacyReportText.Text = sb.ToString().TrimEnd();
        PermissionDenialsText.Text = stats.RecentDenials.Count == 0
            ? Strings.Get("set.privacy_denials_none")
            : Strings.Get("set.privacy_denials_head") + "\n" + string.Join("\n",
                stats.RecentDenials.Take(8).Select(d =>
                    Strings.Format("set.privacy_denial_row", d.TimeUtc.LocalDateTime, d.Kind, string.IsNullOrEmpty(d.Host) ? Strings.Get("set.privacy_unknown_site") : d.Host)));
    }

    private void OnResetPrivacyStats(object sender, RoutedEventArgs e)
    {
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Get("set.privacy_reset_body"),
            Strings.Get("set.privacy_reset_caption"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        FocusReturn.Restore(invoker);
        if (confirm != MessageBoxResult.Yes)
            return;
        try
        {
            PrivacyStatsStore.Save(AppPaths.PrivacyStatsPath, new PrivacyStats());
            _log.Info("Privacy report counters reset from Settings");
        }
        catch (Exception ex)
        {
            _log.Warn($"Privacy report reset failed: {ex.Message}");
        }
        RefreshPrivacyReport();
    }

    private void OnViewLog(object sender, RoutedEventArgs e)
    {
        var dialog = new LogViewerWindow { Owner = this };
        ShellTextScaling.Apply(dialog, _working.ShellFontScale);
        FocusReturn.ShowDialog(dialog);
    }

    // #70: data-folder shortcut + the backup guide.
    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.RootDir);
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.RootDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"Open data folder failed: {ex.Message}");
            ImportNote.Text = Strings.Format("set.datafolder_open_failed", ex.Message, AppPaths.RootDir);
        }
    }

    private void OnBackupGuide(object sender, RoutedEventArgs e)
    {
        var webViewRoot = Path.GetDirectoryName(AppPaths.WebViewDataDir) ?? AppPaths.WebViewDataDir;
        var invoker = FocusReturn.Capture();
        System.Windows.MessageBox.Show(this,
            BackupGuide.Compose(AppPaths.RootDir, webViewRoot),
            Strings.Get("set.backup_guide_caption"), MessageBoxButton.OK, MessageBoxImage.Information);
        FocusReturn.Restore(invoker);
    }

    // #64: the app lock — set/change/remove + the two switches. The
    // credential fields on the clone are write-through from these
    // dialogs (like the toolbar editor); OK adopts them, Cancel
    // discards them, exactly like every other setting here.
    private bool _syncingLockControls;

    private void RefreshLockUi()
    {
        var hasRecord = AppLock.HasValidRecord(_working);
        _syncingLockControls = true;
        AppLockEnabledCheck.IsChecked = _working.AppLockEnabled && hasRecord;
        AppLockTrayHideCheck.IsChecked = _working.AppLockOnTrayHide;
        _syncingLockControls = false;
        AppLockTrayHideCheck.IsEnabled = hasRecord;
        RemoveLockPasswordButton.Visibility = hasRecord ? Visibility.Visible : Visibility.Collapsed;
        AppLockStatusText.Text = hasRecord
            ? _working.AppLockEnabled
                ? Strings.Get("set.lock_on")
                : Strings.Get("set.lock_set_off")
            : Strings.Get("set.lock_off");
    }

    private void OnAppLockEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingLockControls)
            return;
        if (AppLockEnabledCheck.IsChecked == true && !AppLock.HasValidRecord(_working))
        {
            // Can't enable with no credential: go straight to setting one.
            if (!PromptSetLockPassword())
            {
                _syncingLockControls = true;
                AppLockEnabledCheck.IsChecked = false;
                _syncingLockControls = false;
            }
        }
        _working.AppLockEnabled = AppLockEnabledCheck.IsChecked == true && AppLock.HasValidRecord(_working);
        RefreshLockUi();
    }

    private void OnSetLockPassword(object sender, RoutedEventArgs e)
    {
        // Changing an existing password first proves the current one —
        // the app is unlocked, but a walk-up user shouldn't be able to
        // swap the lock's password from an unattended unlocked session.
        if (AppLock.TryGetRecord(_working, out var existing) && existing is not null)
        {
            var current = new PasswordPromptWindow(
                Strings.Get("set.lock_current_first"), confirm: false) { Owner = this };
            if (FocusReturn.ShowDialog(current) != true)
                return;
            if (!AppLock.Verify(current.Password, existing))
            {
                AppLockNote.Text = Strings.Get("ui.that_s_not_the_current_password_nothing");
                return;
            }
        }
        PromptSetLockPassword();
    }

    /// <summary>Ask for a new lock password (twice) and store its record on the clone. True when one was set.</summary>
    private bool PromptSetLockPassword()
    {
        var prompt = new PasswordPromptWindow(
            Strings.Get("set.lock_choose"),
            confirm: true, AppLock.MinCredentialLength, AppLock.MaxCredentialLength) { Owner = this };
        if (FocusReturn.ShowDialog(prompt) != true)
            return false;
        AppLock.SetCredential(_working, prompt.Password);
        AppLockNote.Text = Strings.Get("ui.app_password_set_the_lock_is_on_it");
        RefreshLockUi();
        return true;
    }

    private void OnRemoveLockPassword(object sender, RoutedEventArgs e)
    {
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Get("set.lock_remove_body"),
            Strings.Get("set.lock_remove_caption"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        FocusReturn.Restore(invoker);
        if (confirm != MessageBoxResult.Yes)
            return;
        AppLock.RemoveCredential(_working);
        AppLockNote.Text = Strings.Get("ui.app_password_removed_the_lock_is_off");
        RefreshLockUi();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        HarvestControls();
        DialogResult = true;
    }

    // ───────────────────────── S6: zoom range + shortcut remapping ─────────────────────────

    /// <summary>(Re)fill both zoom combos from the step table for the working extended-range choice (S6 #57), keeping any current selections by percent text.</summary>
    private void RebuildZoomCombos()
    {
        var zoomText = ZoomCombo.SelectedItem as string;
        var profileText = ProfileZoomCombo.SelectedItem as string;
        ZoomCombo.Items.Clear();
        ProfileZoomCombo.Items.Clear();
        foreach (var step in ZoomMath.StepsFor(_working.ExtendedZoomRange))
        {
            ZoomCombo.Items.Add(ZoomMath.FormatPercent(step));
            ProfileZoomCombo.Items.Add(ZoomMath.FormatPercent(step));
        }
        if (zoomText is not null && ZoomCombo.Items.Contains(zoomText))
            ZoomCombo.SelectedItem = zoomText;
        if (profileText is not null && ProfileZoomCombo.Items.Contains(profileText))
            ProfileZoomCombo.SelectedItem = profileText;
    }

    /// <summary>Extended zoom range toggle (S6 #57): write-through, and both zoom combos rebuild from the matching step table.</summary>
    private void OnExtendedZoomChanged(object sender, RoutedEventArgs e)
    {
        _working.ExtendedZoomRange = ExtendedZoomCheck.IsChecked == true;
        RebuildZoomCombos();
        var current = ZoomMath.FormatPercent(_working.ZoomFactor);
        ZoomCombo.SelectedItem = ZoomCombo.Items.Contains(current) ? current : "100%";
        UpdateProfileButtons();
    }

    /// <summary>The action whose key is being captured right now (S6 #53), or null.</summary>
    private string? _capturingActionId;

    private void RebuildShortcutRows()
    {
        ShortcutRows.Children.Clear();
        foreach (var action in ShortcutCatalog.Remappable)
        {
            var gesture = ShortcutBindings.EffectiveGesture(action.Id, _working.ShortcutOverrides);
            var hasOverride = _working.ShortcutOverrides.ContainsKey(action.Id);
            var row = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 3),
            };
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = Strings.Get("shortcut." + action.Id),
                Width = 170,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var keyButton = new System.Windows.Controls.Button
            {
                Content = gesture.DisplayText(),
                Width = 150,
                Tag = action.Id,
                ToolTip = Strings.Format("ui.click_then_press_keys_for", Strings.Get("shortcut." + action.Id)),
            };
            keyButton.Click += OnCaptureShortcut;
            row.Children.Add(keyButton);
            var reset = new System.Windows.Controls.Button
            {
                Content = Strings.Get("ui.reset"),
                Width = 64,
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = hasOverride,
                Tag = action.Id,
                ToolTip = Strings.Format("ui.put_shortcut_back_to", Strings.Get("shortcut." + action.Id), action.DisplayGestures),
            };
            reset.Click += OnResetShortcut;
            row.Children.Add(reset);
            ShortcutRows.Children.Add(row);
        }
    }

    private void OnCaptureShortcut(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string actionId)
            return;
        _capturingActionId = actionId;
        button.Content = Strings.Get("ui.press_keys_esc_cancels");
        ShortcutStatus.Text = "";
        button.Focus();
    }

    /// <summary>
    /// Window-level capture: while a shortcut button is armed, the next
    /// real key press becomes that action's gesture (Esc cancels, Tab
    /// just leaves capture so keyboard users are never trapped).
    /// Modifier-only presses are ignored — a gesture needs a real key.
    /// The S11 global-hotkey capture rides the same preamble; only the
    /// validation/application at the end differs (Core's GlobalHotkey
    /// instead of ShortcutBindings).
    /// </summary>
    private void OnSettingsPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_capturingActionId is null && !_capturingGlobalHotkey && !_capturingQuickNoteHotkey)
            return;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key == System.Windows.Input.Key.Tab)
        {
            CancelCapture();
            return; // not handled — focus moves on as usual
        }
        e.Handled = true;
        if (key == System.Windows.Input.Key.Escape)
        {
            CancelCapture();
            return;
        }
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin
            or System.Windows.Input.Key.System)
            return; // wait for the real key
        var keyboard = System.Windows.Input.Keyboard.Modifiers;
        var mods = GestureModifiers.None;
        if (keyboard.HasFlag(System.Windows.Input.ModifierKeys.Control)) mods |= GestureModifiers.Control;
        if (keyboard.HasFlag(System.Windows.Input.ModifierKeys.Alt)) mods |= GestureModifiers.Alt;
        if (keyboard.HasFlag(System.Windows.Input.ModifierKeys.Shift)) mods |= GestureModifiers.Shift;
        var gesture = new ShortcutGesture(mods, key.ToString());

        if (_capturingGlobalHotkey)
        {
            _capturingGlobalHotkey = false;
            var okGlobal = GlobalHotkey.TrySetGesture(_working.ShortcutOverrides, gesture, out var globalError);
            if (okGlobal)
                _working.GlobalHotkeyGesture = gesture.Format();
            RefreshGlobalHotkeyControls(okGlobal
                ? Strings.Format("set.hotkey_now", gesture.DisplayText())
                : globalError ?? "");
            return;
        }

        if (_capturingQuickNoteHotkey)
        {
            _capturingQuickNoteHotkey = false;
            var okNote = QuickNoteHotkey.TrySetGesture(
                _working.ShortcutOverrides, _working.GlobalHotkeyGesture, gesture, out var noteError);
            if (okNote)
                _working.QuickNoteHotkeyGesture = gesture.Format();
            RefreshQuickNoteHotkeyControls(okNote
                ? Strings.Format("set.hotkey_note_now", gesture.DisplayText())
                : noteError ?? "");
            return;
        }

        var actionId = _capturingActionId!;
        var ok = ShortcutBindings.TrySet(
            _working.ShortcutOverrides, actionId, gesture,
            out var updated, out var error);
        // S11 composition rule: an in-app key may not take the global
        // hotkey's combination — RegisterHotKey would consume it
        // system-wide and the in-app binding would never fire.
        if (ok && gesture == EffectiveGlobalGesture())
        {
            ok = false;
            error = Strings.Format("set.hotkey_clash_global", gesture.DisplayText());
        }
        // R5: the same rule for the quick-note hotkey, while it's on.
        if (ok && _working.QuickNoteHotkeyEnabled && gesture == EffectiveQuickNoteGesture())
        {
            ok = false;
            error = Strings.Format("set.hotkey_clash_note", gesture.DisplayText());
        }
        if (ok)
            _working.ShortcutOverrides = updated;
        _capturingActionId = null;
        RebuildShortcutRows();
        ShortcutStatus.Text = ok
            ? Strings.Format("set.hotkey_action_now", ShortcutCatalog.Find(actionId)?.Name, ShortcutBindings.EffectiveGesture(actionId, _working.ShortcutOverrides).DisplayText())
            : error ?? "";
    }

    private void CancelCapture()
    {
        _capturingActionId = null;
        _capturingGlobalHotkey = false;
        _capturingQuickNoteHotkey = false;
        RebuildShortcutRows();
        RefreshGlobalHotkeyControls();
        RefreshQuickNoteHotkeyControls();
    }

    // ───────────────────────── S11: the global hotkey editor (#96) ─────────────────────────

    /// <summary>True while the global-hotkey button is armed for capture.</summary>
    private bool _capturingGlobalHotkey;

    /// <summary>The working settings' effective global gesture: the stored one, or Core's default.</summary>
    private ShortcutGesture EffectiveGlobalGesture() =>
        _working.GlobalHotkeyGesture is { } text && ShortcutGesture.TryParse(text, out var parsed)
            ? parsed
            : GlobalHotkey.DefaultGesture;

    private void RefreshGlobalHotkeyControls(string? statusOverride = null)
    {
        GlobalHotkeyCheck.IsChecked = _working.GlobalHotkeyEnabled;
        GlobalHotkeyButton.Content = EffectiveGlobalGesture().DisplayText();
        GlobalHotkeyStatus.Text = statusOverride ?? _globalHotkeyStatus?.Invoke() ?? "";
    }

    private void OnCaptureGlobalHotkey(object sender, RoutedEventArgs e)
    {
        _capturingGlobalHotkey = true;
        GlobalHotkeyButton.Content = Strings.Get("ui.press_keys_esc_cancels");
        GlobalHotkeyStatus.Text = "";
        GlobalHotkeyButton.Focus();
    }

    private void OnResetGlobalHotkey(object sender, RoutedEventArgs e)
    {
        _working.GlobalHotkeyGesture = null;
        RefreshGlobalHotkeyControls(
            Strings.Format("set.hotkey_back", GlobalHotkey.DefaultGesture.DisplayText()));
    }

    // ───────────────────────── R5: the quick-note hotkey editor (#148) ─────────────────────────

    /// <summary>True while the quick-note hotkey button is armed for capture.</summary>
    private bool _capturingQuickNoteHotkey;

    /// <summary>The working settings' effective quick-note gesture: the stored one, or Core's default.</summary>
    private ShortcutGesture EffectiveQuickNoteGesture() =>
        _working.QuickNoteHotkeyGesture is { } text && ShortcutGesture.TryParse(text, out var parsed)
            ? parsed
            : QuickNoteHotkey.DefaultGesture;

    private void RefreshQuickNoteHotkeyControls(string? statusOverride = null)
    {
        QuickNoteHotkeyCheck.IsChecked = _working.QuickNoteHotkeyEnabled;
        QuickNoteHotkeyButton.Content = EffectiveQuickNoteGesture().DisplayText();
        QuickNoteHotkeyStatus.Text = statusOverride ?? _quickNoteHotkeyStatus?.Invoke() ?? "";
    }

    private void OnCaptureQuickNoteHotkey(object sender, RoutedEventArgs e)
    {
        _capturingQuickNoteHotkey = true;
        QuickNoteHotkeyButton.Content = Strings.Get("ui.press_keys_esc_cancels");
        QuickNoteHotkeyStatus.Text = "";
        QuickNoteHotkeyButton.Focus();
    }

    private void OnResetQuickNoteHotkey(object sender, RoutedEventArgs e)
    {
        _working.QuickNoteHotkeyGesture = null;
        RefreshQuickNoteHotkeyControls(
            Strings.Format("set.hotkey_note_back", QuickNoteHotkey.DefaultGesture.DisplayText()));
    }

    // ───────────────────────── R5: scheduled commands (#147) ─────────────────────────

    /// <summary>The schedules list shows Core's Describe line per rule; selection maps back by index (the list is rebuilt on every edit).</summary>
    private void RebuildScheduleRows()
    {
        SchedulesList.ItemsSource = _working.CommandSchedules
            .Select(r => r.Describe())
            .ToList();
    }

    private ScheduledCommand? SelectedSchedule() =>
        SchedulesList.SelectedIndex >= 0 && SchedulesList.SelectedIndex < _working.CommandSchedules.Count
            ? _working.CommandSchedules[SchedulesList.SelectedIndex]
            : null;

    private void OnAddSchedule(object sender, RoutedEventArgs e)
    {
        if (_working.CommandSchedules.Count >= CommandScheduler.MaxRules)
        {
            System.Windows.MessageBox.Show(this,
                Strings.Format("set.schedules_cap", CommandScheduler.MaxRules),
                Strings.Get("scheduled_commands"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new ScheduleRuleWindow { Owner = this };
        dialog.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(dialog);
        if (dialog.ShowDialog() == true && dialog.Rule is { } rule)
        {
            _working.CommandSchedules.Add(rule);
            RebuildScheduleRows();
        }
    }

    private void OnEditSchedule(object sender, RoutedEventArgs e)
    {
        if (SelectedSchedule() is not { } existing)
            return;
        var dialog = new ScheduleRuleWindow(existing) { Owner = this };
        dialog.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(dialog);
        if (dialog.ShowDialog() == true && dialog.Rule is { } rule)
        {
            var index = _working.CommandSchedules.IndexOf(existing);
            if (index >= 0)
                _working.CommandSchedules[index] = rule;
            RebuildScheduleRows();
        }
    }

    private void OnRemoveSchedule(object sender, RoutedEventArgs e)
    {
        if (SelectedSchedule() is not { } existing)
            return;
        _working.CommandSchedules.Remove(existing);
        RebuildScheduleRows();
    }

    // ───────────────────────── R5: the reports (#146/#150) ─────────────────────────

    private void OnOpenWeeklyDigest(object sender, RoutedEventArgs e) => _openWeeklyDigest?.Invoke();

    private void OnOpenStatsDashboard(object sender, RoutedEventArgs e) => _openStatsDashboard?.Invoke();

    private void OnResetShortcut(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string actionId })
        {
            _working.ShortcutOverrides = ShortcutBindings.Without(_working.ShortcutOverrides, actionId);
            ShortcutStatus.Text = "";
            RebuildShortcutRows();
        }
    }

    private void OnResetAllShortcuts(object sender, RoutedEventArgs e)
    {
        _working.ShortcutOverrides.Clear();
        ShortcutStatus.Text = Strings.Get("ui.all_shortcuts_are_back_to_their_defaults");
        RebuildShortcutRows();
    }

    private void OnOpenShortcutsList(object sender, RoutedEventArgs e)
    {
        var dialog = new ShortcutsWindow(_working) { Owner = this };
        FocusReturn.ShowDialog(dialog);
    }

    // ───────────────────────── S5: search, memory, appearance, reset, wizard ─────────────────────────

    /// <summary>Section registry (S5 #41/#48), built in the constructor: (Core section id, container, header) in layout order.</summary>
    private readonly (string Id, System.Windows.Controls.StackPanel Container, System.Windows.Controls.TextBlock Header)[] _sections = null!;

    /// <summary>Accent keys parallel to AccentCombo's items (S5 #44).</summary>
    private readonly List<string> _accentKeys = new();

    /// <summary>The section currently at the top of the viewport (S5 #48), tracked by the scroll handler.</summary>
    private string? _currentSectionId;

    private void OnSettingsLoaded(object sender, RoutedEventArgs e)
    {
        // S5 #48: scroll back to the section Settings was left on.
        if (_working.SettingsLastSection is { } last)
        {
            foreach (var (id, container, _) in _sections)
            {
                if (id == last)
                {
                    _currentSectionId = last;
                    Dispatcher.BeginInvoke(new Action(() => container.BringIntoView()));
                    break;
                }
            }
        }
    }

    private void OnSettingsScrolled(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        // The current section is the last visible one whose top has
        // reached the viewport top (within a small slack) — the same
        // "where am I" a reader would report.
        try
        {
            string? current = null;
            foreach (var (id, container, _) in _sections)
            {
                if (container.Visibility != Visibility.Visible)
                    continue;
                if (container.TranslatePoint(new System.Windows.Point(0, 0), SettingsScroller).Y <= 24)
                    current = id;
            }
            if (current is not null)
                _currentSectionId = current;
        }
        catch { /* layout not ready — the previous value stands */ }
    }

    // ── #41 search: section-level filter + highlight ──

    private void OnSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ApplySearch();

    private void ApplySearch()
    {
        var query = SettingsSearchText.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            foreach (var (_, container, header) in _sections)
            {
                container.Visibility = Visibility.Visible;
                header.Background = null;
            }
            SearchSummary.Text = "";
            return;
        }
        var matches = SettingsSearch.Search(query);
        var matchIds = new HashSet<string>(matches.Select(m => m.Id));
        System.Windows.FrameworkElement? firstMatch = null;
        foreach (var (id, container, header) in _sections)
        {
            var hit = matchIds.Contains(id);
            container.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
            header.Background = hit ? SearchHighlightBrush() : null;
            // R1 #108: search results are ranked now — the window
            // scrolls to the BEST match, not the first in layout order.
            if (hit && matches.Count > 0 && id == matches[0].Id)
                firstMatch = container;
        }
        SearchSummary.Text = matches.Count == 0
            ? Strings.Get("set.search_none")
            : Strings.Format("set.search_some", matches.Count, _sections.Length);
        if (firstMatch is not null)
            Dispatcher.BeginInvoke(new Action(() => firstMatch.BringIntoView()));
    }

    /// <summary>A translucent wash of the current accent behind a matched section's header.</summary>
    private static System.Windows.Media.Brush SearchHighlightBrush()
    {
        if (System.Windows.Application.Current?.Resources["AccentBrush"] is System.Windows.Media.SolidColorBrush accent)
        {
            var color = accent.Color;
            color.A = 0x40;
            return new System.Windows.Media.SolidColorBrush(color);
        }
        return System.Windows.Media.Brushes.Transparent;
    }

    // ── #45 toolbar editor: write-through into the working settings ──

    /// <summary>
    /// R6 #158: the custom-accent swatch previews the color in the
    /// text box as the user types (the box + checkbox are wired in
    /// the constructor). An unreadable-as-a-color entry shows the
    /// transparent swatch — the collect path treats it as "no custom
    /// accent" the same way.
    /// </summary>
    private void RefreshCustomAccentSwatch()
    {
        var hex = Core.CustomAccent.TryNormalize(CustomAccentText.Text);
        CustomAccentSwatch.Background = hex is not null
            ? new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex))
            : System.Windows.Media.Brushes.Transparent;
    }

    private void RebuildToolbarEditor()
    {
        ToolbarList.Items.Clear();
        foreach (var id in _working.ToolbarOrder)
        {
            var item = ToolbarLayout.Find(id);
            if (item is null)
                continue;
            var check = new System.Windows.Controls.CheckBox
            {
                Content = item.DisplayName,
                IsChecked = !_working.ToolbarHidden.Contains(id),
                IsEnabled = item.CanHide,
                Tag = id,
            };
            check.Click += OnToolbarItemToggle;
            ToolbarList.Items.Add(new System.Windows.Controls.ListBoxItem { Content = check, Tag = id });
        }
    }

    private void OnToolbarItemToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox check || check.Tag is not string id)
            return;
        if (check.IsChecked == true)
            _working.ToolbarHidden.Remove(id);
        else if (ToolbarLayout.Find(id)?.CanHide == true && !_working.ToolbarHidden.Contains(id))
            _working.ToolbarHidden.Add(id);
    }

    private void OnToolbarMoveUp(object sender, RoutedEventArgs e) => MoveToolbarSelection(-1);

    private void OnToolbarMoveDown(object sender, RoutedEventArgs e) => MoveToolbarSelection(+1);

    private void MoveToolbarSelection(int delta)
    {
        var index = ToolbarList.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _working.ToolbarOrder.Count)
            return;
        (_working.ToolbarOrder[index], _working.ToolbarOrder[target]) =
            (_working.ToolbarOrder[target], _working.ToolbarOrder[index]);
        RebuildToolbarEditor();
        ToolbarList.SelectedIndex = target;
    }

    private void OnToolbarReset(object sender, RoutedEventArgs e)
    {
        _working.ToolbarOrder = ToolbarLayout.DefaultOrder();
        _working.ToolbarHidden = new List<string>();
        RebuildToolbarEditor();
    }

    // ── #50 re-run the setup wizard against the working clone ──

    private void OnRunSetupWizard(object sender, RoutedEventArgs e)
    {
        HarvestControls();
        var wizard = new SetupWizardWindow(_working, markCompletedOnSkip: false) { Owner = this };
        if (FocusReturn.ShowDialog(wizard) == true)
            LoadControls(); // the wizard's four choices refresh this window
    }

    // ── #42 reset all settings (backup first, always) ──

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        HarvestControls();
        var backupName = SettingsReset.BackupFileName(DateTimeOffset.Now);
        var backupPath = Path.Combine(AppPaths.RootDir, backupName);
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Format("set.reset_body", backupPath),
            Strings.Get("set.reset_caption"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        FocusReturn.Restore(invoker);
        try
        {
            Directory.CreateDirectory(AppPaths.RootDir);
            File.WriteAllText(backupPath, SettingsStore.Export(_working));
        }
        catch (Exception ex)
        {
            // No backup, no reset — forgiving means never destructive.
            ResetNote.Text = Strings.Format("set.reset_backup_write_failed", ex.Message);
            _log.Warn($"Settings reset aborted — backup failed: {ex.Message}");
            return;
        }
        SettingsReset.ApplyDefaults(_working);
        LoadControls();
        ResetNote.Text = Strings.Format("set.reset_done", backupName);
        _log.Info("Settings reset to defaults from Settings window (backup written first)");
    }

    // ── #72 guided reset ──

    private void OnOpenResetWizard(object sender, RoutedEventArgs e)
    {
        HarvestControls();
        var wizard = new ResetWizardWindow(_working, _privacy, _log) { Owner = this };
        ThemeManager.ApplyTitleBar(wizard);
        FocusReturn.ShowDialog(wizard);
        if (wizard.SettingsWereReset)
            LoadControls();
        // Site data may have been cleared from the wizard — refresh
        // the live rows either way.
        _ = RefreshSiteDataAsync();
    }

    // ── #79 rotating backups ──

    /// <summary>Rebuild the backup list: one row per kept backup (newest first), each with its own Restore.</summary>
    private void RefreshBackupRows()
    {
        BackupRows.Children.Clear();
        var catalog = BackupRotation.Catalog(BackupService.ExistingNames());
        if (catalog.Count == 0)
        {
            BackupRows.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = Strings.Get("ui.no_backups_yet_the_first_automatic_one"),
                Style = (Style)FindResource("Note"),
            });
            return;
        }
        foreach (var backup in catalog)
        {
            var label = new System.Windows.Controls.TextBlock
            {
                Text = Strings.Format("set.backup_row", Strings.Get(backup.Kind == "settings" ? "set.backup_kind_settings" : "set.backup_kind_downloads"), backup.Stamp.LocalDateTime),
                Width = 300,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var restore = new System.Windows.Controls.Button
            {
                Content = Strings.Get("ui.restore"),
                Width = 76,
                Margin = new Thickness(4, 0, 0, 0),
                Tag = backup,
                ToolTip = backup.Kind == "settings"
                    ? Strings.Get("set.backup_tip_settings")
                    : Strings.Get("set.backup_tip_downloads"),
            };
            restore.Click += OnRestoreBackup;
            var line = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2),
            };
            line.Children.Add(label);
            line.Children.Add(restore);
            BackupRows.Children.Add(line);
        }
    }

    private void OnBackupNow(object sender, RoutedEventArgs e)
    {
        HarvestControls();
        if (BackupService.CreateBackup(_log, automatic: false) is { } written)
        {
            BackupNote.Text = Strings.Format("set.backup_done", string.Join(", ", written));
            RefreshBackupRows();
        }
        else
        {
            BackupNote.Text = Strings.Get("ui.the_backup_couldn_t_be_written_the_log");
        }
    }

    private void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: BackupFileInfo backup })
            return;
        var path = Path.Combine(AppPaths.BackupsDir, backup.FileName);
        var invoker = FocusReturn.Capture();
        if (backup.Kind == "settings")
        {
            var confirm = System.Windows.MessageBox.Show(this,
                Strings.Format("set.restore_settings_body", backup.Stamp.LocalDateTime),
                Strings.Get("set.restore_settings_caption"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            FocusReturn.Restore(invoker);
            if (confirm != MessageBoxResult.Yes)
                return;
            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception ex)
            {
                BackupNote.Text = Strings.Format("set.backup_read_failed", ex.Message);
                return;
            }
            if (!SettingsStore.TryImport(json, out var imported, out var error) || imported is null)
            {
                BackupNote.Text = Strings.Format("set.backup_not_settings", error);
                return;
            }
            CopySettingsInto(imported, _working);
            LoadControls();
            BackupNote.Text = Strings.Format("set.settings_restored", backup.Stamp.LocalDateTime);
            _log.Info($"Settings restored from rotating backup {backup.FileName}");
            return;
        }
        var confirmDownloads = System.Windows.MessageBox.Show(this,
            Strings.Format("set.restore_downloads_body", backup.Stamp.LocalDateTime),
            Strings.Get("set.restore_downloads_caption"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        FocusReturn.Restore(invoker);
        if (confirmDownloads != MessageBoxResult.Yes)
            return;
        try
        {
            Directory.CreateDirectory(AppPaths.RootDir);
            File.Copy(path, AppPaths.DownloadsHistoryPath, overwrite: true);
            BackupNote.Text = Strings.Format("set.downloads_restored", backup.Stamp.LocalDateTime);
            _log.Info($"Downloads history restored from rotating backup {backup.FileName}");
        }
        catch (Exception ex)
        {
            BackupNote.Text = Strings.Format("set.history_restore_failed", ex.Message);
        }
    }

    // ───────────────────────── settings export / import (v0.4.0) ─────────────────────────

    private void OnExportSettings(object sender, RoutedEventArgs e)
    {
        HarvestControls();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "musedesktop-settings.json",
            Filter = Strings.Get("dlg.filter_json"),
            Title = Strings.Get("ui.export_musedesktop_settings"),
        };
        var invoker = FocusReturn.Capture();
        var chosen = dialog.ShowDialog(this) == true;
        FocusReturn.Restore(invoker);
        if (!chosen)
            return;
        // S7 #65: offer the encrypted form first (it's the safer
        // default for a file that may leave this PC). The unencrypted
        // warning is stated before the file is written, not after.
        invoker = FocusReturn.Capture();
        var encrypt = System.Windows.MessageBox.Show(this,
            Strings.Get("set.export_body") +
            (AppLock.HasValidRecord(_working) ? Strings.Get("set.export_body_hash") : "") + ".",
            Strings.Get("set.export_caption"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        FocusReturn.Restore(invoker);
        if (encrypt == MessageBoxResult.Cancel)
            return;
        string contents;
        if (encrypt == MessageBoxResult.Yes)
        {
            var prompt = new PasswordPromptWindow(
                Strings.Get("set.export_password_choose"),
                confirm: true) { Owner = this };
            if (FocusReturn.ShowDialog(prompt) != true)
                return;
            contents = EncryptedExport.Encrypt(SettingsStore.Export(_working), prompt.Password);
        }
        else
        {
            contents = SettingsStore.Export(_working);
        }
        try
        {
            File.WriteAllText(dialog.FileName, contents);
            ImportNote.Text = encrypt == MessageBoxResult.Yes
                ? Strings.Format("set.export_encrypted_done", dialog.FileName)
                : Strings.Format("set.export_plain_done", dialog.FileName);
            _log.Info(encrypt == MessageBoxResult.Yes
                ? "Settings exported (encrypted) from Settings window"
                : "Settings exported from Settings window");
        }
        catch (Exception ex)
        {
            _log.Warn($"Settings export failed: {ex.Message}");
            ImportNote.Text = Strings.Format("set.file_write_failed", ex.Message);
        }
    }

    private void OnImportSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Strings.Get("dlg.filter_json"),
            Title = Strings.Get("ui.import_musedesktop_settings"),
        };
        var invoker = FocusReturn.Capture();
        var chosen = dialog.ShowDialog(this) == true;
        FocusReturn.Restore(invoker);
        if (!chosen)
            return;
        string json;
        try
        {
            json = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            ImportNote.Text = Strings.Format("set.file_read_failed", ex.Message);
            return;
        }
        // S7 #65: an encrypted export is detected by its envelope and
        // asks for its password; wrong password and damaged file are
        // different messages, from Core's format code.
        if (EncryptedExport.IsEncrypted(json))
        {
            var prompt = new PasswordPromptWindow(
                Strings.Get("set.import_password_prompt"),
                confirm: false) { Owner = this };
            if (FocusReturn.ShowDialog(prompt) != true)
                return;
            if (!EncryptedExport.TryDecrypt(json, prompt.Password, out var plain, out _, out var decryptMessage)
                || plain is null)
            {
                ImportNote.Text = decryptMessage ?? Strings.Get("set.import_decrypt_failed");
                return;
            }
            json = plain;
        }
        if (!SettingsStore.TryImport(json, out var imported, out var error) || imported is null)
        {
            ImportNote.Text = error ?? Strings.Get("set.bundle_import_failed");
            return;
        }
        CopySettingsInto(imported, _working);
        LoadControls();
        ImportNote.Text = Strings.Get("ui.settings_imported_choose_ok_to_apply");
        _log.Info("Settings imported from Settings window");
    }

    /// <summary>Overwrite every field of <paramref name="target"/> with <paramref name="source"/>'s (the window keeps editing the same clone).</summary>
    private static void CopySettingsInto(AppSettings source, AppSettings target)
    {
        target.SchemaVersion = source.SchemaVersion;
        target.WindowLeft = source.WindowLeft;
        target.WindowTop = source.WindowTop;
        target.WindowWidth = source.WindowWidth;
        target.WindowHeight = source.WindowHeight;
        target.WindowMaximized = source.WindowMaximized;
        target.ZoomFactor = source.ZoomFactor;
        target.CloseToTray = source.CloseToTray;
        target.StartWithWindows = source.StartWithWindows;
        target.DownloadsFolder = source.DownloadsFolder;
        target.AskWhereToSave = source.AskWhereToSave;
        target.NotifyWhenDownloadCompletes = source.NotifyWhenDownloadCompletes;
        target.DownloadHistoryCap = source.DownloadHistoryCap;
        target.CollisionPolicy = source.CollisionPolicy;
        target.AlwaysOnTop = source.AlwaysOnTop;
        target.WindowPlacementByProfile = source.WindowPlacementByProfile;
        target.CheckForUpdates = source.CheckForUpdates;
        target.SuspendWhenInTray = source.SuspendWhenInTray;
        target.StartMinimizedToTray = source.StartMinimizedToTray;
        target.TraySuspendTimeout = source.TraySuspendTimeout;
        target.TraySingleClickRestore = source.TraySingleClickRestore;
        target.FlashTaskbarOnAttention = source.FlashTaskbarOnAttention;
        target.QuietHoursEnabled = source.QuietHoursEnabled;
        target.QuietHoursStartMinutes = source.QuietHoursStartMinutes;
        target.QuietHoursEndMinutes = source.QuietHoursEndMinutes;
        target.Profiles = source.Profiles;
        target.ActiveProfileId = source.ActiveProfileId;
        target.PendingProfileFolderDeletions = source.PendingProfileFolderDeletions;
        target.LastUrl = source.LastUrl;
        target.LastUrlByProfile = source.LastUrlByProfile;
        // S5 fields (appearance, Settings-window memory, first-run flag).
        target.Theme = source.Theme;
        target.AccentColor = source.AccentColor;
        // R6 fields: mode + accent + backdrop are the theme bundle
        // (#160) — they travel inside the settings export/import with
        // the rest of the round-2 face.
        target.CustomAccent = source.CustomAccent;
        target.Backdrop = source.Backdrop;
        target.TitleBarProfileTint = source.TitleBarProfileTint;
        target.Density = source.Density;
        target.TrayIconStyle = source.TrayIconStyle;
        target.TrayCountOverlay = source.TrayCountOverlay;
        target.ThemeLightStartMinutes = source.ThemeLightStartMinutes;
        target.ThemeDarkStartMinutes = source.ThemeDarkStartMinutes;
        target.ShellFontScale = source.ShellFontScale;
        target.ToolbarOrder = source.ToolbarOrder;
        target.ToolbarHidden = source.ToolbarHidden;
        target.SettingsLastSection = source.SettingsLastSection;
        target.SettingsWindowWidth = source.SettingsWindowWidth;
        target.SettingsWindowHeight = source.SettingsWindowHeight;
        target.FirstRunCompleted = source.FirstRunCompleted;
        // S6 fields (shortcut remaps, reduce motion, extended zoom).
        target.ShortcutOverrides = source.ShortcutOverrides;
        target.ReduceMotion = source.ReduceMotion;
        target.ExtendedZoomRange = source.ExtendedZoomRange;
        // S7 fields (autofill toggles #68, app lock #64 — the record
        // travels with exports/imports like everything else; a plain
        // export exposes the salted hash, never a password, and the
        // fail-open sanitize means a bad record can never lock
        // anyone out).
        target.PasswordAutosaveEnabled = source.PasswordAutosaveEnabled;
        target.GeneralAutofillEnabled = source.GeneralAutofillEnabled;
        target.AppLockEnabled = source.AppLockEnabled;
        target.AppLockSalt = source.AppLockSalt;
        target.AppLockHash = source.AppLockHash;
        target.AppLockIterations = source.AppLockIterations;
        target.AppLockOnTrayHide = source.AppLockOnTrayHide;
        // S9 fields (update channel/frequency/snooze, What's New
        // seen-state + opt-out, kept/previous installer records) —
        // state records travel with exports too: importing settings
        // onto the same PC keeps the rollback offer intact.
        target.UpdateChannel = source.UpdateChannel;
        target.UpdateCheckFrequency = source.UpdateCheckFrequency;
        target.LastUpdateCheckUtc = source.LastUpdateCheckUtc;
        target.UpdateSnoozedUntilUtc = source.UpdateSnoozedUntilUtc;
        target.LastSeenVersion = source.LastSeenVersion;
        target.ShowWhatsNew = source.ShowWhatsNew;
        target.KeptInstaller = source.KeptInstaller;
        target.PreviousInstaller = source.PreviousInstaller;
        // S11 fields (the global hotkey + the links toggle).
        target.GlobalHotkeyEnabled = source.GlobalHotkeyEnabled;
        target.GlobalHotkeyGesture = source.GlobalHotkeyGesture;
        target.RegisterProtocolLinks = source.RegisterProtocolLinks;
    }
}
