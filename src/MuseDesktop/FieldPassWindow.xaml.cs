using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The guided field pass (Diagnostics): nine checks that only a
/// person at this PC can judge — layouts, the page health check,
/// the approval alert, conversation tools, downloads, the tray and
/// hotkey, the app lock, Settings search, and the update check.
///
/// The window is deliberately modeless: most checks are done IN the
/// main window (or with it closed to the tray), so this checklist
/// floats alongside instead of locking the app behind a dialog.
/// All state lives in Core's <see cref="FieldPassSession"/>; the
/// window renders the catalogue, records verdicts into the session,
/// and saves through <see cref="FieldPassStore"/> on every answer —
/// and notes save as they're typed (debounced), so closing the
/// window, or the app dying, loses nothing. The verdicts are the
/// user's own: the pass verifies nothing by itself, and only
/// verdict names (never note text) reach the log.
///
/// Open-feature buttons dispatch the main window's existing open
/// commands through the <paramref name="openStep"/> delegate — no
/// feature is re-implemented here. One exception, handled locally:
/// when this window was opened FROM Diagnostics, the page-health
/// check's "open Diagnostics" just reactivates that Diagnostics
/// window instead of stacking a second one.
/// </summary>
public partial class FieldPassWindow : Window
{
    private readonly string _storePath;
    private readonly string _appVersion;
    private readonly DiagnosticLog _log;
    private readonly Action<FieldPassStep> _openStep;
    private readonly FieldPassSession _session;
    private readonly DispatcherTimer _noteSaveTimer;

    private FieldPassStep _current;
    private bool _loading;

    public FieldPassWindow(string storePath, string appVersion, DiagnosticLog log,
        Action<FieldPassStep> openStep)
    {
        _storePath = storePath;
        _appVersion = appVersion;
        _log = log;
        _openStep = openStep;
        _session = FieldPassStore.Load(storePath);
        InitializeComponent();
        PassProgressBar.Maximum = FieldPassCatalog.Steps.Count;
        _noteSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _noteSaveTimer.Tick += (_, _) =>
        {
            _noteSaveTimer.Stop();
            CommitNote();
        };
        Closing += (_, _) =>
        {
            _noteSaveTimer.Stop();
            CommitNote();
        };
        // Resume where the last pass left off: the first unanswered
        // check, or the first check when the pass is complete.
        _current = FieldPass.NextUnanswered(_session) ?? FieldPassCatalog.Steps[0];
        RefreshStepList();
        ShowStep(_current);
    }

    // ─────────────────────────── rendering ───────────────────────────

    /// <summary>The step list's marker for a verdict: ✓ passed, ! problem, – skipped, ● current, ○ waiting.</summary>
    private string MarkerFor(FieldPassStep step)
    {
        return _session.VerdictFor(step.Id) switch
        {
            FieldPassVerdict.Pass => "\u2713",
            FieldPassVerdict.Problem => "!",
            FieldPassVerdict.Skipped => "\u2013",
            _ => ReferenceEquals(step, _current) ? "\u25cf" : "\u25cb",
        };
    }

    private void RefreshStepList()
    {
        _loading = true;
        StepsList.Items.Clear();
        foreach (var step in FieldPassCatalog.Steps)
            StepsList.Items.Add($"{MarkerFor(step)}  {step.Title}");
        StepsList.SelectedIndex = IndexOf(_current);
        _loading = false;
        RefreshProgress();
    }

    private void RefreshProgress()
    {
        var progress = FieldPass.Progress(_session);
        PassProgressBar.Value = progress.Answered;
        RemainingText.Text = Strings.Format("fieldpass.remaining", progress.Answered, progress.Remaining);
        ProgressText.Text = progress.IsComplete
            ? Strings.Format("fieldpass.progress_done",
                progress.Total, progress.Passed, progress.Problems, progress.Skipped)
            : Strings.Format("fieldpass.progress",
                IndexOf(_current) + 1, progress.Total, progress.Passed,
                progress.Problems, progress.Skipped, progress.Remaining);
    }

