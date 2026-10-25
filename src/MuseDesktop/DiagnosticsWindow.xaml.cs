using System.Windows;

namespace MuseDesktop;

/// <summary>
/// The Diagnostics window (S8 #73/#74/#77/#78): a read-only report the
/// main window's provider composes on demand (runtime health, proxy,
/// memory, startup timings, file paths — all Core models, so the
/// fuller diagnostics work in S10 reuses them). Refresh re-asks the
/// provider; Copy puts the report on the clipboard.
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private readonly Func<string> _reportProvider;
    private readonly Action? _onRollback;

    /// <summary>
    /// <paramref name="rollbackLabel"/> + <paramref name="onRollback"/>
    /// (v0.13.0, S9 #84) show the "Reinstall previous version" button
    /// when the updater has a usable kept installer; the owner window
    /// owns the confirm + launch + exit.
    /// </summary>
    public DiagnosticsWindow(Func<string> reportProvider,
        string? rollbackLabel = null, Action? onRollback = null)
    {
        _reportProvider = reportProvider;
        _onRollback = onRollback;
        InitializeComponent();
        if (rollbackLabel is not null && onRollback is not null)
        {
            RollbackButton.Content = rollbackLabel;
            RollbackButton.Visibility = Visibility.Visible;
        }
        RefreshReport();
    }

    private void OnRollback(object sender, RoutedEventArgs e) => _onRollback?.Invoke();

    private void RefreshReport()
    {
        try
        {
            ReportText.Text = _reportProvider();
        }
        catch (Exception ex)
        {
            ReportText.Text = $"Diagnostics couldn't be collected ({ex.Message}). The log has the details.";
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshReport();

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
