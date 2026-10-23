using System.Diagnostics;
using System.IO;
using System.Windows;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The finished-downloads list (v0.4.0): what Muse downloaded, where it
/// went, and one-click Open / Show in folder. Backed by
/// <see cref="DownloadHistoryStore"/>; the window reloads the list each
/// time it opens so it always reflects the latest completions.
/// </summary>
public partial class DownloadsWindow : Window
{
    private readonly DiagnosticLog _log;

    /// <summary>One display row: the record plus its pre-formatted detail line.</summary>
    public sealed class Row
    {
        public required DownloadRecord Record { get; init; }
        public string FileName => Record.FileName;
        public string Detail =>
            $"{DownloadText.FormatBytes(Record.SizeBytes)} · {Record.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {Record.FullPath}";
    }

    public DownloadsWindow(DiagnosticLog log)
    {
        _log = log;
        InitializeComponent();
        Refresh();
    }

    private Row? Selected => DownloadsList.SelectedItem as Row;

    private void Refresh()
    {
        var records = DownloadHistoryStore.Load(AppPaths.DownloadsHistoryPath);
        DownloadsList.ItemsSource = records.Select(r => new Row { Record = r }).ToList();
        if (records.Count == 0)
            NoteText.Text = "Nothing here yet — files you download from Muse will appear in this list.";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var enabled = Selected is not null;
        OpenButton.IsEnabled = enabled;
        FolderButton.IsEnabled = enabled;
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

    private void OnClear(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(this,
            "Clear the download history?\n\nThis only removes the list — the downloaded files themselves stay where they are.",
            "Clear download history", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;
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
}
