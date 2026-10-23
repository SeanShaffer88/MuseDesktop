using System.IO;
using System.Windows;
using Microsoft.Win32;
using MuseDesktop.Core;

namespace MuseDesktop;

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
            ZoomCombo.Items.Add(ZoomMath.FormatPercent(step));

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
        UpdateProfileButtons();
    }

    private void UpdateProfileButtons()
    {
        var selected = SelectedProfile;
        SwitchProfileButton.IsEnabled = selected is not null && selected.Id != _working.ActiveProfileId;
        RemoveProfileButton.IsEnabled = selected is not null
                                         && selected.Id != _working.ActiveProfileId
                                         && _working.Profiles.Count > 1;
        _syncingProfileStartHome = true;
        ProfileStartHomeCheck.IsChecked = selected?.StartAtHome == true;
        _syncingProfileStartHome = false;
        ProfileStartHomeCheck.IsEnabled = selected is not null;
    }

    private bool _syncingProfileStartHome;

    private void OnProfileStartHomeChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingProfileStartHome)
            return;
        if (SelectedProfile is { } selected)
            selected.StartAtHome = ProfileStartHomeCheck.IsChecked == true;
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
        var name = string.IsNullOrWhiteSpace(NewProfileNameText.Text)
            ? $"Profile {_working.Profiles.Count + 1}"
            : NewProfileNameText.Text.Trim();
        var profile = new AccountProfile { Id = Profiles.NewId(), Name = name };
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
        target.CheckForUpdates = source.CheckForUpdates;
        target.SuspendWhenInTray = source.SuspendWhenInTray;
        target.Profiles = source.Profiles;
        target.ActiveProfileId = source.ActiveProfileId;
        target.LastUrl = source.LastUrl;
        target.LastUrlByProfile = source.LastUrlByProfile;
    }
}
