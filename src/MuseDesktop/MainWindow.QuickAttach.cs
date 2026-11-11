using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>Attachment discovery lifecycle, separate from the main window coordinator.</summary>
public partial class MainWindow
{
    private CancellationTokenSource? _quickAttachScan;
    private int _quickAttachGeneration;
    private bool _quickAttachDisposed;

    private void CancelQuickAttachRefresh()
    {
        _quickAttachDisposed = true;
        _quickAttachGeneration++;
        _quickAttachScan?.Cancel();
        _quickAttachScan?.Dispose();
        _quickAttachScan = null;
    }

    private async void RefreshQuickAttachStrip()
    {
        if (!_quickAttachSetupDone || _quickAttachDisposed) return;
        _quickAttachScan?.Cancel();
        _quickAttachScan?.Dispose();
        _quickAttachScan = new CancellationTokenSource();
        var token = _quickAttachScan.Token;
        var generation = ++_quickAttachGeneration;
        if (_isLocked || !_settings.QuickAttachStripEnabled)
        {
            QuickAttachChips.Children.Clear();
            _quickAttachHasContent = false;
            UpdateQuickAttachStripVisibility();
            return;
        }
        // Clipboard access stays on STA. Directory enumeration does not.
        var hasImage = ClipboardHasImage();
        var folders = RecentFiles.DefaultSourceFolders(_settings.QuickAttachIncludeScreenshots);
        var limit = _settings.QuickAttachMaxFiles;
        try
        {
            var files = await Task.Run(() => RecentFiles.Collect(folders, limit, token), token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_quickAttachDisposed || token.IsCancellationRequested || generation != _quickAttachGeneration ||
                    _isLocked || !_settings.QuickAttachStripEnabled) return;
                QuickAttachChips.Children.Clear();
                _quickAttachHasContent = hasImage || files.Count > 0;
                if (hasImage) QuickAttachChips.Children.Add(BuildPastedImageChip());
                foreach (var entry in files) QuickAttachChips.Children.Add(BuildFileChip(entry));
                UpdateQuickAttachStripVisibility();
            });
        }
        catch (OperationCanceledException) { /* Superseded scan or window closing. */ }
        catch (Exception ex)
        {
            // File names, paths and clipboard data never enter this log.
            _log.Warn($"Attachment discovery failed: {ex.GetType().Name}");
        }
    }
}
