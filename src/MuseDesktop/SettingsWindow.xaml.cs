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

        StartWithWindowsCheck.IsChecked = working.StartWithWindows;
        CloseToTrayCheck.IsChecked = working.CloseToTray;
        SuspendInTrayCheck.IsChecked = working.SuspendWhenInTray;
        CheckUpdatesCheck.IsChecked = working.CheckForUpdates;
        AskWhereToSaveCheck.IsChecked = working.AskWhereToSave;
        DownloadsFolderText.Text = working.DownloadsFolder ?? "";

        foreach (var step in ZoomMath.Steps)
            ZoomCombo.Items.Add(ZoomMath.FormatPercent(step));
        var current = ZoomMath.FormatPercent(working.ZoomFactor);
        ZoomCombo.SelectedItem = ZoomCombo.Items.Contains(current) ? current : "100%";

        RefreshProfileCombo(_working.ActiveProfileId);
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
        DialogResult = true;
    }
}
