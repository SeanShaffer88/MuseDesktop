using System.IO;
using System.Windows;
using Microsoft.Win32;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The first-run setup wizard (v0.9.0, S5 #50): one screen, four
/// choices, pre-filled with the current values. It edits the settings
/// object it is handed — Save writes the four choices (and the
/// first-run flag) into it and returns DialogResult true; the CALLER
/// owns persistence and adoption, exactly like the Settings window's
/// clone flow. Skip changes nothing on the target except, when
/// <paramref name="markCompletedOnSkip"/> is set, the first-run flag —
/// App passes false and records completion itself so closing the
/// window with the X counts as skipping too.
/// </summary>
public partial class SetupWizardWindow : Window
{
    private readonly AppSettings _target;

    public SetupWizardWindow(AppSettings target, bool markCompletedOnSkip)
    {
        _target = target;
        InitializeComponent();
        CloseToTrayCheck.IsChecked = target.CloseToTray;
        StartWithWindowsCheck.IsChecked = target.StartWithWindows;
        DownloadsFolderText.Text = target.DownloadsFolder ?? "";
        CheckUpdatesCheck.IsChecked = target.CheckForUpdates;
        _markCompletedOnSkip = markCompletedOnSkip;
        ShellTextScaling.Apply(this, target.ShellFontScale);
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private readonly bool _markCompletedOnSkip;

    private void OnBrowseDownloadsFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Strings.Get("ui.choose_where_musedesktop_saves_downloads"),
            InitialDirectory = Directory.Exists(DownloadsFolderText.Text)
                ? DownloadsFolderText.Text
                : AppPaths.DownloadsHint,
        };
        if (dialog.ShowDialog(this) == true)
            DownloadsFolderText.Text = dialog.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _target.CloseToTray = CloseToTrayCheck.IsChecked == true;
        _target.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _target.DownloadsFolder = string.IsNullOrWhiteSpace(DownloadsFolderText.Text)
            ? null
            : DownloadsFolderText.Text.Trim();
        _target.CheckForUpdates = CheckUpdatesCheck.IsChecked == true;
        _target.FirstRunCompleted = true;
        DialogResult = true;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        if (_markCompletedOnSkip)
            _target.FirstRunCompleted = true;
        DialogResult = false;
    }
}
