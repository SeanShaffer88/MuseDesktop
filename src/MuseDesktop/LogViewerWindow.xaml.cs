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
            AuditText.Text = $"Couldn't read the log file ({ex.Message}). It lives at {AppPaths.LogPath}.";
            FindingsText.Text = "";
            LogText.Text = "";
            return;
        }
        if (_logText.Length == 0)
        {
            AuditText.Text = "The log is empty right now — nothing has been written to it yet.";
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
            : string.Join("\n", findings.Take(30).Select(f => $"Line {f.LineNumber}: {f.Description}.")) +
              (findings.Count > 30 ? $"\n…and {findings.Count - 30} more." : "");
    }

    private void OnCopyRedacted(object sender, RoutedEventArgs e)
    {
        if (_logText.Length == 0)
            return;
        try
        {
            System.Windows.Clipboard.SetText(RedactionAudit.Redact(_logText));
            CopyNote.Text = "Copied — anything the check flagged was removed first.";
        }
        catch
        {
            CopyNote.Text = "Couldn't copy — the clipboard is busy. Try again.";
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();
}
