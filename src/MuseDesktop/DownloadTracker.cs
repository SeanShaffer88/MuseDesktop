using System.Diagnostics;
using System.IO;
using Microsoft.Web.WebView2.Core;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// A download that failed during THIS session and can be re-issued
/// (S2 #15). The source URI lives only in this in-memory object: it is
/// never written to disk or to the log (the log gets host+path only,
/// via <see cref="UrlPolicy.RedactForLog"/>), and the retry offer dies
/// with the process — exactly the privacy rule the history store follows.
/// </summary>
public sealed class SessionFailedDownload
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public required string Uri { get; init; }
}

/// <summary>
/// Follows ALL active WebView2 downloads (v0.6.0, S2 #13 — it followed
/// exactly one before): each operation gets a Core
/// <see cref="ActiveDownload"/> model in an <see cref="ActiveDownloadSet"/>,
/// progress flows to the status bar (the existing single-download wording
/// when one download runs; an aggregate when several do), and the
/// Downloads window / taskbar read the same models. Pause/resume ride
/// WebView2's own Pause()/Resume()/CanResume (present in the pinned SDK
/// 1.0.4258.31; pausing surfaces as Interrupted + UserPaused, NOT a
/// failure). Completed and failed downloads both produce history records
/// through the finished callback; user-cancelled ones do not.
/// </summary>
public sealed class DownloadTracker
{
    private sealed class Entry
    {
        public required CoreWebView2DownloadOperation Op { get; init; }
        public required ActiveDownload Model { get; init; }
        public DownloadRateSampler Sampler { get; } = new();
        public EventHandler<object>? BytesHandler;
        public EventHandler<object>? EndTimeHandler;
        public EventHandler<object>? StateHandler;
        public bool Final;
    }

    private readonly DiagnosticLog _log;
    private readonly Action<string, string?> _setStatus;
    private readonly Action<DownloadRecord> _onFinished;
    private readonly Action<string>? _onCompletedNotify;

    private readonly List<Entry> _entries = new();
    private readonly ActiveDownloadSet _set = new();
    private readonly List<SessionFailedDownload> _sessionFailed = new();
    private long _nextId;

    public DownloadTracker(
        DiagnosticLog log,
        Action<string, string?> setStatus,
        Action<DownloadRecord> onFinished,
        Action<string>? onCompletedNotify = null)
    {
        _log = log;
        _setStatus = setStatus;
        _onFinished = onFinished;
        _onCompletedNotify = onCompletedNotify;
    }

    /// <summary>Any download tracked (in progress or paused).</summary>
    public bool IsActive => _entries.Count > 0;

    /// <summary>Any download actually transferring (tray suspend must not sleep mid-transfer; a paused one may sleep).</summary>
    public bool IsTransferring => _set.AnyTransferring;

    public bool AnyPaused => _set.AnyPaused;
    public double? OverallFraction => _set.OverallFraction;

    /// <summary>Aggregate transfer speed across in-progress downloads (bytes/sec) — the speed graph's and governor's input (R4 #132/#135).</summary>
    public double TotalSpeed => _set.TotalSpeed;

    /// <summary>The live models, newest last — the Downloads window's active list.</summary>
    public IReadOnlyList<ActiveDownload> Snapshot() => _set.Items.ToList();

    /// <summary>Downloads that failed this session and can be retried (in-memory only, S2 #15).</summary>
    public IReadOnlyList<SessionFailedDownload> SessionFailed => _sessionFailed.ToList();

    /// <summary>The last completed file, for click-to-open in the status bar.</summary>
    public string? CompletedPath { get; private set; }

    /// <summary>The most recent failure's file name + reason (S6 #54), so the announce wiring can name it.</summary>
    public string? LastFailedName { get; private set; }
    public string? LastFailedReason { get; private set; }

    /// <summary>Raised when the tracked set goes empty ↔ non-empty — drives the ✕ button.</summary>
    public event Action<bool>? ActiveChanged;

    /// <summary>Raised when a download completes — the status text becomes clickable.</summary>
    public event Action? Completed;

