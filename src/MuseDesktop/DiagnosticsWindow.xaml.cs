using System.Windows;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The Diagnostics window (S8 #73/#74/#77/#78; S10 #89/#90/#93 made it
/// the full panel): a read-only report the main window's provider
/// composes on demand (runtime health, proxy, memory, startup
/// timings, file paths — and since S10 also architecture, Windows and
/// .NET versions, the active profile, the update channel, data sizes,
/// and the most recent errors, redacted). Refresh re-asks the
/// provider; Copy puts the report on the clipboard. The S10 actions
/// (export support bundle, open today's log, copy last error) are
/// optional delegates from the main window, shown only when supplied.
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private readonly Func<Task<string>> _reportProvider;
    private int _reportGeneration;
    private readonly Action? _onRollback;
    private readonly Action? _onExportBundle;
    private readonly Action? _onOpenLog;
    private readonly Func<string?>? _lastErrorProvider;

    /// <summary>
    /// <paramref name="rollbackLabel"/> + <paramref name="onRollback"/>
    /// (v0.13.0, S9 #84) show the "Reinstall previous version" button
    /// when the updater has a usable kept installer; the owner window
    /// owns the confirm + launch + exit. The S10 delegates
    /// (<paramref name="onExportBundle"/>, <paramref name="onOpenLog"/>,
    /// <paramref name="lastErrorProvider"/>) light up the support
    /// actions row; the main window supplies all three.
    /// </summary>
    public DiagnosticsWindow(Func<Task<string>> reportProvider,
        string? rollbackLabel = null, Action? onRollback = null,
        Action? onExportBundle = null, Action? onOpenLog = null,
        Func<string?>? lastErrorProvider = null)
    {
        _reportProvider = reportProvider;
        _onRollback = onRollback;
        _onExportBundle = onExportBundle;
        _onOpenLog = onOpenLog;
        _lastErrorProvider = lastErrorProvider;
        InitializeComponent();
        if (rollbackLabel is not null && onRollback is not null)
        {
            RollbackButton.Content = rollbackLabel;
            RollbackButton.Visibility = Visibility.Visible;
        }
        if (onExportBundle is not null)
            ExportBundleButton.Visibility = Visibility.Visible;
        if (onOpenLog is not null)
            OpenLogButton.Visibility = Visibility.Visible;
        if (lastErrorProvider is not null)
            CopyLastErrorButton.Visibility = Visibility.Visible;
        _ = RefreshReportAsync();
    }

    private void OnRollback(object sender, RoutedEventArgs e) => _onRollback?.Invoke();

    private void OnExportBundle(object sender, RoutedEventArgs e) => _onExportBundle?.Invoke();

    private void OnOpenLog(object sender, RoutedEventArgs e) => _onOpenLog?.Invoke();

    /// <summary>Copy just the newest error, redacted (S10 #93).</summary>
    private void OnCopyLastError(object sender, RoutedEventArgs e)
    {
        var last = _lastErrorProvider?.Invoke();
        if (last is null)
        {
            CopyNote.Text = "No errors in the current log — nothing to copy.";
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(last);
            CopyNote.Text = "Copied the most recent error (redacted).";
        }
        catch
        {
            CopyNote.Text = "Couldn't copy — the clipboard is busy.";
        }
    }

    private async Task RefreshReportAsync()
    {
        var generation = ++_reportGeneration;
        ReportText.Text = "Collecting diagnostics…";
        try
        {
            var report = await _reportProvider();
            if (generation == _reportGeneration)
                ReportText.Text = report;
        }
        catch (Exception ex)
        {
            if (generation == _reportGeneration)
                ReportText.Text = $"Diagnostics couldn't be collected ({ex.Message}). The log has the details.";
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshReportAsync();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(ReportText.Text);
            CopyNote.Text = "Copied.";
        }
        catch
        {
            CopyNote.Text = "Couldn't copy — the clipboard is busy.";
        }
    }
}
