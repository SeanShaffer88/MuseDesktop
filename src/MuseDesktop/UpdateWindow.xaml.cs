using System.Diagnostics;
using System.Windows;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The update window (v0.13.0, S9 #81/#83): download the new installer
/// in-app with progress, verify its SHA-256 against the release's
/// SHA256SUMS.txt, and launch it — only ever on the user's click.
/// Nothing here is silent: no auto-download, no auto-launch, and a
/// hash mismatch deletes the file with a plain explanation. Snooze
/// (tomorrow / next week) also lives here, writing the timestamp the
/// Core schedule consults before any automatic reminder.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly GitHubRelease _release;
    private readonly AppSettings _settings;
    private readonly DiagnosticLog _log;
    private readonly Action _persistSettings;
    private readonly Action _exitApp;
    private CancellationTokenSource? _downloadCts;
    private string? _verifiedInstallerPath;

    public UpdateWindow(GitHubRelease release, AppSettings settings, DiagnosticLog log,
        Action persistSettings, Action exitApp)
    {
        _release = release;
        _settings = settings;
        _log = log;
        _persistSettings = persistSettings;
        _exitApp = exitApp;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        HeadlineText.Text = $"MuseDesktop {_release.Tag} is available";
        if (_settings.UpdateSnoozedUntilUtc is { } until && DateTimeOffset.UtcNow < until)
            SnoozeNoteText.Text = UpdateSchedule.DescribeSnooze(until);
        Closing += (_, _) => _downloadCts?.Cancel();
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        DownloadButton.Visibility = Visibility.Collapsed;
        SnoozeDayButton.Visibility = Visibility.Collapsed;
        SnoozeWeekButton.Visibility = Visibility.Collapsed;
        CancelDownloadButton.Visibility = Visibility.Visible;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        StatusText.Text = "Downloading update…";
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<string>(text =>
        {
            StatusText.Text = text;
            var pctAt = text.IndexOf('%');
            if (pctAt > 0)
            {
                var start = pctAt;
                while (start > 0 && char.IsDigit(text[start - 1]))
                    start--;
                if (start < pctAt && int.TryParse(text.AsSpan(start, pctAt - start), out var pct))
                {
                    DownloadProgress.IsIndeterminate = false;
                    DownloadProgress.Value = pct;
                }
            }
        });
        var result = await UpdateService.DownloadAndVerifyAsync(
            _release, AppPaths.UpdatesDir, _log, progress, _downloadCts.Token);
        CancelDownloadButton.Visibility = Visibility.Collapsed;
        DownloadProgress.Visibility = Visibility.Collapsed;
        switch (result.Outcome)
        {
            case UpdateDownloadOutcome.Verified:
                _verifiedInstallerPath = result.InstallerPath;
                StatusText.Text = "Downloaded and verified — the installer's fingerprint matches the " +
                                  "fingerprint published with the release. Choose Install when you're ready: " +
                                  "MuseDesktop will close and the installer will update it in place " +
                                  "(your sign-in and settings carry over).";
                InstallButton.Visibility = Visibility.Visible;
                break;
            default:
                StatusText.Text = result.Message ?? "The update couldn't be downloaded.";
                DownloadButton.Visibility = Visibility.Visible;
                SnoozeDayButton.Visibility = Visibility.Visible;
                SnoozeWeekButton.Visibility = Visibility.Visible;
                break;
        }
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_verifiedInstallerPath is null)
            return;
        var newVersion = UpdateInfo.ParseTag(_release.Tag)?.ToString(3) ?? _release.Tag;
        // S9 #84: rotate the kept installers BEFORE the installer takes
        // over — after it starts, this process is on its way out.
        UpdateService.PrepareRollback(_settings, AppPaths.UpdatesDir, newVersion, _verifiedInstallerPath, _log);
        _persistSettings();
        try
        {
            Process.Start(new ProcessStartInfo(_verifiedInstallerPath) { UseShellExecute = true });
            _log.Info($"Update installer launched ({_release.Tag}) — exiting so setup can take over");
        }
        catch (Exception ex)
        {
            _log.Warn($"Launching the update installer failed: {ex.Message}");
            StatusText.Text = "The installer couldn't be started. The verified installer is kept in the " +
                              $"updates folder ({AppPaths.UpdatesDir}) — you can run it from there.";
            return;
        }
        Close();
        _exitApp();
    }

    private void OnSnoozeTomorrow(object sender, RoutedEventArgs e) => Snooze(UpdateSnooze.Tomorrow);

    private void OnSnoozeNextWeek(object sender, RoutedEventArgs e) => Snooze(UpdateSnooze.NextWeek);

    private void Snooze(UpdateSnooze choice)
    {
        var until = UpdateSchedule.SnoozeUntil(DateTimeOffset.UtcNow, choice);
        _settings.UpdateSnoozedUntilUtc = until;
        _persistSettings();
        _log.Info($"Update reminders snoozed until {until:yyyy-MM-dd}");
        Close();
    }

    private void OnOpenReleasePage(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_release.PageUrl) { UseShellExecute = true }); }
        catch { /* no default browser — nothing sensible to do */ }
    }
}
