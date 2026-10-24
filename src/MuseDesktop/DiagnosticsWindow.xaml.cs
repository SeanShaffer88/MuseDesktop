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

    public DiagnosticsWindow(Func<string> reportProvider)
    {
        _reportProvider = reportProvider;
        InitializeComponent();
        RefreshReport();
    }

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
