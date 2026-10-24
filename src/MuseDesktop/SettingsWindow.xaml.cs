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

        foreach (var step in ZoomMath.Steps)
        {
            ZoomCombo.Items.Add(ZoomMath.FormatPercent(step));
            ProfileZoomCombo.Items.Add(ZoomMath.FormatPercent(step));
        }

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

        LoadControls();
    }

    /// <summary>(Re)fill every control from the working settings — at open and after an import.</summary>
    private void LoadControls()
    {
        StartWithWindowsCheck.IsChecked = _working.StartWithWindows;
        CloseToTrayCheck.IsChecked = _working.CloseToTray;
        SuspendInTrayCheck.IsChecked = _working.SuspendWhenInTray;
        CheckUpdatesCheck.IsChecked = _working.CheckForUpdates;
        AskWhereToSaveCheck.IsChecked = _working.AskWhereToSave;
        NotifyDownloadCompleteCheck.IsChecked = _working.NotifyWhenDownloadCompletes;
        CollisionPolicyCombo.SelectedIndex = _working.CollisionPolicy == DownloadCollisionPolicy.AskWhereToSave ? 1 : 0;
        HistoryCapCombo.SelectedItem = DownloadHistoryStore.SanitizeCap(_working.DownloadHistoryCap).ToString();
        DownloadsFolderText.Text = _working.DownloadsFolder ?? "";

        var current = ZoomMath.FormatPercent(_working.ZoomFactor);
        ZoomCombo.SelectedItem = ZoomCombo.Items.Contains(current) ? current : "100%";

        RefreshProfileCombo(_working.ActiveProfileId);
    }

    /// <summary>Read the plain controls back into the working settings (OK and Export share this).</summary>
    private void HarvestControls()
    {
        _working.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _working.CloseToTray = CloseToTrayCheck.IsChecked == true;
        _working.SuspendWhenInTray = SuspendInTrayCheck.IsChecked == true;
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
        _syncingProfileStartHome = true;
        ProfileStartHomeCheck.IsChecked = selected?.StartAtHome == true;
        _syncingProfileStartHome = false;
        ProfileStartHomeCheck.IsEnabled = selected is not null;
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

    private bool _syncingProfileStartHome;

    private void OnProfileStartHomeChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingProfileStartHome)
            return;
        if (SelectedProfile is { } selected)
            selected.StartAtHome = ProfileStartHomeCheck.IsChecked == true;
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
        target.Profiles = source.Profiles;
        target.ActiveProfileId = source.ActiveProfileId;
        target.PendingProfileFolderDeletions = source.PendingProfileFolderDeletions;
        target.LastUrl = source.LastUrl;
        target.LastUrlByProfile = source.LastUrlByProfile;
    }
}
