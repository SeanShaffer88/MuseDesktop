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
    private readonly DispatcherTimer? _refreshTimer;
    private readonly DownloadSpeedHistory _speedHistory = new();
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
                    ? string.IsNullOrWhiteSpace(Record.FailureReason)
                        ? Strings.Get("dl.row_failed") + " · "
                        : Strings.Format("dl.row_failed_reason", Record.FailureReason)
                    : "";
                // R4 #137/#136: the user's note and the hash verdict ride
                // the same line — both are facts about THIS entry.
                var note = string.IsNullOrWhiteSpace(Record.Note) ? "" : Strings.Format("dl.row_note", Record.Note);
                var hash = Record.HashVerified switch
                {
                    true => Strings.Get("dl.row_hash_ok"),
                    false => Strings.Get("dl.row_hash_failed"),
                    null => "",
                };
                return state + Strings.Format("dl.row_main",
                    DownloadText.FormatBytes(Record.SizeBytes),
                    Record.CompletedUtc.ToLocalTime(), Record.FullPath, note, hash);
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
        try { _hasUndoBatch = DownloadSortJournalStore.LastBatch(AppPaths.DownloadSortJournalPath) is not null; }
        catch { _hasUndoBatch = false; }
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
            || pair.First.State != pair.Second.State
            || pair.First.Note != pair.Second.Note
            || pair.First.HashVerified != pair.Second.HashVerified);
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
            NoteText.Text = Strings.Get("ui.nothing_here_yet_files_you_download");
        else if (filtered.Count == 0)
            NoteText.Text = Strings.Get("ui.no_downloads_match_the_current_search");
        else
            NoteText.Text = Strings.Get("ui.files_muse_downloaded_for_you_the_list");
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
            NoteText.Text = Strings.Format("note.file_not_at_location", row.Record.FileName);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(row.Record.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"Downloads window open failed: {ex.Message}");
            NoteText.Text = Strings.Format("note.couldnt_open_file", row.Record.FileName);
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
                NoteText.Text = Strings.Format("note.folder_no_longer_exists", row.Record.FileName);
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
            NoteText.Text = Strings.Format("note.copied_path", row.Record.FullPath);
        }
        catch (Exception ex)
        {
            _log.Warn($"Downloads window copy-path failed: {ex.Message}");
            NoteText.Text = Strings.Get("ui.couldn_t_copy_the_path_the_clipboard_is");
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
            NoteText.Text = Strings.Format("note.file_not_at_location", row.Record.FileName);
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
            NoteText.Text = Strings.Format("note.couldnt_open_with_dialog", row.Record.FileName);
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
            Strings.Format("msg.delete_downloaded_body", row.Record.FileName),
            Strings.Get("msg.delete_downloaded_file"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
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
            NoteText.Text = Strings.Format("note.deleted_file", row.Record.FileName);
        }
        catch (Exception ex)
        {
            _log.Warn($"Downloads window delete-file failed: {ex.Message}");
            NoteText.Text = Strings.Format("note.couldnt_delete_file", row.Record.FileName, ex.Message);
        }
        Refresh();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Get("msg.clear_history_body"),
            Strings.Get("msg.clear_download_history"), MessageBoxButton.YesNo, MessageBoxImage.Question);
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
                Detail = DownloadLines.ActiveDetail(d, now),
                State = d.State,
                CanResume = d.CanResume,
            })
            .ToList();
        rows.AddRange(_tracker.SessionFailed.Select(f => new ActiveRow
        {
            Id = f.Id,
            FileName = f.FileName,
            Detail = Strings.Get("dl.failed_this_session"),
            State = DownloadActivityState.Failed,
            IsSessionFailed = true,
        }));
        ActiveList.ItemsSource = rows;
        if (selectedId is not null)
            ActiveList.SelectedItem = rows.FirstOrDefault(r => r.Id == selectedId);
        ActiveNote.Text = rows.Count == 0
            ? Strings.Get("nothing_is_downloading_right_now")
            : Strings.Format("dl.active_items_note", rows.Count);
        UpdateActiveButtons();
        // R4 #135: feed the speed graph from the same poll that refreshes
        // this list. Samples live in memory only — closing the window
        // forgets them, and they never leave the PC.
        _speedHistory.Add(DateTimeOffset.Now, _tracker.TotalSpeed);
        DrawSpeedGraph();
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
            ? Strings.Format("dl.speed_graph",
                DownloadText.FormatSpeed(_tracker?.TotalSpeed ?? 0),
                DownloadText.FormatSpeed(_speedHistory.Peak))
            : Strings.Get("dl.speed_graph_plain");
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
            NoteText.Text = Strings.Format("note.retrying_file", row.FileName);
        }
        catch (Exception ex)
        {
            _log.Warn($"Retry download failed: {ex.Message}");
            NoteText.Text = Strings.Format("note.couldnt_retry_file", row.FileName);
        }
        RefreshActive();
    }

    // ───────────────────────── R4: notes (#137) + verify (#136) ─────────────────────────

    /// <summary>Attach or edit the selected entry's note (R4 #137). The note persists with the history file, on this PC only.</summary>
    private void OnEditNote(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        var prompt = new TextPromptWindow(
            Strings.Get("dl.note_title"),
            Strings.Format("dl.note_body", row.Record.FileName),
            row.Record.Note ?? "",
            multiline: false,
            okText: Strings.Get("save_note"))
        { Owner = this };
        ShellTextScaling.Apply(prompt, _settings.ShellFontScale);
        if (prompt.ShowDialog() != true)
            return;
        try
        {
            if (DownloadHistoryStore.SetNote(AppPaths.DownloadsHistoryPath, row.Record, prompt.Text, _historyCap))
                NoteText.Text = Strings.Format("note.note_saved_for", row.Record.FileName);
            else
                NoteText.Text = Strings.Get("ui.that_entry_is_no_longer_in_the_history");
        }
        catch (Exception ex)
        {
            _log.Warn($"Download note save failed: {ex.Message}");
            NoteText.Text = Strings.Get("ui.couldn_t_save_the_note");
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
        if (Selected is not { } row)
            return;
        if (!File.Exists(row.Record.FullPath))
        {
            NoteText.Text = Strings.Format("note.file_not_at_location", row.Record.FileName);
            return;
        }
        var prompt = new TextPromptWindow(
            Strings.Get("dl.verify_title"),
            Strings.Format("dl.verify_body", row.Record.FileName),
            "",
            multiline: false,
            okText: Strings.Get("dl.verify_ok"))
        { Owner = this };
        ShellTextScaling.Apply(prompt, _settings.ShellFontScale);
        if (prompt.ShowDialog() != true)
            return;
        if (!DownloadHash.TryNormalizeSha256(prompt.Text, out _))
        {
            NoteText.Text = Strings.Get("ui.that_doesn_t_look_like_a_sha_256_hash");
            return;
        }
        VerifyButton.IsEnabled = false;
        var path = row.Record.FullPath;
        var expected = prompt.Text;
        var result = await Task.Run(() => DownloadHash.Verify(path, expected));
        if (result.Error is not null)
        {
            NoteText.Text = Strings.Format("note.couldnt_check_file", row.Record.FileName, result.Error);
        }
        else
        {
            try
            {
                DownloadHistoryStore.SetHashVerified(AppPaths.DownloadsHistoryPath, row.Record, result.Matches, _historyCap);
            }
            catch (Exception ex)
            {
                _log.Warn($"Hash verdict save failed: {ex.Message}");
            }
            _log.Info($"SHA-256 check: {row.Record.FileName} — {(result.Matches ? "match" : "MISMATCH")}");
            NoteText.Text = result.Matches
                ? Strings.Format("dl.hash_matches", row.Record.FileName)
                : Strings.Format("dl.hash_mismatch", row.Record.FileName);
        }
        Refresh();
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
        var applied = new List<DownloadMove>();
        foreach (var move in moves)
        {
            try
            {
                var dir = Path.GetDirectoryName(move.DestinationPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.Move(move.SourcePath, move.DestinationPath);
                applied.Add(move);
            }
            catch (Exception ex)
            {
                _log.Warn($"Download move failed ({move.RuleName}): {Path.GetFileName(move.SourcePath)} — {ex.Message}");
            }
        }
        if (applied.Count == 0)
            return applied;
        try
        {
            if (journal)
            {
                DownloadSortJournalStore.Append(AppPaths.DownloadSortJournalPath, new DownloadSortBatch
                {
                    AppliedUtc = DateTimeOffset.UtcNow,
                    Moves = applied,
                });
            }
            var map = applied.ToDictionary(m => m.SourcePath, m => m.DestinationPath);
            DownloadHistoryStore.RewritePaths(AppPaths.DownloadsHistoryPath, map, _historyCap);
        }
        catch (Exception ex)
        {
            _log.Warn($"Download sort bookkeeping failed: {ex.Message}");
        }
        return applied;
    }

    /// <summary>The preview text for a move plan: one line per move, capped, so the confirm dialog stays readable.</summary>
    private static string PreviewText(IReadOnlyList<DownloadMove> moves)
    {
        var lines = moves.Take(12).Select(m => Strings.Format("dl.sort_line", Path.GetFileName(m.SourcePath), m.DestinationPath));
        var text = string.Join("\n", lines);
        if (moves.Count > 12)
            text += "\n" + Strings.Format("dl.sort_more", moves.Count - 12);
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
            NoteText.Text = Strings.Get("ui.nothing_to_sort_every_finished_download");
            return;
        }
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Format("msg.sort_downloads_body", moves.Count, PreviewText(moves)),
            Strings.Get("msg.sort_downloads"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        var applied = ApplyMoves(moves);
        NoteText.Text = applied.Count == moves.Count
            ? Strings.Format("note.sorted_all", applied.Count)
            : Strings.Format("note.sorted_some", applied.Count, moves.Count);
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
            NoteText.Text = Strings.Get("ui.nothing_to_archive_every_finished");
            return;
        }
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Format("msg.archive_downloads_body", moves.Count, PreviewText(moves)),
            Strings.Get("msg.archive_downloads"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        var applied = ApplyMoves(moves);
        NoteText.Text = Strings.Format("note.archived_some", applied.Count, moves.Count);
        Refresh();
    }

    /// <summary>Undo the newest sort/archive batch (R4 #131): reverse its moves, then consume the batch.</summary>
    private void OnUndoSort(object sender, RoutedEventArgs e)
    {
        var journalPath = AppPaths.DownloadSortJournalPath;
        var batch = DownloadSortJournalStore.LastBatch(journalPath);
        if (batch is null)
        {
            NoteText.Text = Strings.Get("ui.nothing_to_undo");
            Refresh();
            return;
        }
        var undo = DownloadSortPlanner.PlanUndo(batch, File.Exists);
        if (undo.Count == 0)
        {
            DownloadSortJournalStore.RemoveLast(journalPath);
            NoteText.Text = Strings.Get("ui.those_files_aren_t_where_the_sort_left");
            Refresh();
            return;
        }
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Format("msg.undo_sort_body", undo.Count, PreviewText(undo)),
            Strings.Get("msg.undo_sort"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        var applied = ApplyMoves(undo, journal: false);
        DownloadSortJournalStore.RemoveLast(journalPath);
        NoteText.Text = Strings.Format("note.put_back_some", applied.Count, undo.Count);
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
            Strings.Get("dl.batch_title"),
            Strings.Get("dl.batch_body"),
            "",
            multiline: true,
            okText: Strings.Get("dl.batch_ok"))
        { Owner = this };
        ShellTextScaling.Apply(prompt, _settings.ShellFontScale);
        if (prompt.ShowDialog() != true)
            return;
        var parsed = BatchLinkParser.Parse(prompt.Text);
        if (parsed.Links.Count == 0)
        {
            NoteText.Text = parsed.Rejected.Count > 0
                ? Strings.Format("dl.batch_none_usable", parsed.Rejected.Count)
                : Strings.Get("dl.batch_empty");
            return;
        }
        var names = string.Join("\n", parsed.Links.Take(10).Select(l => Strings.Format("msg.batch_link_line", l.FileName)));
        if (parsed.Links.Count > 10)
            names += Strings.Format("msg.batch_more_links", parsed.Links.Count - 10);
        var rejected = parsed.Rejected.Count > 0
            ? Strings.Format("msg.batch_rejected_lines", parsed.Rejected.Count, BatchLinkParser.MaxLinks)
            : "";
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Format("msg.download_links_body", parsed.Links.Count,
                DownloadConcurrency.MaxConcurrentDownloads, names + rejected),
            Strings.Get("msg.download_links"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        try
        {
            await _startBatchAsync(parsed.Links);
            NoteText.Text = Strings.Format("note.batch_started", parsed.Links.Count);
        }
        catch (Exception ex)
        {
            _log.Warn($"Batch download start failed: {ex.Message}");
            NoteText.Text = Strings.Get("ui.couldn_t_start_the_batch_see_the_log");
        }
        RefreshActive();
    }
}