    /// <summary>Raised when a new download attaches (a fresh attempt clears a latched failure state).</summary>
    public event Action? Started;

    /// <summary>Raised when a download fails (not on pause or user-cancel).</summary>
    public event Action? Failed;

    /// <summary>Raised on any progress/state change — taskbar and governor listeners use this.</summary>
    public event Action? Changed;

    /// <summary>Raised when a final state frees a batch-download slot.</summary>
    public event Action? CapacityChanged;

    /// <summary>Raised for operation state transitions, not byte-progress updates.</summary>
    public event Action? TransferStateChanged;

    /// <summary>Take over a download whose destination has already been decided by policy.</summary>
    public void Attach(CoreWebView2DownloadOperation op)
    {
        var id = (++_nextId).ToString();
        var model = new ActiveDownload
        {
            Id = id,
            FileName = Path.GetFileName(op.ResultFilePath),
            FullPath = op.ResultFilePath,
            BytesReceived = op.BytesReceived,
            TotalBytes = op.TotalBytesToReceive is { } t ? (long)t : null,
            EstimatedEnd = op.EstimatedEndTime,
            State = DownloadActivityState.InProgress,
            CanResume = op.CanResume,
        };
        var entry = new Entry { Op = op, Model = model };
        entry.BytesHandler = (_, _) => OnProgress(entry);
        entry.EndTimeHandler = (_, _) => OnProgress(entry);
        entry.StateHandler = (_, _) => OnStateChanged(entry);
        op.BytesReceivedChanged += entry.BytesHandler;
        op.EstimatedEndTimeChanged += entry.EndTimeHandler;
        op.StateChanged += entry.StateHandler;

        var wasEmpty = _entries.Count == 0;
        _entries.Add(entry);
        _set.Add(model);
        CompletedPath = null;
        _log.Info($"Download started: {model.FileName}");
        if (wasEmpty)
            ActiveChanged?.Invoke(true);
        Started?.Invoke();
        RefreshStatus();
        Changed?.Invoke();
    }

    /// <summary>Cancel the most recently started download (the status-bar ✕).</summary>
    public void Cancel()
    {
        var entry = _entries.LastOrDefault();
        if (entry is not null)
            Cancel(entry.Model.Id);
    }

    public void Cancel(string id)
    {
        var entry = Find(id);
        if (entry is null) return;
        try { entry.Op.Cancel(); }
        catch (Exception ex) { _log.Warn($"Cancel download failed: {ex.Message}"); }
    }

    /// <summary>Pause one download (S2 #14). The state change arrives via StateChanged as Interrupted + UserPaused.</summary>
    public void Pause(string id)
    {
        var entry = Find(id);
        if (entry is null || entry.Model.State != DownloadActivityState.InProgress) return;
        try { entry.Op.Pause(); }
        catch (Exception ex) { _log.Warn($"Pause download failed: {ex.Message}"); }
    }

    /// <summary>Resume a paused download (S2 #14), only when WebView2 says it can resume.</summary>
    public void Resume(string id)
    {
        var entry = Find(id);
        if (entry is null || entry.Model.State != DownloadActivityState.Paused) return;
        try
        {
            if (entry.Op.CanResume)
            {
                entry.Sampler.Reset();
                entry.Op.Resume();
            }
            else
            {
                _log.Warn($"Download cannot resume: {entry.Model.FileName}");
                _setStatus($"This download can't be resumed: {entry.Model.FileName}", null);
            }
        }
        catch (Exception ex) { _log.Warn($"Resume download failed: {ex.Message}"); }
    }

    public void PauseAll()
    {
        foreach (var id in _entries
                     .Where(entry => entry.Model.State == DownloadActivityState.InProgress)
                     .Select(entry => entry.Model.Id)
                     .ToList())
            Pause(id);
    }

    public void ResumeAll()
    {
        foreach (var id in _entries
                     .Where(entry => entry.Model.State == DownloadActivityState.Paused && entry.Model.CanResume)
                     .Select(entry => entry.Model.Id)
                     .ToList())
            Resume(id);
    }

