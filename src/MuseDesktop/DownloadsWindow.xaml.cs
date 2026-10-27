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
/// R4 (v1.4.0) adds the downloads round-2 surface: the speed graph
/// (#135), per-entry notes (#137), Verify SHA-256 (#136), Sort + Undo
/// (#131), Archive (#138), and batch link capture (#134).
/// </summary>
public partial class DownloadsWindow : Window
{
    private readonly DiagnosticLog _log;
    private readonly DownloadTracker? _tracker;
    private readonly Func<string, Task>? _retryAsync;
    private readonly Func<IReadOnlyList<BatchLink>, Task>? _startBatchAsync;
    private readonly AppSettings _settings;
    private readonly int _historyCap;
    private readonly DispatcherTimer? _historySearchTimer;
    private readonly DispatcherTimer? _activeRefreshTimer;
    private readonly DispatcherTimer? _speedSampleTimer;
    private readonly DownloadSpeedHistory _speedHistory = new();
    private CancellationTokenSource? _hashCancellation;
    private List<DownloadRecord> _allRecords = new();
    private bool _hasUndoBatch;

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
                // R4 #137/#136: the user's note and the hash verdict ride
                // the same line — both are facts about THIS entry.
                var note = string.IsNullOrWhiteSpace(Record.Note) ? "" : $" · Note: {Record.Note}";
                var hash = Record.HashVerified switch
                {
                    true => " · SHA-256 verified",
                    false => " · SHA-256 check FAILED",
                    null => "",
                };
                return $"{state}{DownloadText.FormatBytes(Record.SizeBytes)} · {Record.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {Record.FullPath}{note}{hash}";
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
        int historyCap = DownloadHistoryStore.MaxEntries,
        AppSettings? settings = null,
        Func<IReadOnlyList<BatchLink>, Task>? startBatchAsync = null)
    {
        _log = log;
        _tracker = tracker;
        _retryAsync = retryAsync;
        _startBatchAsync = startBatchAsync;
        _settings = (settings ?? new AppSettings()).Sanitized();
        _historyCap = DownloadHistoryStore.SanitizeCap(historyCap);
        InitializeComponent();
        AddLinksButton.IsEnabled = _startBatchAsync is not null;
        _historySearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _historySearchTimer.Tick += (_, _) =>
        {
            _historySearchTimer?.Stop();
            ApplyHistoryQuery();
        };
        _activeRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _activeRefreshTimer.Tick += (_, _) =>
        {
            _activeRefreshTimer?.Stop();
            RefreshActive();
        };
        _speedSampleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _speedSampleTimer.Tick += (_, _) =>
        {
            if (_tracker is not null)
            {
                _speedHistory.Add(DateTimeOffset.Now, _tracker.TotalSpeed);
                DrawSpeedGraph();
            }
        };
        Refresh();
        RefreshActive();
        if (_tracker is { } tracker)
        {
            // Tracker events refresh rows/history; the separate 750 ms
            // timer samples only the visible in-memory speed graph.
            tracker.Changed += OnTrackerChanged;
            tracker.Completed += OnTrackerHistoryChanged;
            tracker.Failed += OnTrackerHistoryChanged;
            _speedHistory.Add(DateTimeOffset.Now, tracker.TotalSpeed);
            DrawSpeedGraph();
            _speedSampleTimer?.Start();
            Closed += (_, _) =>
            {
                _historySearchTimer?.Stop();
                _activeRefreshTimer?.Stop();
                _speedSampleTimer?.Stop();
                tracker.Changed -= OnTrackerChanged;
                tracker.Completed -= OnTrackerHistoryChanged;
                tracker.Failed -= OnTrackerHistoryChanged;
            };
        }
        else
        {
            Closed += (_, _) =>
            {
                _historySearchTimer?.Stop();
                _activeRefreshTimer?.Stop();
                _speedSampleTimer?.Stop();
            };
        }
    }

    private Row? Selected => DownloadsList.SelectedItem as Row;
    private ActiveRow? SelectedActive => ActiveList.SelectedItem as ActiveRow;

    // ───────────────────────── history ─────────────────────────

    private void Refresh()
    {
        _allRecords = DownloadHistoryStore.Load(AppPaths.DownloadsHistoryPath, _historyCap);
        try { _hasUndoBatch = DownloadSortJournalStore.LastBatch(AppPaths.DownloadSortJournalPath) is not null; }
        catch { _hasUndoBatch = false; }
        ApplyHistoryQuery();
        UpdateButtons();
    }

    /// <summary>
    /// Reload history after a completion/failure event, but only rebind
    /// when something actually changed so a notification cannot steal the
    /// user's selection unnecessarily.
    /// </summary>
    private void RefreshHistoryOnly()
    {
        var loaded = DownloadHistoryStore.Load(AppPaths.DownloadsHistoryPath, _historyCap);
        var changed = loaded.Count != _allRecords.Count || loaded.Zip(_allRecords).Any(pair =>
            pair.First.FullPath != pair.Second.FullPath
            || pair.First.CompletedUtc != pair.Second.CompletedUtc
            || pair.First.State != pair.Second.State
            || pair.First.Note != pair.Second.Note
            || pair.First.HashVerified != pair.Second.HashVerified);
        if (!changed)
            return;
        _allRecords = loaded;
        ApplyHistoryQuery();
        UpdateButtons();
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
        _historySearchTimer?.Stop();
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

    private void OnSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _historySearchTimer?.Stop();
        _historySearchTimer?.Start();
    }

    private void OnTrackerChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnTrackerChanged));
            return;
        }
        if (!IsLoaded)
            return;
        _activeRefreshTimer?.Stop();
        _activeRefreshTimer?.Start();
    }

    private void OnTrackerHistoryChanged()
    {
        if (Dispatcher.CheckAccess())
            RefreshHistoryOnly();
        else
            Dispatcher.BeginInvoke(new Action(RefreshHistoryOnly));
    }

    private void UpdateButtons()
    {
        var enabled = Selected is not null;
        OpenButton.IsEnabled = enabled;
        FolderButton.IsEnabled = enabled;
        CopyPathButton.IsEnabled = enabled;
        OpenWithButton.IsEnabled = enabled;
        DeleteFileButton.IsEnabled = enabled;
        // R4 actions: note/verify act on the selection; sort/archive act
        // on the whole completed history; undo needs a journaled batch.
        NoteButton.IsEnabled = enabled;
        VerifyButton.IsEnabled = enabled && Selected?.Record.State == DownloadRecordState.Completed;
        var anyCompleted = _allRecords.Any(r => r.State == DownloadRecordState.Completed);
        SortButton.IsEnabled = anyCompleted;
        ArchiveButton.IsEnabled = anyCompleted;
        UndoSortButton.IsEnabled = _hasUndoBatch;
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
        // The graph has its own low-frequency sample timer; progress
        // events may arrive in bursts and must not distort the time axis.
    }

    /// <summary>Redraw the speed polyline from the sampled history (R4 #135).</summary>
    private void DrawSpeedGraph()
    {
        if (SpeedPolyline is null || SpeedCanvas is null)
            return;
        var width = SpeedCanvas.ActualWidth > 0 ? SpeedCanvas.ActualWidth : 560;
        const double height = 44;
        var points = new System.Windows.Media.PointCollection();
        foreach (var (x, y) in _speedHistory.NormalizedPoints(120))
            points.Add(new System.Windows.Point(x * width, height - 2 - y * (height - 4)));
        SpeedPolyline.Points = points;
        SpeedGraphLabel.Text = _speedHistory.Peak > 0
            ? $"Download speed — now {DownloadText.FormatSpeed(_tracker?.TotalSpeed ?? 0)} · peak {DownloadText.FormatSpeed(_speedHistory.Peak)} (this PC only)"
            : "Download speed (this PC only)";
    }

    private void UpdateActiveButtons()
    {
        var row = SelectedActive;
        PauseButton.IsEnabled = row is { State: DownloadActivityState.InProgress, IsSessionFailed: false };
        ResumeButton.IsEnabled = row is { State: DownloadActivityState.Paused, IsSessionFailed: false };
        CancelButton.IsEnabled = row is { IsSessionFailed: false } &&
                                  row.State is DownloadActivityState.InProgress or DownloadActivityState.Paused;
        RetryButton.IsEnabled = row is { IsSessionFailed: true } && _retryAsync is not null;
        var active = _tracker?.Snapshot() ?? Array.Empty<ActiveDownload>();
        PauseAllButton.IsEnabled = active.Any(download => download.State == DownloadActivityState.InProgress);
        ResumeAllButton.IsEnabled = active.Any(download => download.State == DownloadActivityState.Paused && download.CanResume);
    }

    private void OnPauseAll(object sender, RoutedEventArgs e) => _tracker?.PauseAll();

    private void OnResumeAll(object sender, RoutedEventArgs e) => _tracker?.ResumeAll();

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

    // ───────────────────────── R4: notes (#137) + verify (#136) ─────────────────────────

    /// <summary>Attach or edit the selected entry's note (R4 #137). The note persists with the history file, on this PC only.</summary>
    private async void OnEditNote(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        var prompt = new TextPromptWindow(
            "Download note",
            $"Note for \"{row.Record.FileName}\" — kept with the download history on this PC only. Clear the text to remove the note.",
            row.Record.Note ?? "",
            multiline: false,
            okText: "Save note")
        { Owner = this };
        ShellTextScaling.Apply(prompt, _settings.ShellFontScale);
        if (prompt.ShowDialog() != true)
            return;
        try
        {
            if (await DownloadHistoryStore.SetNoteAsync(AppPaths.DownloadsHistoryPath, row.Record, prompt.Text, _historyCap))
                NoteText.Text = $"Note saved for \"{row.Record.FileName}\".";
            else
                NoteText.Text = "That entry is no longer in the history — the note wasn't saved.";
        }
        catch (Exception ex)
        {
            _log.Warn($"Download note save failed: {ex.Message}");
            NoteText.Text = "Couldn't save the note.";
        }
        Refresh();
    }

    /// <summary>
    /// Verify the selected file against a pasted SHA-256 (R4 #136). The
    /// hash runs off the UI thread; the verdict persists on the entry so
    /// the pass/fail state survives the window closing.
    /// </summary>
    private async void OnVerifyHash(object sender, RoutedEventArgs e)
    {
        if (_hashCancellation is { } activeHash)
        {
            activeHash.Cancel();
            NoteText.Text = "Cancelling SHA-256 verification…";
            return;
        }
        if (Selected is not { } row)
            return;
        if (!File.Exists(row.Record.FullPath))
        {
            NoteText.Text = $"\"{row.Record.FileName}\" isn't at that location anymore (moved or deleted).";
            return;
        }
        var prompt = new TextPromptWindow(
            "Verify SHA-256",
            $"Paste the SHA-256 hash \"{row.Record.FileName}\" should have (64 hex characters; spaces and colons are fine). MuseDesktop hashes the file on this PC and compares — nothing is sent anywhere.",
            "",
            multiline: false,
            okText: "Verify")
        { Owner = this };
        ShellTextScaling.Apply(prompt, _settings.ShellFontScale);
        if (prompt.ShowDialog() != true)
            return;
        if (!DownloadHash.TryNormalizeSha256(prompt.Text, out _))
        {
            NoteText.Text = "That doesn't look like a SHA-256 hash (64 hex characters) — nothing was checked.";
            return;
        }

        var path = row.Record.FullPath;
        var expected = prompt.Text;
        var tokenSource = new CancellationTokenSource();
        _hashCancellation = tokenSource;
        VerifyButton.Content = "Cancel verify";
        VerifyButton.IsEnabled = true;
        long totalBytes;
        try { totalBytes = new FileInfo(path).Length; }
        catch { totalBytes = 0; }
        long lastShown = 0;
        var hashFinished = false;
        var progress = new Progress<long>(bytesRead =>
        {
            if (hashFinished || !ReferenceEquals(_hashCancellation, tokenSource))
                return;
            if (bytesRead - lastShown < 1024 * 1024 && bytesRead < totalBytes)
                return;
            lastShown = bytesRead;
            NoteText.Text = $"Checking SHA-256… {DownloadText.FormatBytes(bytesRead)} of {DownloadText.FormatBytes(totalBytes)} read. Select Cancel verify to stop.";
        });
        try
        {
            var result = await DownloadHash.VerifyAsync(path, expected, progress, tokenSource.Token);
            hashFinished = true;
            if (result.Error is not null)
            {
                NoteText.Text = $"Couldn't check \"{row.Record.FileName}\": {result.Error}";
            }
            else
            {
                try
                {
                    await DownloadHistoryStore.SetHashVerifiedAsync(AppPaths.DownloadsHistoryPath, row.Record, result.Matches, _historyCap);
                }
                catch (Exception ex)
                {
                    _log.Warn($"Hash verdict save failed: {ex.Message}");
                }
                _log.Info($"SHA-256 check: {row.Record.FileName} — {(result.Matches ? "match" : "MISMATCH")}");
                NoteText.Text = result.Matches
                    ? $"SHA-256 matches — \"{row.Record.FileName}\" is exactly the file that hash describes."
                    : $"SHA-256 does NOT match — \"{row.Record.FileName}\" is not the file that hash describes. Treat it with care.";
            }
            Refresh();
        }
        catch (OperationCanceledException)
        {
            NoteText.Text = $"SHA-256 verification cancelled for \"{row.Record.FileName}\"; no verdict was saved.";
        }
        finally
        {
            hashFinished = true;
            if (ReferenceEquals(_hashCancellation, tokenSource))
                _hashCancellation = null;
            tokenSource.Dispose();
            VerifyButton.Content = "Verify SHA-256…";
            UpdateButtons();
        }
    }

    // ───────────────────────── R4: sort / archive / undo (#131, #138) ─────────────────────────

    /// <summary>
    /// Apply a move plan to disk, journal it for undo, and point the
    /// history at the new locations. Returns the moves that actually
    /// happened. File names only in the log — moves are local facts, but
    /// the discipline costs nothing here.
    /// </summary>
    private List<DownloadMove> ApplyMoves(IReadOnlyList<DownloadMove> moves, bool journal = true)
    {
        var execution = DownloadMoveTransaction.Apply(moves, File.Exists, (source, destination) =>
        {
            var dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.Move(source, destination);
        });
        var applied = execution.Applied.ToList();
        if (execution.FailedMove is { } failedMove && execution.Failure is { } failure)
        {
            _log.Warn($"Download move failed ({failedMove.RuleName}): {Path.GetFileName(failedMove.SourcePath)} — {failure.Message}");
            foreach (var rollback in execution.RollbackFailures)
                _log.Error($"Download move rollback failed ({rollback.Move.RuleName}): {Path.GetFileName(rollback.Move.DestinationPath)} — {rollback.Error.Message}");
        }

        if (applied.Count == 0)
            return applied;

        if (journal)
        {
            try
            {
                DownloadSortJournalStore.Append(AppPaths.DownloadSortJournalPath, new DownloadSortBatch
                {
                    AppliedUtc = DateTimeOffset.UtcNow,
                    Moves = applied.ToList(),
                });
            }
            catch (Exception ex)
            {
                _log.Warn($"Download sort journal save failed; attempting to roll back moved files: {ex.Message}");
                RollBackMoves(applied);
                if (applied.Count == 0)
                    return applied;

                // If some files could not be restored, make one more attempt
                // to record only those still moved, so Undo can recover them.
                try
                {
                    DownloadSortJournalStore.Append(AppPaths.DownloadSortJournalPath, new DownloadSortBatch
                    {
                        AppliedUtc = DateTimeOffset.UtcNow,
                        Moves = applied.ToList(),
                    });
                }
                catch (Exception retryException)
                {
                    _log.Error($"Could not journal files that remain moved after rollback failed: {retryException.Message}");
                }
            }
        }

        try
        {
            var map = applied.ToDictionary(m => m.SourcePath, m => m.DestinationPath);
            DownloadHistoryStore.RewritePaths(AppPaths.DownloadsHistoryPath, map, _historyCap);
        }
        catch (Exception ex)
        {
            _log.Warn($"Download history path update failed: {ex.Message}");
        }
        return applied;
    }

    /// <summary>Best-effort reverse of moves, retaining only files still at their new locations.</summary>
    private void RollBackMoves(List<DownloadMove> applied)
    {
        for (var i = applied.Count - 1; i >= 0; i--)
        {
            var move = applied[i];
            try
            {
                if (!File.Exists(move.DestinationPath))
                {
                    applied.RemoveAt(i);
                    continue;
                }
                if (File.Exists(move.SourcePath))
                    throw new IOException("The original path is now occupied; refusing to overwrite it during rollback.");
                File.Move(move.DestinationPath, move.SourcePath);
                applied.RemoveAt(i);
            }
            catch (Exception ex)
            {
                _log.Error($"Download move rollback failed ({move.RuleName}): {Path.GetFileName(move.DestinationPath)} — {ex.Message}");
            }
        }
    }

    /// <summary>The preview text for a move plan: one line per move, capped, so the confirm dialog stays readable.</summary>
    private static string PreviewText(IReadOnlyList<DownloadMove> moves)
    {
        var lines = moves.Take(12).Select(m => $"• {Path.GetFileName(m.SourcePath)}  →  {m.DestinationPath}");
        var text = string.Join("\n", lines);
        if (moves.Count > 12)
            text += $"\n• …and {moves.Count - 12} more";
        return text;
    }

    /// <summary>Sort finished downloads into subfolders (R4 #131): plan per downloads folder, preview, then move.</summary>
    private void OnSortDownloads(object sender, RoutedEventArgs e)
    {
        var completed = _allRecords.Where(r => r.State == DownloadRecordState.Completed).ToList();
        var moves = new List<DownloadMove>();
        foreach (var group in completed.GroupBy(
                     r => Path.GetDirectoryName(r.FullPath) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0)
                continue;
            var archive = _settings.DownloadArchiveAfterDays > 0
                ? new DownloadArchiveOptions(
                    _settings.DownloadArchiveFolder ?? Path.Combine(group.Key, "Archive"),
                    _settings.DownloadArchiveAfterDays)
                : null;
            moves.AddRange(DownloadSortPlanner.Plan(
                group, group.Key, _settings.DownloadSortRules, _settings.DownloadSortUseCategories,
                File.Exists, archive, DateTimeOffset.Now));
        }
        if (moves.Count == 0)
        {
            NoteText.Text = "Nothing to sort — every finished download is already in a subfolder, or no rule matches it.";
            return;
        }
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            $"Move {moves.Count} file(s) into subfolders?\n\n{PreviewText(moves)}\n\n" +
            "Nothing is overwritten (name clashes get a \" (1)\" suffix), and Undo sort can put everything back.",
            "Sort downloads", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        var applied = ApplyMoves(moves);
        NoteText.Text = applied.Count == moves.Count
            ? $"Sorted {applied.Count} file(s). Undo sort can put them back."
            : $"Sorted {applied.Count} of {moves.Count} file(s) — the rest couldn't be moved (see the log). Undo sort can put back the ones that moved.";
        Refresh();
    }

    /// <summary>Archive finished downloads into dated folders (R4 #138): same preview + journal discipline as Sort.</summary>
    private void OnArchiveDownloads(object sender, RoutedEventArgs e)
    {
        var completed = _allRecords.Where(r => r.State == DownloadRecordState.Completed).ToList();
        var moves = new List<DownloadMove>();
        foreach (var group in completed.GroupBy(
                     r => Path.GetDirectoryName(r.FullPath) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0)
                continue;
            var root = _settings.DownloadArchiveFolder ?? Path.Combine(group.Key, "Archive");
            moves.AddRange(DownloadSortPlanner.PlanArchive(group, root, File.Exists));
        }
        if (moves.Count == 0)
        {
            NoteText.Text = "Nothing to archive — every finished download is already in the archive.";
            return;
        }
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            $"Move {moves.Count} file(s) into the archive, in folders named by the day each finished downloading?\n\n{PreviewText(moves)}\n\n" +
            "Nothing is overwritten, and Undo sort can put everything back.",
            "Archive downloads", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        var applied = ApplyMoves(moves);
        NoteText.Text = $"Archived {applied.Count} of {moves.Count} file(s). Undo sort can put them back.";
        Refresh();
    }

    /// <summary>Undo the newest sort/archive batch (R4 #131): reverse its moves, then consume the batch.</summary>
    private void OnUndoSort(object sender, RoutedEventArgs e)
    {
        var journalPath = AppPaths.DownloadSortJournalPath;
        var batch = DownloadSortJournalStore.LastBatch(journalPath);
        if (batch is null)
        {
            NoteText.Text = "Nothing to undo.";
            Refresh();
            return;
        }
        var undo = DownloadSortPlanner.PlanUndo(batch, File.Exists);
        if (undo.Count == 0)
        {
            DownloadSortJournalStore.RemoveLast(journalPath);
            NoteText.Text = "Those files aren't where the sort left them anymore — nothing left to undo.";
            Refresh();
            return;
        }
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            $"Move {undo.Count} file(s) back where they were before the last sort/archive?\n\n{PreviewText(undo)}\n\n" +
            "Nothing is overwritten — a file that now sits at an original spot keeps it, and the returning file gets a \" (1)\" suffix.",
            "Undo sort", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        var applied = ApplyMoves(undo, journal: false);
        if (applied.Count == undo.Count)
            DownloadSortJournalStore.RemoveLast(journalPath);
        NoteText.Text = applied.Count == undo.Count
            ? $"Put {applied.Count} of {undo.Count} file(s) back."
            : $"Put {applied.Count} of {undo.Count} file(s) back; the undo entry was kept so you can retry the remaining move(s).";
        Refresh();
    }

    // ───────────────────────── R4: batch link capture (#134) ─────────────────────────

    /// <summary>
    /// Paste-a-list batch capture (R4 #134): parse + validate in Core,
    /// show exactly what will download (and what was dropped), then hand
    /// the URLs to the main window, which starts them under the
    /// in-flight cap.
    /// </summary>
    private async void OnAddLinks(object sender, RoutedEventArgs e)
    {
        if (_startBatchAsync is null)
            return;
        var prompt = new TextPromptWindow(
            "Download a list of links",
            "Paste one link per line. MuseDesktop keeps only http/https links, drops duplicates, and asks before downloading anything.",
            "",
            multiline: true,
            okText: "Review links")
        { Owner = this };
        ShellTextScaling.Apply(prompt, _settings.ShellFontScale);
        if (prompt.ShowDialog() != true)
            return;
        var parsed = BatchLinkParser.Parse(prompt.Text);
        if (parsed.Links.Count == 0)
        {
            NoteText.Text = parsed.Rejected.Count > 0
                ? $"No usable links in that list — {parsed.Rejected.Count} line(s) weren't http/https links."
                : "That list was empty.";
            return;
        }
        var names = string.Join("\n", parsed.Links.Take(10).Select(l => $"• {l.FileName}"));
        if (parsed.Links.Count > 10)
            names += $"\n• …and {parsed.Links.Count - 10} more";
        var rejected = parsed.Rejected.Count > 0
            ? $"\n\n{parsed.Rejected.Count} line(s) were skipped (not usable links, duplicates, or past the {BatchLinkParser.MaxLinks}-link cap)."
            : "";
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            $"Download {parsed.Links.Count} file(s)? At most {DownloadConcurrency.MaxConcurrentDownloads} run at once; the rest queue.\n\n{names}{rejected}",
            "Download links", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        try
        {
            await _startBatchAsync(parsed.Links);
            NoteText.Text = $"Batch started: {parsed.Links.Count} link(s) — watch the active list above.";
        }
        catch (Exception ex)
        {
            _log.Warn($"Batch download start failed: {ex.Message}");
            NoteText.Text = "Couldn't start the batch — see the log.";
        }
        RefreshActive();
    }
}