    private void ShowStep(FieldPassStep step)
    {
        _current = step;
        _loading = true;
        StepTitleText.Text = Strings.Format("fieldpass.check_title", IndexOf(step) + 1, step.Title);
        InstructionsText.Text = step.Instructions;
        SuccessText.Text = step.SuccessLine;
        OpenFeatureButton.Visibility = step.OpenTarget == FieldPassOpenTarget.None
            ? Visibility.Collapsed
            : Visibility.Visible;
        NotesBox.Text = _session.EntryFor(step.Id)?.Note ?? "";
        _loading = false;
        StatusText.Text = _session.VerdictFor(step.Id) switch
        {
            FieldPassVerdict.Pass => Strings.Get("fieldpass.status_passed"),
            FieldPassVerdict.Problem => Strings.Get("fieldpass.status_problem"),
            FieldPassVerdict.Skipped => Strings.Get("fieldpass.status_skipped"),
            _ => Strings.Get("fieldpass.status_none"),
        };
        RefreshProgress();
    }

    private static int IndexOf(FieldPassStep step)
    {
        for (var i = 0; i < FieldPassCatalog.Steps.Count; i++)
            if (FieldPassCatalog.Steps[i].Id == step.Id)
                return i;
        return 0;
    }

    // ─────────────────────────── interaction ───────────────────────────

    private void OnStepSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || StepsList.SelectedIndex < 0)
            return;
        CommitNote();
        ShowStep(FieldPassCatalog.Steps[StepsList.SelectedIndex]);
        RefreshStepList();
    }

    private void OnNotesChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;
        // Autosave the note as a draft (debounced) — the user's
        // words are kept even without a verdict, like a draft.
        _noteSaveTimer.Stop();
        _noteSaveTimer.Start();
    }

    /// <summary>Save the notes box's text against the current step (idempotent; errors never take the window down).</summary>
    private void CommitNote()
    {
        try
        {
            var before = _session.EntryFor(_current.Id)?.Note ?? "";
            if (!string.Equals(before, NotesBox.Text.Trim(), StringComparison.Ordinal))
            {
                _session.SetNote(_current.Id, NotesBox.Text);
                FieldPassStore.Save(_storePath, _session);
            }
        }
        catch (Exception ex) { _log.Warn($"Field pass note save failed: {ex.Message}"); }
    }

    private void OnPass(object sender, RoutedEventArgs e) => RecordVerdict(FieldPassVerdict.Pass);

    private void OnProblem(object sender, RoutedEventArgs e) => RecordVerdict(FieldPassVerdict.Problem);

    private void OnSkip(object sender, RoutedEventArgs e) => RecordVerdict(FieldPassVerdict.Skipped);

    private void RecordVerdict(FieldPassVerdict verdict)
    {
        _noteSaveTimer.Stop();
        try
        {
            _session.Record(_current.Id, verdict, NotesBox.Text, DateTimeOffset.UtcNow);
            FieldPassStore.Save(_storePath, _session);
        }
        catch (Exception ex) { _log.Warn($"Field pass save failed: {ex.Message}"); }
        // Verdict names only — the note is the user's own words and
        // is never logged.
        _log.Info($"Field pass: {_current.Id} = {verdict}");
        // Advance to the next unanswered check; stay put when the
        // pass is complete (the header says so).
        var next = FieldPass.NextUnanswered(_session);
        ShowStep(next ?? _current);
        RefreshStepList();
    }

    private void OnOpenFeature(object sender, RoutedEventArgs e)
    {
        if (_current.OpenTarget == FieldPassOpenTarget.Diagnostics && Owner is DiagnosticsWindow)
        {
            // Launched from Diagnostics: it's already open behind
            // this window — bring it forward instead of stacking a
            // second Diagnostics on top of the first.
            Owner.Activate();
            return;
        }
        _openStep(_current);
    }

    private void OnCopySummary(object sender, RoutedEventArgs e)
    {
        CommitNote();
        try
        {
            System.Windows.Clipboard.SetText(FieldPassSummary.Render(_session, _appVersion, DateTimeOffset.Now));
            CopyNote.Text = Strings.Get("ui.copied");
        }
        catch
        {
            CopyNote.Text = Strings.Get("ui.couldn_t_copy_the_clipboard_is_busy");
        }
    }

    private void OnStartOver(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(this,
            Strings.Get("fieldpass.reset_body"),
            Strings.Get("field_pass"), MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
            return;
        _noteSaveTimer.Stop();
        _session.Reset();
        try { FieldPassStore.Save(_storePath, _session); }
        catch (Exception ex) { _log.Warn($"Field pass reset save failed: {ex.Message}"); }
        _log.Info("Field pass: started over");
        ShowStep(FieldPassCatalog.Steps[0]);
        RefreshStepList();
    }
}