    /// <summary>Remove and return a session-failed download for a retry attempt (S2 #15), or null.</summary>
    public SessionFailedDownload? TakeSessionFailed(string id)
    {
        var failed = _sessionFailed.FirstOrDefault(f => f.Id == id);
        if (failed is not null)
        {
            _sessionFailed.Remove(failed);
            Changed?.Invoke();
        }
        return failed;
    }

    /// <summary>
    /// Follow a completed file that was moved after finishing (R4 #131:
    /// auto-sort relocates it) so the status bar's click-to-open keeps
    /// pointing at the real file.
    /// </summary>
    public void UpdateCompletedPath(string oldPath, string newPath)
    {
        if (CompletedPath is not null &&
            string.Equals(CompletedPath, oldPath, StringComparison.OrdinalIgnoreCase))
            CompletedPath = newPath;
    }

    /// <summary>Open the completed file (the status-bar click). No-op until a download completes.</summary>
    public void TryOpenCompleted()
    {
        if (CompletedPath is not { } path || !File.Exists(path))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"Open downloaded file failed: {ex.Message}");
            _setStatus($"Couldn't open {Path.GetFileName(path)} — it's in your downloads folder", null);
        }
    }

    private Entry? Find(string id) => _entries.FirstOrDefault(e => e.Model.Id == id);

    private void OnProgress(Entry entry)
    {
        if (entry.Final) return;
        var op = entry.Op;
        var model = entry.Model;
        model.FullPath = op.ResultFilePath;
        model.FileName = Path.GetFileName(op.ResultFilePath);
        model.BytesReceived = op.BytesReceived;
        model.TotalBytes = op.TotalBytesToReceive is { } t ? (long)t : null;
        model.EstimatedEnd = op.EstimatedEndTime;
        model.CanResume = op.CanResume;
        if (model.State == DownloadActivityState.InProgress)
            model.SpeedBytesPerSecond = entry.Sampler.Sample(op.BytesReceived, DateTimeOffset.Now);
        RefreshStatus();
        Changed?.Invoke();
    }

    private void OnStateChanged(Entry entry)
    {
        if (entry.Final) return;
        var op = entry.Op;
        var model = entry.Model;
        var name = Path.GetFileName(op.ResultFilePath);
        model.FileName = name;
        model.FullPath = op.ResultFilePath;
        model.BytesReceived = op.BytesReceived;
        model.CanResume = op.CanResume;

        switch (op.State)
        {
            case CoreWebView2DownloadState.InProgress:
                // Back from a pause/resume: keep tracking, fresh rate.
                if (model.State == DownloadActivityState.Paused)
                {
                    model.State = DownloadActivityState.InProgress;
                    entry.Sampler.Reset();
                    TransferStateChanged?.Invoke();
                    _log.Info($"Download resumed: {name}");
                }
                RefreshStatus();
                Changed?.Invoke();
                return;

            case CoreWebView2DownloadState.Completed:
            {
                _log.Info($"Download completed: {name}");
                model.State = DownloadActivityState.Completed;
                CompletedPath = op.ResultFilePath;
                var record = new DownloadRecord
                {
                    FileName = name,
                    FullPath = op.ResultFilePath,
                    SizeBytes = op.TotalBytesToReceive is { } total ? (long)total : op.BytesReceived,
                    CompletedUtc = DateTimeOffset.UtcNow,
                    State = DownloadRecordState.Completed,
                };
                Finalize(entry);
                _onFinished(record);
                _onCompletedNotify?.Invoke(name);
                if (_entries.Count > 0) RefreshStatus();
                else _setStatus($"Download complete: {name} — click to open", null);
                Completed?.Invoke();
                Changed?.Invoke();
                return;
            }

            case CoreWebView2DownloadState.Interrupted:
            {
                var reason = op.InterruptReason;
                if (reason == CoreWebView2DownloadInterruptReason.UserPaused)
                {
                    // A pause, NOT a failure (S2 #14): stay tracked, no
                    // history record, Resume stays available per CanResume.
                    model.State = DownloadActivityState.Paused;
                    model.SpeedBytesPerSecond = 0;
                    TransferStateChanged?.Invoke();
                    _log.Info($"Download paused: {name}");
                    if (_entries.Count == 1)
                        _setStatus($"Download paused: {name}", null);
                    else
                        RefreshStatus();
                    Changed?.Invoke();
                    return;
                }
                if (reason == CoreWebView2DownloadInterruptReason.UserCanceled)
                {
                    _log.Warn($"Download cancelled: {name}");
                    model.State = DownloadActivityState.Cancelled;
                    Finalize(entry);
                    if (_entries.Count > 0) RefreshStatus();
                    else _setStatus($"Download cancelled: {name}", null);
                    Changed?.Invoke();
                    return;
                }
                // A real failure: history record (S2 #16) + an in-memory,
                // this-session-only retry offer (S2 #15). The URI is read
                // from the operation into memory only — never logged in
                // full, never persisted.
                _log.Warn($"Download interrupted: {name} ({reason})");
                LastFailedName = name;
                LastFailedReason = reason.ToString();
                model.State = DownloadActivityState.Failed;
                var record = new DownloadRecord
                {
                    FileName = name,
                    FullPath = op.ResultFilePath,
                    SizeBytes = op.BytesReceived,
                    CompletedUtc = DateTimeOffset.UtcNow,
                    State = DownloadRecordState.Failed,
                    FailureReason = reason.ToString(),
                };
                var uri = SafeUri(op);
                Finalize(entry);
                _onFinished(record);
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    _sessionFailed.Add(new SessionFailedDownload
                    {
                        Id = model.Id,
                        FileName = name,
                        FullPath = op.ResultFilePath,
                        Uri = uri,
                    });
                    if (_sessionFailed.Count > DownloadRetryPolicy.MaxSessionFailures)
                        _sessionFailed.RemoveAt(0); // Oldest retry address is no longer retained.
                }
                if (_entries.Count > 0) RefreshStatus();
                else _setStatus($"Download interrupted: {name} ({reason})", null);
                Failed?.Invoke();
                Changed?.Invoke();
                return;
            }
        }
    }

    private static string? SafeUri(CoreWebView2DownloadOperation op)
    {
        try { return op.Uri; }
        catch { return null; }
    }

    /// <summary>Stop following an operation that reached a final state.</summary>
    private void Finalize(Entry entry)
    {
        entry.Final = true;
        try
        {
            if (entry.BytesHandler is not null) entry.Op.BytesReceivedChanged -= entry.BytesHandler;
            if (entry.EndTimeHandler is not null) entry.Op.EstimatedEndTimeChanged -= entry.EndTimeHandler;
            if (entry.StateHandler is not null) entry.Op.StateChanged -= entry.StateHandler;
        }
        catch { /* the operation may already be gone; finalizing is bookkeeping */ }
        _entries.Remove(entry);
        _set.Remove(entry.Model.Id);
        CapacityChanged?.Invoke();
        if (_entries.Count == 0)
            ActiveChanged?.Invoke(false);
    }

    /// <summary>
    /// Status bar: one download keeps the byte-identical v0.2.0 wording
    /// (plus the speed detail, S2 #22); several downloads show the Core
    /// aggregate. A single paused download says so.
    /// </summary>
    private void RefreshStatus()
    {
        if (_entries.Count == 0)
            return;
        if (_entries.Count == 1)
        {
            var entry = _entries[0];
            var model = entry.Model;
            if (model.State == DownloadActivityState.Paused)
            {
                _setStatus($"Download paused: {model.FileName}", null);
                return;
            }
            var (text, right) = DownloadText.ProgressStatus(
                model.FileName,
                entry.Op.BytesReceived,
                entry.Op.TotalBytesToReceive is { } total ? (long)total : null,
                entry.Op.EstimatedEndTime,
                DateTimeOffset.Now,
                model.SpeedBytesPerSecond);
            _setStatus(text, right);
            return;
        }
        var aggregate = DownloadText.AggregateStatus(_set, DateTimeOffset.Now);
        _setStatus(aggregate.Text, aggregate.Right);
    }
}
