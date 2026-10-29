using System.Diagnostics;
using System.IO;
using System.Windows;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The in-app log viewer (v0.11.0, S7 #66): a read-only tail of the
/// diagnostic log plus the redaction audit run over exactly what's on
/// screen. The audit is the honest part — the app's logging rules say
/// addresses are host+path only and no tokens are ever written, and
/// this window PROVES it on the real file (or says plainly that it
/// found something, by line number). "Copy redacted log" copies the
/// text with flagged spans removed, so a log can be pasted into a bug
/// report without hand-scrubbing.
/// </summary>
public partial class LogViewerWindow : Window
{
    private string _logText = "";

    public LogViewerWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        CopyNote.Text = "";
        try
        {
            _logText = File.Exists(AppPaths.LogPath)
                ? File.ReadAllText(AppPaths.LogPath)
                : "";
        }
        catch (Exception ex)
        {
            _logText = "";
            AuditText.Text = Strings.Format("ui.couldnt_read_log_file", ex.Message, AppPaths.LogPath);
            FindingsText.Text = "";
            LogText.Text = "";
            return;
        }
        if (_logText.Length == 0)
        {
            AuditText.Text = Strings.Get("ui.the_log_is_empty_right_now_nothing_has");
            FindingsText.Text = "";
            LogText.Text = "";
            return;
        }
        LogText.Text = _logText;
        LogText.ScrollToEnd();
        var lineCount = _logText.Split('\n').Length;
        var findings = RedactionAudit.Scan(_logText);
        AuditText.Text = RedactionAudit.Summarize(findings, lineCount);
        FindingsText.Text = findings.Count == 0
            ? ""
            : string.Join("\n", findings.Take(30).Select(f => Strings.Format("log.line_finding", f.LineNumber, f.Description))) +
              (findings.Count > 30 ? "\n" + Strings.Format("log.and_more", findings.Count - 30) : "");
    }

    private void OnCopyRedacted(object sender, RoutedEventArgs e)
    {
        if (_logText.Length == 0)
            return;
        try
        {
            System.Windows.Clipboard.SetText(RedactionAudit.Redact(_logText));
            CopyNote.Text = Strings.Get("ui.copied_anything_the_check_flagged_was");
        }
        catch
        {
            CopyNote.Text = Strings.Get("ui.couldn_t_copy_the_clipboard_is_busy_try");
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    /// <summary>Copy just the newest error, redacted (S10 #93) — the one line a bug report usually needs.</summary>
    private void OnCopyLastError(object sender, RoutedEventArgs e)
    {
        var last = LogReading.LastError(_logText);
        if (last is null)
        {
            CopyNote.Text = Strings.Get("ui.no_errors_in_the_current_log_nothing_to");
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(last);
            CopyNote.Text = Strings.Get("ui.copied_the_most_recent_error_redacted");
        }
        catch
        {
            CopyNote.Text = Strings.Get("ui.couldn_t_copy_the_clipboard_is_busy_try");
        }
    }

    /// <summary>Open the log file itself in the default text editor (S10 #93).</summary>
    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(AppPaths.LogPath))
            {
                CopyNote.Text = Strings.Get("ui.there_s_no_log_file_yet_it_appears_once");
                return;
            }
            Process.Start(new ProcessStartInfo(AppPaths.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CopyNote.Text = Strings.Format("ui.couldnt_open_log_file", ex.Message, AppPaths.LogPath);
        }
    }
}
