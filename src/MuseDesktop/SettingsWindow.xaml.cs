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

    public SettingsWindow(AppSettings working, DiagnosticLog log, WebPrivacyGateway? privacy,
        Action<AppSettings, string> switchProfile)
    {
        _working = working;
        _log = log;
        _privacy = privacy;
        _switchProfile = switchProfile;
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
            (SettingsSearch.AppearanceId, SectionAppearance, HeaderAppearance),
            (SettingsSearch.ZoomId, SectionZoom, HeaderZoom),
            (SettingsSearch.AccessibilityId, SectionAccessibility, HeaderAccessibility),
            (SettingsSearch.DownloadsId, SectionDownloads, HeaderDownloads),
            (SettingsSearch.DataId, SectionData, HeaderData),
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
        ThemeCombo.Items.Add("Dark");
        ThemeCombo.Items.Add("Light");
        ThemeCombo.Items.Add("Follow Windows");
        foreach (var (key, _, _) in AccentPalette.Palette)
        {
            _accentKeys.Add(key);
            AccentCombo.Items.Add(key);
        }
        foreach (var factor in ShellTextScale.Allowed)
            TextScaleCombo.Items.Add(ShellTextScale.FormatPercent(factor));

        // S3 #24: accent-color choices — "None" then the fixed palette.
        _colorKeys.Add(null);
        ProfileColorCombo.Items.Add("None");
        foreach (var (key, _) in ProfileColors.Palette)
        {
            _colorKeys.Add(key);
            ProfileColorCombo.Items.Add(key);
        }

        // S2 (#16/#21): collision-policy and history-cap choices.
        CollisionPolicyCombo.Items.Add("Save as a new name");
        CollisionPolicyCombo.Items.Add("Ask where to save");
        foreach (var cap in DownloadHistoryStore.AllowedCaps)
            HistoryCapCombo.Items.Add(cap.ToString());

        // S8 #75: the per-profile data-cap choices (Off / 500 MB /
        // 1 GB / 2 GB), parallel to ProfileDataCaps.Allowed.
        foreach (var cap in ProfileDataCaps.Allowed)
            ProfileDataCapCombo.Items.Add(ProfileDataCaps.Label(cap));

        // S7 #61: the clear-data choices, titled by Core's plan model
        // so the UI and the confirm dialog always agree.
        foreach (var choice in ClearChoices)
            ClearChoiceCombo.Items.Add(ClearDataPlan.Title(choice));
        ClearScopeCombo.Items.Add("This profile only");
        ClearScopeCombo.Items.Add("All profiles");
        ClearChoiceCombo.SelectedIndex = 2; // All site data — the pre-v0.11.0 behavior, one click away
        ClearScopeCombo.SelectedIndex = 0;

        // S4 (#34/#38): suspend-timeout and quiet-hours choices. The
        // quiet-hours combos offer whole hours only — minute precision
        // would be settings-file-only precision theatre.
        foreach (var option in SuspendTimeoutChoices)
            SuspendTimeoutCombo.Items.Add(option.Label);
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
        (SuspendTimeout.OneMinute, "1 minute"),
        (SuspendTimeout.FiveMinutes, "5 minutes"),
        (SuspendTimeout.FifteenMinutes, "15 minutes"),
        (SuspendTimeout.Never, "Never"),
    };

    /// <summary>(Re)fill every control from the working settings — at open and after an import.</summary>
    private void LoadControls()
    {
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
        AskWhereToSaveCheck.IsChecked = _working.AskWhereToSave;
        NotifyDownloadCompleteCheck.IsChecked = _working.NotifyWhenDownloadCompletes;
        CollisionPolicyCombo.SelectedIndex = _working.CollisionPolicy == DownloadCollisionPolicy.AskWhereToSave ? 1 : 0;
        HistoryCapCombo.SelectedItem = DownloadHistoryStore.SanitizeCap(_working.DownloadHistoryCap).ToString();
        DownloadsFolderText.Text = _working.DownloadsFolder ?? "";

        // S6: extended zoom range (its handler rebuilds the step lists
        // when it changes), reduce motion, the shortcut editor rows.
        ExtendedZoomCheck.IsChecked = _working.ExtendedZoomRange;
        RebuildZoomCombos();
        ReduceMotionCombo.SelectedIndex = (int)_working.ReduceMotion;
        RebuildShortcutRows();

        var current = ZoomMath.FormatPercent(_working.ZoomFactor);
        ZoomCombo.SelectedItem = ZoomCombo.Items.Contains(current) ? current : "100%";

        // S5: appearance controls + the toolbar editor + the data note.
        ThemeCombo.SelectedIndex = (int)_working.Theme;
        var accentIndex = _accentKeys.IndexOf(_working.AccentColor);
        AccentCombo.SelectedIndex = accentIndex >= 0 ? accentIndex : 0;
        TextScaleCombo.SelectedItem = ShellTextScale.FormatPercent(_working.ShellFontScale);
        RebuildToolbarEditor();
        PortableNote.Text = AppPaths.IsPortable
            ? $"Portable mode is ON: settings, download history, and logs live in {AppPaths.RootDir} — a data folder beside the app. Sign-in data still lives in your user profile (see README, “Where things live”)."
            : $"Settings, download history, and logs live in {AppPaths.RootDir}. For portable use, put an empty file named portable.txt beside MuseDesktop.exe and restart: settings then live in a data folder beside the app and start fresh there — nothing is moved automatically.";

        RefreshProfileCombo(_working.ActiveProfileId);

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
        // S5 appearance (the toolbar layout itself is write-through —
        // see the toolbar editor handlers).
        if (ThemeCombo.SelectedIndex >= 0 && ThemeCombo.SelectedIndex <= (int)Core.ThemeMode.FollowSystem)
            _working.Theme = (Core.ThemeMode)ThemeCombo.SelectedIndex;
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
        ProfileCombo.DisplayMemberPath = nameof(AccountProfile.Name);
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
        var items = new List<string> { "Don't copy settings" };
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
        _syncingProfileControls = false;
        ProfileDataText.Text = selected is null
            ? ""
            : ProfileDataInfo.Describe(AppPaths.ProfileDir(selected.Id), selected);
        ProfileCapNote.Text = selected is null ? "" : DescribeCap(selected);
    }

    /// <summary>The #75 cap line for the selected profile: the cap, the promise, and the last cleanup (never silent).</summary>
    private static string DescribeCap(AccountProfile profile)
    {
        var cleaned = profile.LastCacheCleanupUtc is { } utc
            ? $" Last cleanup: {utc.LocalDateTime:MMM d, HH:mm} — freed {DownloadText.FormatBytes(profile.LastCacheCleanupFreedBytes)}."
            : "";
        return profile.DataCapBytes <= 0
            ? "No size cap: this profile's data folder grows as Muse uses it." + cleaned
            : $"When this profile's data folder passes {ProfileDataCaps.Label(profile.DataCapBytes)}, only its disposable caches are emptied, oldest first — cookies and sign-ins are never touched." + cleaned;
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
            Title = $"Choose where profile \"{selected.Name}\" saves downloads",
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
                ? "This profile will start at that Muse page."
                : "That address can't be used — it must be an https:// muse.ai page. Until it is fixed, this profile starts at the Muse home page instead.";
    }

    // ── S3: color palette keys parallel to ProfileColorCombo items ──
    private readonly List<string?> _colorKeys = new();
    private readonly List<string?> _copyFromIds = new();
    private string? _copyFromId;
    private bool _syncingProfileControls;

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

    /// <summary>Per-profile desktop shortcut (S3 #23).</summary>
    private void OnCreateProfileShortcut(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } selected)
            return;
        var error = ProfileShortcut.CreateOnDesktop(selected, _log);
        ProfileNote.Text = error ?? $"Desktop shortcut created: {Profiles.ShortcutFileName(selected)}";
    }

    private void OnProfileSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateProfileButtons();

    private void OnAddProfile(object sender, RoutedEventArgs e)
    {
        if (_working.Profiles.Count >= Profiles.MaxProfiles)
        {
            var capInvoker = FocusReturn.Capture();
            System.Windows.MessageBox.Show(this,
                $"MuseDesktop supports up to {Profiles.MaxProfiles} profiles.",
                "Profiles", MessageBoxButton.OK, MessageBoxImage.Information);
            FocusReturn.Restore(capInvoker);
            return;
        }
        // S3 #30: past the soft threshold, adding is a deliberate choice —
        // every profile keeps its own sign-in data on this PC.
        if (_working.Profiles.Count >= Profiles.SoftProfileWarningThreshold)
        {
            var warnInvoker = FocusReturn.Capture();
            var warn = System.Windows.MessageBox.Show(this,
                $"You already have {_working.Profiles.Count} profiles. " +
                "Each profile keeps its own Muse sign-in data on this PC, so many profiles use more disk.\n\n" +
                "Add another profile?",
                "Profiles", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (warn != MessageBoxResult.Yes)
            {
                FocusReturn.Restore(warnInvoker);
                return;
            }
        }
        var name = string.IsNullOrWhiteSpace(NewProfileNameText.Text)
            ? $"Profile {_working.Profiles.Count + 1}"
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
            $"Switch to profile \"{selected.Name}\"?\n\n" +
            "MuseDesktop restarts now with that profile's sign-in. " +
            "Any other unsaved changes in this window are discarded.",
            "Switch profile", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        _switchProfile(_working, selected.Id);
        // The app is exiting for the restart; this window goes with it.
    }

    private void OnBrowseDownloadsFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where MuseDesktop saves downloads",
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
            ClearResultNote.Text = "The web view isn't running yet — try again once Muse has loaded.";
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
            "Clear browsing data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
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

    private async Task RefreshPermissionsAsync()
    {
        PermissionRows.Children.Clear();
        if (_privacy is null || !_privacy.IsLiveReady)
        {
            PermissionsNote.Text = "The web view isn't running yet, so saved permissions can't be read. " +
                "MuseDesktop's own policy stands either way: microphone and notifications are granted to muse.ai when the page asks; everything else is denied.";
            return;
        }
        try
        {
            var rows = await _privacy.GetPermissionsAsync();
            PermissionsNote.Text = rows.Count == 0
                ? "No site has a saved permission choice in this profile. MuseDesktop's own policy: microphone and notifications are granted to muse.ai when the page asks; everything else is denied."
                : "Saved choices in this profile (from any site the profile has visited). Allow / Block change the saved choice; Forget returns it to asking MuseDesktop's policy each time:";
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
                line.Children.Add(PermissionButton("Allow", row, CoreWebView2PermissionState.Allow));
                line.Children.Add(PermissionButton("Block", row, CoreWebView2PermissionState.Deny));
                line.Children.Add(PermissionButton("Forget", row, CoreWebView2PermissionState.Default));
                PermissionRows.Children.Add(line);
            }
        }
        catch (Exception ex)
        {
            PermissionsNote.Text = $"Couldn't read saved permissions ({ex.Message}).";
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
            ToolTip = $"{label} {row.Kind} for {row.Host}",
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
            PermissionsNote.Text = $"Couldn't change that permission ({ex.Message}).";
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
                Text = $"{profile.Name} — measuring…",
                Width = 300,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var clearButton = new System.Windows.Controls.Button
            {
                Content = "Clear this profile…",
                Width = 118,
                Margin = new Thickness(4, 0, 0, 0),
                Tag = profile.Id,
                ToolTip = $"Clear all site data for {profile.Name} (signs that profile out of Muse)",
            };
            clearButton.Click += OnClearOneProfile;
            // S8 #75: the manual cache cleanup — disposable caches
            // only, from the same allowlist the automatic cap uses.
            var cleanButton = new System.Windows.Controls.Button
            {
                Content = "Clean caches",
                Width = 92,
                Margin = new Thickness(4, 0, 0, 0),
                Tag = profile.Id,
                ToolTip = $"Delete only {profile.Name}'s disposable caches (GPU, code, and page caches) — cookies and sign-ins are never touched",
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
            var sizeText = bytes is { } b ? DownloadText.FormatBytes(b) : "no data folder yet";
            var cookieText = " · cookie count unavailable";
            if (_privacy is not null && await _privacy.GetCookieSummaryAsync(profile.Id) is { } cookies)
            {
                cookieText = cookies.Count == 0
                    ? " · no cookies"
                    : $" · {cookies.Count} cookies across {cookies.Domains.Count} site(s)";
            }
            // S8 #75: the cap and its last cleanup ride along so the
            // cleanup is visible here, never silent.
            var capText = profile.DataCapBytes > 0 ? $" · cap {ProfileDataCaps.Label(profile.DataCapBytes)}" : "";
            if (profile.LastCacheCleanupUtc is { } cleanedUtc)
                capText += $" · caches cleaned {cleanedUtc.LocalDateTime:MMM d} (−{DownloadText.FormatBytes(profile.LastCacheCleanupFreedBytes)})";
            label.Text = $"{profile.Name} — {sizeText}{cookieText}{capText}";
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
            ? $"{profile.Name}: no disposable caches to clean."
            : $"{profile.Name}: cleaned {outcome.DeletedCount} cache folder(s), freed {DownloadText.FormatBytes(outcome.FreedBytes)}. Sign-ins untouched.";
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
            $"Clear all site data for {profile.Name}?\n\n" + ClearDataPlan.Describe(ClearDataChoice.AllSiteData),
            "Clear this profile", MessageBoxButton.YesNo, MessageBoxImage.Warning);
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
        var since = stats.SinceUtc is { } sinceUtc ? $"Since {sinceUtc.LocalDateTime:yyyy-MM-dd}, on" : "On";
        sb.AppendLine($"{since} this PC (all profiles combined), MuseDesktop has blocked:");
        sb.AppendLine($"• {stats.Total(PrivacyEventCategory.BlockedNavigation)} page navigations it won't open");
        sb.AppendLine($"• {stats.Total(PrivacyEventCategory.BlockedNewWindow)} pop-up / new-window requests");
        sb.AppendLine($"• {stats.Total(PrivacyEventCategory.DeniedPermission)} permission requests");
        var rows = stats.Rows().Take(6).ToList();
        if (rows.Count > 0)
        {
            sb.AppendLine("Most common:");
            foreach (var row in rows)
            {
                var what = row.Category switch
                {
                    nameof(PrivacyEventCategory.BlockedNavigation) => "blocked navigation",
                    nameof(PrivacyEventCategory.BlockedNewWindow) => "blocked new window",
                    nameof(PrivacyEventCategory.DeniedPermission) => "denied permission",
                    _ => row.Category,
                };
                sb.AppendLine($"• {row.Count}× {what} — {row.Kind}" +
                              (string.IsNullOrEmpty(row.Host) ? "" : $" — {row.Host}"));
            }
        }
        PrivacyReportText.Text = sb.ToString().TrimEnd();
        PermissionDenialsText.Text = stats.RecentDenials.Count == 0
            ? "Recently denied by MuseDesktop: nothing yet."
            : "Recently denied by MuseDesktop (this PC, all profiles):\n" + string.Join("\n",
                stats.RecentDenials.Take(8).Select(d =>
                    $"• {d.TimeUtc.LocalDateTime:MMM d, HH:mm} — {d.Kind} — {(string.IsNullOrEmpty(d.Host) ? "(unknown site)" : d.Host)}"));
    }

    private void OnResetPrivacyStats(object sender, RoutedEventArgs e)
    {
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            "Start the privacy report's counts over from today? The recent-denials list clears too. Nothing else is affected.",
            "Reset privacy counters", MessageBoxButton.YesNo, MessageBoxImage.Question);
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
            ImportNote.Text = $"Couldn't open the data folder ({ex.Message}). It is at {AppPaths.RootDir}.";
        }
    }

    private void OnBackupGuide(object sender, RoutedEventArgs e)
    {
        var webViewRoot = Path.GetDirectoryName(AppPaths.WebViewDataDir) ?? AppPaths.WebViewDataDir;
        var invoker = FocusReturn.Capture();
        System.Windows.MessageBox.Show(this,
            BackupGuide.Compose(AppPaths.RootDir, webViewRoot),
            "Back up MuseDesktop", MessageBoxButton.OK, MessageBoxImage.Information);
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
                ? "App lock is ON — your password or PIN is asked at startup, from Lock now (tray and ⋯ menus), and when the window hides to the tray if that switch is on."
                : "A password is set, but the lock is OFF — tick “Require my app password or PIN” to use it."
            : "App lock is off. Set a password or PIN to require it before MuseDesktop opens.";
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
                "Type your CURRENT app password or PIN first:", confirm: false) { Owner = this };
            if (FocusReturn.ShowDialog(current) != true)
                return;
            if (!AppLock.Verify(current.Password, existing))
            {
                AppLockNote.Text = "That's not the current password — nothing was changed.";
                return;
            }
        }
        PromptSetLockPassword();
    }

    /// <summary>Ask for a new lock password (twice) and store its record on the clone. True when one was set.</summary>
    private bool PromptSetLockPassword()
    {
        var prompt = new PasswordPromptWindow(
            "Choose an app password or PIN (4–64 characters — a PIN is fine; 6 or more characters is better). " +
            "You'll type it whenever MuseDesktop starts locked.",
            confirm: true, AppLock.MinCredentialLength, AppLock.MaxCredentialLength) { Owner = this };
        if (FocusReturn.ShowDialog(prompt) != true)
            return false;
        AppLock.SetCredential(_working, prompt.Password);
        AppLockNote.Text = "App password set — the lock is on. It takes effect when you choose OK.";
        RefreshLockUi();
        return true;
    }

    private void OnRemoveLockPassword(object sender, RoutedEventArgs e)
    {
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            "Remove the app password and turn the app lock off?\n\n" +
            "MuseDesktop will open without asking for anything. You can set a new password here any time.",
            "Remove app password", MessageBoxButton.YesNo, MessageBoxImage.Question);
        FocusReturn.Restore(invoker);
        if (confirm != MessageBoxResult.Yes)
            return;
        AppLock.RemoveCredential(_working);
        AppLockNote.Text = "App password removed — the lock is off once you choose OK.";
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
                Text = action.Name,
                Width = 170,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var keyButton = new System.Windows.Controls.Button
            {
                Content = gesture.DisplayText(),
                Width = 150,
                Tag = action.Id,
                ToolTip = $"Click, then press the new keys for {action.Name}",
            };
            keyButton.Click += OnCaptureShortcut;
            row.Children.Add(keyButton);
            var reset = new System.Windows.Controls.Button
            {
                Content = "Reset",
                Width = 64,
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = hasOverride,
                Tag = action.Id,
                ToolTip = $"Put {action.Name} back to {action.DisplayGestures}",
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
        button.Content = "Press keys… (Esc cancels)";
        ShortcutStatus.Text = "";
        button.Focus();
    }

    /// <summary>
    /// Window-level capture: while a shortcut button is armed, the next
    /// real key press becomes that action's gesture (Esc cancels, Tab
    /// just leaves capture so keyboard users are never trapped).
    /// Modifier-only presses are ignored — a gesture needs a real key.
    /// </summary>
    private void OnSettingsPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_capturingActionId is not { } actionId)
            return;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key == System.Windows.Input.Key.Tab)
        {
            _capturingActionId = null;
            RebuildShortcutRows();
            return; // not handled — focus moves on as usual
        }
        e.Handled = true;
        if (key == System.Windows.Input.Key.Escape)
        {
            _capturingActionId = null;
            RebuildShortcutRows();
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
        var ok = ShortcutBindings.TrySet(
            _working.ShortcutOverrides, actionId, new ShortcutGesture(mods, key.ToString()),
            out var updated, out var error);
        if (ok)
            _working.ShortcutOverrides = updated;
        _capturingActionId = null;
        RebuildShortcutRows();
        ShortcutStatus.Text = ok
            ? $"{ShortcutCatalog.Find(actionId)?.Name} now uses {ShortcutBindings.EffectiveGesture(actionId, _working.ShortcutOverrides).DisplayText()}."
            : error ?? "";
    }

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
        ShortcutStatus.Text = "All shortcuts are back to their defaults.";
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
            if (hit && firstMatch is null)
                firstMatch = container;
        }
        SearchSummary.Text = matches.Count == 0
            ? "No section matches that search — clear the search box to see everything again."
            : $"{matches.Count} of {_sections.Length} sections match — the others are hidden while you search.";
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
            "Reset all settings to their defaults?\n\n" +
            $"First, your current settings are saved to a backup file:\n{backupPath}\n\n" +
            "Preferences (startup, theme, downloads, tray, zoom…) return to defaults, and the setup wizard " +
            "will offer itself again next launch. Your profiles, sign-ins, last pages, and downloaded files " +
            "are NOT touched — and you can Import the backup at any time to undo this.",
            "Reset all settings", MessageBoxButton.YesNo, MessageBoxImage.Warning);
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
            ResetNote.Text = $"Couldn't write the backup file ({ex.Message}) — your settings were NOT reset.";
            _log.Warn($"Settings reset aborted — backup failed: {ex.Message}");
            return;
        }
        SettingsReset.ApplyDefaults(_working);
        LoadControls();
        ResetNote.Text = $"Settings reset to defaults. Backup saved as {backupName} in the settings folder — Import it any time to undo.";
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
                Text = "No backups yet — the first automatic one is written today, or use Back up now.",
                Style = (Style)FindResource("Note"),
            });
            return;
        }
        foreach (var backup in catalog)
        {
            var label = new System.Windows.Controls.TextBlock
            {
                Text = $"{(backup.Kind == "settings" ? "Settings" : "Downloads history")} — {backup.Stamp.LocalDateTime:yyyy-MM-dd HH:mm}",
                Width = 300,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var restore = new System.Windows.Controls.Button
            {
                Content = "Restore…",
                Width = 76,
                Margin = new Thickness(4, 0, 0, 0),
                Tag = backup,
                ToolTip = backup.Kind == "settings"
                    ? "Replace the settings in this window with this backup (OK still applies them)"
                    : "Replace the downloads history file with this backup",
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
            BackupNote.Text = $"Backed up: {string.Join(", ", written)} — kept in the backups folder next to your settings.";
            RefreshBackupRows();
        }
        else
        {
            BackupNote.Text = "The backup couldn't be written — the log has the details.";
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
                $"Restore settings from the {backup.Stamp.LocalDateTime:yyyy-MM-dd HH:mm} backup?\n\n" +
                "The settings in this window are replaced by the backup's — choose OK here to apply them, Cancel to discard. " +
                "Sign-ins and profile data folders are not part of a settings backup.",
                "Restore settings backup", MessageBoxButton.YesNo, MessageBoxImage.Question);
            FocusReturn.Restore(invoker);
            if (confirm != MessageBoxResult.Yes)
                return;
            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception ex)
            {
                BackupNote.Text = $"That backup couldn't be read ({ex.Message}).";
                return;
            }
            if (!SettingsStore.TryImport(json, out var imported, out var error) || imported is null)
            {
                BackupNote.Text = $"That backup isn't a settings file MuseDesktop can use ({error}).";
                return;
            }
            CopySettingsInto(imported, _working);
            LoadControls();
            BackupNote.Text = $"Settings restored from the {backup.Stamp.LocalDateTime:yyyy-MM-dd HH:mm} backup — review them, then OK to apply.";
            _log.Info($"Settings restored from rotating backup {backup.FileName}");
            return;
        }
        var confirmDownloads = System.Windows.MessageBox.Show(this,
            $"Restore the downloads history from the {backup.Stamp.LocalDateTime:yyyy-MM-dd HH:mm} backup?\n\n" +
            "The current history file is replaced. The Downloads window shows the restored history next time it opens.",
            "Restore downloads history", MessageBoxButton.YesNo, MessageBoxImage.Question);
        FocusReturn.Restore(invoker);
        if (confirmDownloads != MessageBoxResult.Yes)
            return;
        try
        {
            Directory.CreateDirectory(AppPaths.RootDir);
            File.Copy(path, AppPaths.DownloadsHistoryPath, overwrite: true);
            BackupNote.Text = $"Downloads history restored from the {backup.Stamp.LocalDateTime:yyyy-MM-dd HH:mm} backup.";
            _log.Info($"Downloads history restored from rotating backup {backup.FileName}");
        }
        catch (Exception ex)
        {
            BackupNote.Text = $"The history couldn't be restored ({ex.Message}).";
        }
    }

    // ───────────────────────── settings export / import (v0.4.0) ─────────────────────────

    private void OnExportSettings(object sender, RoutedEventArgs e)
    {
        HarvestControls();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "musedesktop-settings.json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            Title = "Export MuseDesktop settings",
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
            "Encrypt this export with a password?\n\n" +
            "Yes — the file can only be opened with the password (you'll choose it next). Recommended if the file will leave this PC.\n" +
            "No — a plain settings file. Anyone who can read it can read your profile names and start pages" +
            (AppLock.HasValidRecord(_working) ? ", and it includes your app-lock password hash (never the password itself)" : "") + ".",
            "Export settings", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        FocusReturn.Restore(invoker);
        if (encrypt == MessageBoxResult.Cancel)
            return;
        string contents;
        if (encrypt == MessageBoxResult.Yes)
        {
            var prompt = new PasswordPromptWindow(
                "Choose a password for this encrypted export. You'll need it to import the file — it can't be recovered.",
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
                ? $"Encrypted settings exported to {dialog.FileName}."
                : $"Settings exported to {dialog.FileName} — unencrypted: profile names and start pages in it are readable by anyone who opens the file.";
            _log.Info(encrypt == MessageBoxResult.Yes
                ? "Settings exported (encrypted) from Settings window"
                : "Settings exported from Settings window");
        }
        catch (Exception ex)
        {
            _log.Warn($"Settings export failed: {ex.Message}");
            ImportNote.Text = $"Couldn't write that file ({ex.Message}).";
        }
    }

    private void OnImportSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            Title = "Import MuseDesktop settings",
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
            ImportNote.Text = $"Couldn't read that file ({ex.Message}).";
            return;
        }
        // S7 #65: an encrypted export is detected by its envelope and
        // asks for its password; wrong password and damaged file are
        // different messages, from Core's format code.
        if (EncryptedExport.IsEncrypted(json))
        {
            var prompt = new PasswordPromptWindow(
                "This settings export is encrypted. Type its password to import it:",
                confirm: false) { Owner = this };
            if (FocusReturn.ShowDialog(prompt) != true)
                return;
            if (!EncryptedExport.TryDecrypt(json, prompt.Password, out var plain, out _, out var decryptMessage)
                || plain is null)
            {
                ImportNote.Text = decryptMessage ?? "That file couldn't be decrypted.";
                return;
            }
            json = plain;
        }
        if (!SettingsStore.TryImport(json, out var imported, out var error) || imported is null)
        {
            ImportNote.Text = error ?? "That file couldn't be imported.";
            return;
        }
        CopySettingsInto(imported, _working);
        LoadControls();
        ImportNote.Text = "Settings imported — choose OK to apply them. If the file names a different active profile, that change takes effect next launch.";
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
    }
}
