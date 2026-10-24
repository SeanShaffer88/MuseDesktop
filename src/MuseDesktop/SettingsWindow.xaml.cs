using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
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
/// clone, Cancel discards it. The one live action in here is clearing
/// browsing data, which the main window performs via the callback — it acts
/// on the WebView2 profile, not on settings.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _working;
    private readonly DiagnosticLog _log;
    private readonly Func<Task<bool>> _clearBrowsingData;
    private readonly Action<AppSettings, string> _switchProfile;

    public SettingsWindow(AppSettings working, DiagnosticLog log, Func<Task<bool>> clearBrowsingData,
        Action<AppSettings, string> switchProfile)
    {
        _working = working;
        _log = log;
        _clearBrowsingData = clearBrowsingData;
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
            (SettingsSearch.DownloadsId, SectionDownloads, HeaderDownloads),
            (SettingsSearch.DataId, SectionData, HeaderData),
        };

        foreach (var step in ZoomMath.Steps)
        {
            ZoomCombo.Items.Add(ZoomMath.FormatPercent(step));
            ProfileZoomCombo.Items.Add(ZoomMath.FormatPercent(step));
        }

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
        _syncingProfileControls = false;
        ProfileDataText.Text = selected is null
            ? ""
            : ProfileDataInfo.Describe(AppPaths.ProfileDir(selected.Id), selected);
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
        if (dialog.ShowDialog(this) == true)
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
            System.Windows.MessageBox.Show(this,
                $"MuseDesktop supports up to {Profiles.MaxProfiles} profiles.",
                "Profiles", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // S3 #30: past the soft threshold, adding is a deliberate choice —
        // every profile keeps its own sign-in data on this PC.
        if (_working.Profiles.Count >= Profiles.SoftProfileWarningThreshold)
        {
            var warn = System.Windows.MessageBox.Show(this,
                $"You already have {_working.Profiles.Count} profiles. " +
                "Each profile keeps its own Muse sign-in data on this PC, so many profiles use more disk.\n\n" +
                "Add another profile?",
                "Profiles", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (warn != MessageBoxResult.Yes)
                return;
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
        var confirm = System.Windows.MessageBox.Show(this,
            $"Switch to profile \"{selected.Name}\"?\n\n" +
            "MuseDesktop restarts now with that profile's sign-in. " +
            "Any other unsaved changes in this window are discarded.",
            "Switch profile", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;
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
        if (dialog.ShowDialog(this) == true)
            DownloadsFolderText.Text = dialog.FolderName;
    }

    private async void OnClearData(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(this,
            "Clear all browsing data stored by MuseDesktop and sign out of Muse?\n\n" +
            "Your chats are stored in your Muse account and are not affected.",
            "Clear browsing data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        ClearDataButton.IsEnabled = false;
        var ok = await _clearBrowsingData();
        ClearDataNote.Text = ok
            ? "Done — browsing data cleared. You'll be asked to sign in to Muse again."
            : "Couldn't clear data because the web view isn't running yet. Try again once Muse has loaded.";
        _log.Info($"Clear browsing data requested from Settings: {(ok ? "done" : "web view not ready")}");
        if (ok)
            DialogResult = true; // adopt settings too; the main window navigates home
        else
            ClearDataButton.IsEnabled = true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        HarvestControls();
        DialogResult = true;
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
        if (wizard.ShowDialog() == true)
            LoadControls(); // the wizard's four choices refresh this window
    }

    // ── #42 reset all settings (backup first, always) ──

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        HarvestControls();
        var backupName = SettingsReset.BackupFileName(DateTimeOffset.Now);
        var backupPath = Path.Combine(AppPaths.RootDir, backupName);
        var confirm = System.Windows.MessageBox.Show(this,
            "Reset all settings to their defaults?\n\n" +
            $"First, your current settings are saved to a backup file:\n{backupPath}\n\n" +
            "Preferences (startup, theme, downloads, tray, zoom…) return to defaults, and the setup wizard " +
            "will offer itself again next launch. Your profiles, sign-ins, last pages, and downloaded files " +
            "are NOT touched — and you can Import the backup at any time to undo this.",
            "Reset all settings", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;
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
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, SettingsStore.Export(_working));
            ImportNote.Text = $"Settings exported to {dialog.FileName}.";
            _log.Info("Settings exported from Settings window");
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
        if (dialog.ShowDialog(this) != true)
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
    }
}
