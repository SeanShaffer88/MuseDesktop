using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// Downloads: the live manager on top (S2 #13 — every active download
/// with its own progress, pause/resume where WebView2 allows it,
/// cancel, and retry for this session's failures) and the finished-
/// downloads history below (v0.4.0; S2 #16 search/filter/sort, S2 #17
/// Copy path / Open with… / Delete file). History loads with the
/// configured cap each time it is read so it matches Settings.
/// </summary>
public partial class DownloadsWindow : Window
{
    private readonly DiagnosticLog _log;
    private readonly DownloadTracker? _tracker;
    private readonly Func<string, Task>? _retryAsync;
    private readonly int _historyCap;
    private readonly DispatcherTimer? _refreshTimer;
    private List<DownloadRecord> _allRecords = new();

    /// <summary>One history row: the record plus its pre-formatted detail line.</summary>
    public sealed class Row
    {
        public required DownloadRecord Record { get; init; }
        public string FileName => Record.FileName;
        public string Detail
        {
            get
            {
                var state = Record.State == DownloadRecordState.Failed
                    ? $"Failed{(string.IsNullOrWhiteSpace(Record.FailureReason) ? "" : $" ({Record.FailureReason})")} · "
                    : "";
                return $"{state}{DownloadText.FormatBytes(Record.SizeBytes)} · {Record.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {Record.FullPath}";
            }
        }
    }

    /// <summary>One active/session-failed row in the manager list.</summary>
    public sealed class ActiveRow
    {
        public required string Id { get; init; }
        public required string FileName { get; init; }
        public required string Detail { get; init; }
        public required DownloadActivityState State { get; init; }
        public bool IsSessionFailed { get; init; }
        public bool CanResume { get; init; }
    }

    public DownloadsWindow(
        DiagnosticLog log,
        DownloadTracker? tracker = null,
        Func<string, Task>? retryAsync = null,
        int historyCap = DownloadHistoryStore.MaxEntries)
    {
        _log = log;
        _tracker = tracker;
        _retryAsync = retryAsync;
        _historyCap = DownloadHistoryStore.SanitizeCap(historyCap);
        InitializeComponent();
        Refresh();
        RefreshActive();
        if (_tracker is not null)
        {
            // Live progress without event plumbing: poll the tracker models
            // while the window is open; the list is small by construction.
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            _refreshTimer.Tick += (_, _) => { RefreshActive(); RefreshHistoryOnly(); };
            _refreshTimer.Start();
            Closed += (_, _) => _refreshTimer.Stop();
        }
    }

    private Row? Selected => DownloadsList.SelectedItem as Row;
    private ActiveRow? SelectedActive => ActiveList.SelectedItem as ActiveRow;

    // ───────────────────────── history ─────────────────────────

    private void Refresh()
    {
        _allRecords = DownloadHistoryStore.Load(AppPaths.DownloadsHistoryPath, _historyCap);
        ApplyHistoryQuery();
        UpdateButtons();
    }

    /// <summary>
    /// Reload history from disk (a download may have finished while this
    /// window is open) — but only rebind when something actually changed,
    /// so the 750 ms poll doesn't steal the user's selection mid-click.
    /// </summary>
    private void RefreshHistoryOnly()
    {
        var loaded = DownloadHistoryStore.Load(AppPaths.DownloadsHistoryPath, _historyCap);
        var changed = loaded.Count != _allRecords.Count || loaded.Zip(_allRecords).Any(pair =>
            pair.First.FullPath != pair.Second.FullPath
            || pair.First.CompletedUtc != pair.Second.CompletedUtc
            || pair.First.State != pair.Second.State);
        if (!changed)
            return;
        _allRecords = loaded;
        ApplyHistoryQuery();
    }

