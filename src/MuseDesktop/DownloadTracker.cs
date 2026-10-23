using System.Diagnostics;
using System.IO;
using Microsoft.Web.WebView2.Core;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// Follows the one active WebView2 download: progress into the status
/// bar, cancel, click-to-open when done, and a history record on
/// completion (v0.4.0 — extracted from the main window; the wording
/// comes from <see cref="DownloadText"/> so it is unit-tested).
/// </summary>
public sealed class DownloadTracker
{
    private readonly DiagnosticLog _log;
    private readonly Action<string, string?> _setStatus;
    private readonly Action<DownloadRecord> _onCompleted;

    private CoreWebView2DownloadOperation? _op;

    public DownloadTracker(DiagnosticLog log, Action<string, string?> setStatus, Action<DownloadRecord> onCompleted)
    {
        _log = log;
        _setStatus = setStatus;
        _onCompleted = onCompleted;
    }

    public bool IsActive => _op is not null;

    /// <summary>The last completed file, for click-to-open in the status bar.</summary>
    public string? CompletedPath { get; private set; }

    /// <summary>Raised when a download starts (true) or ends (false) — drives the ✕ button.</summary>
    public event Action<bool>? ActiveChanged;

    /// <summary>Raised when a download completes — the status text becomes clickable.</summary>
    public event Action? Completed;

    /// <summary>Take over a download whose destination has already been decided by policy.</summary>
    public void Attach(CoreWebView2DownloadOperation op)
    {
        _op = op;
        CompletedPath = null;
        var name = Path.GetFileName(op.ResultFilePath);
        _log.Info($"Download started: {name}");
        ActiveChanged?.Invoke(true);
        _setStatus($"Downloading {name}…", null);

        op.BytesReceivedChanged += OnProgress;
        op.EstimatedEndTimeChanged += OnProgress;
        op.StateChanged += OnStateChanged;
    }

    public void Cancel()
    {
        try { _op?.Cancel(); }
        catch (Exception ex) { _log.Warn($"Cancel download failed: {ex.Message}"); }
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

    private void OnProgress(object? sender, object e)
    {
        if (_op is null) return;
        var (text, right) = DownloadText.ProgressStatus(
            Path.GetFileName(_op.ResultFilePath),
            _op.BytesReceived,
            _op.TotalBytesToReceive is { } total ? (long)total : null,
            _op.EstimatedEndTime,
            DateTimeOffset.Now);
        _setStatus(text, right);
    }

    private void OnStateChanged(object? sender, object e)
    {
        if (_op is null) return;
        var name = Path.GetFileName(_op.ResultFilePath);
        switch (_op.State)
        {
            case CoreWebView2DownloadState.Completed:
                _log.Info($"Download completed: {name}");
                CompletedPath = _op.ResultFilePath;
                _onCompleted(new DownloadRecord
                {
                    FileName = name,
                    FullPath = _op.ResultFilePath,
                    SizeBytes = _op.TotalBytesToReceive is { } total ? (long)total : _op.BytesReceived,
                    CompletedUtc = DateTimeOffset.UtcNow,
                });
                _setStatus($"Download complete: {name} — click to open", null);
                Completed?.Invoke();
                break;
            case CoreWebView2DownloadState.Interrupted:
                var cancelled = _op.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled;
                _log.Warn($"Download interrupted: {name} ({_op.InterruptReason})");
                _setStatus(cancelled
                    ? $"Download cancelled: {name}"
                    : $"Download interrupted: {name} ({_op.InterruptReason})", null);
                break;
        }
        if (_op.State != CoreWebView2DownloadState.InProgress)
        {
            _op.BytesReceivedChanged -= OnProgress;
            _op.EstimatedEndTimeChanged -= OnProgress;
            _op.StateChanged -= OnStateChanged;
            _op = null;
            ActiveChanged?.Invoke(false);
        }
    }
}