    private void ApplyHistoryQuery()
    {
        // ComboBox IsSelected in XAML can fire SelectionChanged while
        // InitializeComponent is still creating the other controls.
        if (DownloadsList is null || StateFilterCombo is null || SortCombo is null || NoteText is null)
            return;
        var filterTag = (StateFilterCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
        var filter = filterTag switch
        {
            "Completed" => DownloadStateFilter.Completed,
            "Failed" => DownloadStateFilter.Failed,
            _ => DownloadStateFilter.All,
        };
        var sortTag = (SortCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
        var sort = sortTag switch
        {
            "LargestFirst" => DownloadHistorySort.LargestFirst,
            "NameAscending" => DownloadHistorySort.NameAscending,
            _ => DownloadHistorySort.NewestFirst,
        };
        var filtered = DownloadHistoryQuery.Apply(_allRecords, SearchText?.Text, filter, sort);
        DownloadsList.ItemsSource = filtered.Select(r => new Row { Record = r }).ToList();
        if (_allRecords.Count == 0)
            NoteText.Text = "Nothing here yet — files you download from Muse will appear in this list.";
        else if (filtered.Count == 0)
            NoteText.Text = "No downloads match the current search/filter.";
        else
            NoteText.Text = "Files Muse downloaded for you. The list is kept on this PC only.";
    }

    private void OnSearchOrFilterChanged(object sender, object e) => ApplyHistoryQuery();

    private void UpdateButtons()
    {
        var enabled = Selected is not null;
        OpenButton.IsEnabled = enabled;
        FolderButton.IsEnabled = enabled;
        CopyPathButton.IsEnabled = enabled;
        OpenWithButton.IsEnabled = enabled;
        DeleteFileButton.IsEnabled = enabled;
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateButtons();

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        if (!File.Exists(row.Record.FullPath))
        {
            NoteText.Text = $"\"{row.Record.FileName}\" isn't at that location anymore (moved or deleted).";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(row.Record.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"Downloads window open failed: {ex.Message}");
            NoteText.Text = $"Couldn't open \"{row.Record.FileName}\".";
        }
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        try
        {
            if (File.Exists(row.Record.FullPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Record.FullPath}\"")
                    { UseShellExecute = true });
            }
            else if (Path.GetDirectoryName(row.Record.FullPath) is { } dir && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            else
            {
                NoteText.Text = $"The folder for \"{row.Record.FileName}\" no longer exists.";
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Downloads window show-in-folder failed: {ex.Message}");
        }
    }

    /// <summary>Copy the full path of the selected download (S2 #17).</summary>
    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        try
        {
            System.Windows.Clipboard.SetText(row.Record.FullPath);
            NoteText.Text = $"Copied: {row.Record.FullPath}";
        }
        catch (Exception ex)
        {
            _log.Warn($"Downloads window copy-path failed: {ex.Message}");
            NoteText.Text = "Couldn't copy the path — the clipboard is busy.";
        }
    }

    /// <summary>
    /// Open-with dialog for the selected file (S2 #17): the documented
    /// shell entry point <c>rundll32 shell32.dll,OpenAs_RunDLL</c>.
    /// </summary>
    private void OnOpenWith(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        if (!File.Exists(row.Record.FullPath))
        {
            NoteText.Text = $"\"{row.Record.FileName}\" isn't at that location anymore (moved or deleted).";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe",
                $"shell32.dll,OpenAs_RunDLL \"{row.Record.FullPath}\""));
        }
        catch (Exception ex)
        {
            _log.Warn($"Downloads window open-with failed: {ex.Message}");
            NoteText.Text = $"Couldn't open the Open-with dialog for \"{row.Record.FileName}\".";
        }
    }

    /// <summary>
    /// Delete the selected file from disk (S2 #17): confirmed first, and
    /// the history entry goes with it — a history row for a file the user
    /// just deleted from inside this window would only confuse.
    /// </summary>
    private void OnDeleteFile(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            $"Delete \"{row.Record.FileName}\" from this PC?\n\n" +
            "The file is deleted (not sent to the Recycle Bin by this action — Windows deletes it directly) " +
            "and its entry is removed from this list. This can't be undone from MuseDesktop.",
            "Delete downloaded file", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        try
        {
            if (File.Exists(row.Record.FullPath))
                File.Delete(row.Record.FullPath);
            DownloadHistoryStore.Remove(AppPaths.DownloadsHistoryPath, row.Record, _historyCap);
            _log.Info($"Downloaded file deleted from Downloads window: {row.Record.FileName}");
            NoteText.Text = $"Deleted \"{row.Record.FileName}\".";
        }
        catch (Exception ex)
        {
            _log.Warn($"Downloads window delete-file failed: {ex.Message}");
            NoteText.Text = $"Couldn't delete \"{row.Record.FileName}\" ({ex.Message}).";
        }
        Refresh();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            "Clear the download history?\n\nThis only removes the list — the downloaded files themselves stay where they are.",
            "Clear download history", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        try
        {
            DownloadHistoryStore.Clear(AppPaths.DownloadsHistoryPath);
            _log.Info("Download history cleared");
        }
        catch (Exception ex)
        {
            _log.Warn($"Download history clear failed: {ex.Message}");
        }
        Refresh();
    }

    // ───────────────────────── active manager (S2 #13–15) ─────────────────────────

    private void RefreshActive()
    {
        if (_tracker is null)
        {
            ActiveList.ItemsSource = new List<ActiveRow>();
            UpdateActiveButtons();
            return;
        }
        var selectedId = SelectedActive?.Id;
        var now = DateTimeOffset.Now;
        var rows = _tracker.Snapshot()
            .Select(d => new ActiveRow
            {
                Id = d.Id,
                FileName = d.FileName,
                Detail = DownloadText.ActiveDetail(d, now),
                State = d.State,
                CanResume = d.CanResume,
            })
            .ToList();
        rows.AddRange(_tracker.SessionFailed.Select(f => new ActiveRow
        {
            Id = f.Id,
            FileName = f.FileName,
            Detail = "Failed this session — Retry starts the download again",
            State = DownloadActivityState.Failed,
            IsSessionFailed = true,
        }));
        ActiveList.ItemsSource = rows;
        if (selectedId is not null)
            ActiveList.SelectedItem = rows.FirstOrDefault(r => r.Id == selectedId);
        ActiveNote.Text = rows.Count == 0
            ? "Nothing is downloading right now."
            : $"{rows.Count} item(s) — Pause/Resume use WebView2's own download controls; Retry is available for this session's failures only.";
        UpdateActiveButtons();
    }

    private void UpdateActiveButtons()
    {
        var row = SelectedActive;
        PauseButton.IsEnabled = row is { State: DownloadActivityState.InProgress, IsSessionFailed: false };
        ResumeButton.IsEnabled = row is { State: DownloadActivityState.Paused, IsSessionFailed: false };
        CancelButton.IsEnabled = row is { IsSessionFailed: false } &&
                                  row.State is DownloadActivityState.InProgress or DownloadActivityState.Paused;
        RetryButton.IsEnabled = row is { IsSessionFailed: true } && _retryAsync is not null;
    }

    private void OnActiveSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateActiveButtons();

    private void OnPauseDownload(object sender, RoutedEventArgs e)
    {
        if (SelectedActive is { } row) _tracker?.Pause(row.Id);
        RefreshActive();
    }

    private void OnResumeDownload(object sender, RoutedEventArgs e)
    {
        if (SelectedActive is { } row) _tracker?.Resume(row.Id);
        RefreshActive();
    }

    private void OnCancelDownload(object sender, RoutedEventArgs e)
    {
        if (SelectedActive is { } row) _tracker?.Cancel(row.Id);
        RefreshActive();
    }

    private async void OnRetryDownload(object sender, RoutedEventArgs e)
    {
        if (SelectedActive is not { IsSessionFailed: true } row || _retryAsync is null)
            return;
        RetryButton.IsEnabled = false;
        try
        {
            await _retryAsync(row.Id);
            NoteText.Text = $"Retrying \"{row.FileName}\" — watch the active list / status bar for progress.";
        }
        catch (Exception ex)
        {
            _log.Warn($"Retry download failed: {ex.Message}");
            NoteText.Text = $"Couldn't retry \"{row.FileName}\".";
        }
        RefreshActive();
    }
}
