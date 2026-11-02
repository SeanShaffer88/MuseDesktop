using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── Field pass: catalogue, session, store, summary ─────────────────────

/// <summary>
/// The guided field pass (Diagnostics): the catalogue is pinned at
/// the nine checks the feature ships (a tenth check is a deliberate
/// product decision, not an accident), the session records only the
/// user's own verdicts, notes are drafts that never count as
/// answers, the store round-trips and survives corruption by
/// preserving the damaged file, and the summary says exactly what
/// the user answered — nothing is verified by the pass itself.
/// </summary>
public class FieldPassCatalogTests
{
    [Fact]
    public void Catalogue_HasNineUniqueFullyWordedSteps()
    {
        var steps = FieldPassCatalog.Steps;
        Assert.Equal(9, steps.Count);
        Assert.Equal(9, steps.Select(s => s.Id).Distinct().Count());
        Assert.All(steps, step =>
        {
            Assert.False(string.IsNullOrWhiteSpace(step.Title));
            Assert.False(string.IsNullOrWhiteSpace(step.Instructions));
            Assert.False(string.IsNullOrWhiteSpace(step.SuccessLine));
        });
    }

    [Fact]
    public void Catalogue_OpenTargets_MatchTheBrief()
    {
        Assert.Equal(FieldPassOpenTarget.Settings, FieldPassCatalog.ById("layouts")!.OpenTarget);
        Assert.Equal(FieldPassOpenTarget.Diagnostics, FieldPassCatalog.ById("page-health")!.OpenTarget);
        Assert.Equal(FieldPassOpenTarget.None, FieldPassCatalog.ById("approval-alert")!.OpenTarget);
        Assert.Equal(FieldPassOpenTarget.None, FieldPassCatalog.ById("conversation-tools")!.OpenTarget);
        Assert.Equal(FieldPassOpenTarget.Downloads, FieldPassCatalog.ById("downloads")!.OpenTarget);
        Assert.Equal(FieldPassOpenTarget.None, FieldPassCatalog.ById("tray-hotkey")!.OpenTarget);
        Assert.Equal(FieldPassOpenTarget.None, FieldPassCatalog.ById("app-lock")!.OpenTarget);
        Assert.Equal(FieldPassOpenTarget.Settings, FieldPassCatalog.ById("settings-search")!.OpenTarget);
        Assert.Equal(FieldPassOpenTarget.Settings, FieldPassCatalog.ById("update-check")!.OpenTarget);
        Assert.Null(FieldPassCatalog.ById("nope"));
        Assert.Null(FieldPassCatalog.ById(null));
    }
}

public class FieldPassSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Record_CountsVerdictsAndStampsTheTime()
    {
        var session = new FieldPassSession();
        session.Record("layouts", FieldPassVerdict.Pass, "  rail is nice  ", Now);
        session.Record("downloads", FieldPassVerdict.Problem, "stalled at 98%", Now);
        session.Record("app-lock", FieldPassVerdict.Skipped, null, Now);

        var progress = FieldPass.Progress(session);
        Assert.Equal(9, progress.Total);
        Assert.Equal(1, progress.Passed);
        Assert.Equal(1, progress.Problems);
        Assert.Equal(1, progress.Skipped);
        Assert.Equal(3, progress.Answered);
        Assert.Equal(6, progress.Remaining);
        Assert.False(progress.IsComplete);

        var entry = session.EntryFor("layouts");
        Assert.NotNull(entry);
        Assert.Equal("rail is nice", entry!.Note);   // notes are trimmed
        Assert.Equal(Now, entry.AnsweredUtc);
    }

    [Fact]
    public void Record_UnknownStepOrUnanswered_Throws()
    {
        var session = new FieldPassSession();
        Assert.Throws<ArgumentException>(() =>
            session.Record("nope", FieldPassVerdict.Pass, null, Now));
        Assert.Throws<ArgumentException>(() =>
            session.Record("layouts", FieldPassVerdict.Unanswered, null, Now));
        Assert.Throws<ArgumentException>(() => session.SetNote("nope", "hi"));
    }

    [Fact]
    public void NoteOnly_IsADraft_NotAnAnswer()
    {
        var session = new FieldPassSession();
        session.SetNote("layouts", "half-way through");
        Assert.Equal(FieldPassVerdict.Unanswered, session.VerdictFor("layouts"));
        Assert.Equal(0, FieldPass.Progress(session).Answered);
        Assert.Equal("layouts", FieldPass.NextUnanswered(session)!.Id);

        // Clearing the draft on an unanswered step removes the entry.
        session.SetNote("layouts", "  ");
        Assert.Null(session.EntryFor("layouts"));

        // A note on an answered step sticks to the verdict.
        session.Record("layouts", FieldPassVerdict.Pass, null, Now);
        session.SetNote("layouts", "added later");
        Assert.Equal("added later", session.EntryFor("layouts")!.Note);
        Assert.Equal(FieldPassVerdict.Pass, session.VerdictFor("layouts"));
    }

    [Fact]
    public void NextUnanswered_WalksTheCatalogue_ThenNull()
    {
        var session = new FieldPassSession();
        foreach (var step in FieldPassCatalog.Steps)
        {
            Assert.Equal(step.Id, FieldPass.NextUnanswered(session)!.Id);
            session.Record(step.Id, FieldPassVerdict.Pass, null, Now);
        }
        Assert.Null(FieldPass.NextUnanswered(session));
        Assert.True(FieldPass.Progress(session).IsComplete);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var session = new FieldPassSession();
        session.Record("layouts", FieldPassVerdict.Pass, "ok", Now);
        session.SetNote("downloads", "draft");
        session.Reset();
        Assert.Empty(session.Entries);
        Assert.Equal(0, FieldPass.Progress(session).Answered);
        Assert.Equal("layouts", FieldPass.NextUnanswered(session)!.Id);
    }
}

public class FieldPassStoreTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MuseDesktop-fieldpass-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Store_RoundTripsVerdictsNotesAndTimes()
    {
        using var temp = new TempDir();
        var path = System.IO.Path.Combine(temp.Path, "field-pass.json");
        var session = new FieldPassSession();
        session.Record("layouts", FieldPassVerdict.Pass, "rail works", Now);
        session.Record("approval-alert", FieldPassVerdict.Problem, "no toast", Now);
        session.SetNote("downloads", "draft note");
        FieldPassStore.Save(path, session);

        var loaded = FieldPassStore.Load(path);
        Assert.Equal(FieldPassVerdict.Pass, loaded.VerdictFor("layouts"));
        Assert.Equal("rail works", loaded.EntryFor("layouts")!.Note);
        Assert.Equal(Now, loaded.EntryFor("layouts")!.AnsweredUtc);
        Assert.Equal(FieldPassVerdict.Problem, loaded.VerdictFor("approval-alert"));
        Assert.Equal("draft note", loaded.EntryFor("downloads")!.Note);
        Assert.Equal(2, FieldPass.Progress(loaded).Answered);
    }

    [Fact]
    public void Store_MissingFile_IsAFreshPass()
    {
        using var temp = new TempDir();
        var loaded = FieldPassStore.Load(System.IO.Path.Combine(temp.Path, "nope.json"));
        Assert.Empty(loaded.Entries);
    }

    [Fact]
    public void Store_CorruptFile_IsPreserved_AndAFreshPassStarts()
    {
        using var temp = new TempDir();
        var path = System.IO.Path.Combine(temp.Path, "field-pass.json");
        File.WriteAllText(path, "{ this is not json");
        var loaded = FieldPassStore.Load(path);
        Assert.Empty(loaded.Entries);
        var preserved = Directory.GetFiles(temp.Path, "field-pass.json.corrupt-*");
        Assert.Single(preserved);
        Assert.Equal("{ this is not json", File.ReadAllText(preserved[0]));
    }

    [Fact]
    public void Store_SanitizesUnknownStepsAndVerdictWords()
    {
        using var temp = new TempDir();
        var path = System.IO.Path.Combine(temp.Path, "field-pass.json");
        File.WriteAllText(path, """
            { "entries": {
                "layouts": { "verdict": "Pass", "note": "ok", "answeredUtc": null },
                "ghost-step": { "verdict": "Pass", "note": "boo", "answeredUtc": null },
                "downloads": { "verdict": "SortOf", "note": "typo in a hand edit", "answeredUtc": null }
            } }
            """);
        var loaded = FieldPassStore.Load(path);
        Assert.Equal(FieldPassVerdict.Pass, loaded.VerdictFor("layouts"));
        Assert.Null(loaded.EntryFor("ghost-step"));
        // An unrecognised verdict word degrades to Unanswered and
        // keeps its note — one typo never costs the whole pass.
        Assert.Equal(FieldPassVerdict.Unanswered, loaded.VerdictFor("downloads"));
        Assert.Equal("typo in a hand edit", loaded.EntryFor("downloads")!.Note);
    }

    [Fact]
    public void AppPaths_FieldPassFile_LivesBesideTheSettings()
    {
        Assert.Equal("field-pass.json", System.IO.Path.GetFileName(AppPaths.FieldPassPath));
        Assert.Equal(AppPaths.RootDir, System.IO.Path.GetDirectoryName(AppPaths.FieldPassPath));
    }
}

public class FieldPassSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(-6));

    [Fact]
    public void Summary_NamesVersionDateCountsVerdictsAndNotes()
    {
        var session = new FieldPassSession();
        session.Record("layouts", FieldPassVerdict.Pass, "Left Rail kept", Now);
        session.Record("page-health", FieldPassVerdict.Problem, "drafts not recognised", Now);
        session.Record("app-lock", FieldPassVerdict.Skipped, null, Now);
        var text = FieldPassSummary.Render(session, "2.0.0", Now);

        Assert.Contains("MuseDesktop version: 2.0.0", text);
        Assert.Contains("2026-11-02", text);
        Assert.Contains("3 of 9 answered", text);
        Assert.Contains("1 passed", text);
        Assert.Contains("1 with a problem", text);
        Assert.Contains("1 skipped", text);
        Assert.Contains("6 not answered", text);
        Assert.Contains("1. Layouts \u2014 Passed", text);
        Assert.Contains("Note: Left Rail kept", text);
        Assert.Contains("2. Page health \u2014 Problem reported", text);
        Assert.Contains("Note: drafts not recognised", text);
        Assert.Contains("7. App lock \u2014 Skipped", text);
        Assert.Contains("9. Update check \u2014 Not answered", text);
    }

    [Fact]
    public void Summary_FreshPass_ListsEverythingUnanswered()
    {
        var text = FieldPassSummary.Render(new FieldPassSession(), "2.0.0", Now);
        Assert.Contains("0 of 9 answered", text);
        foreach (var step in FieldPassCatalog.Steps)
            Assert.Contains(step.Title, text);
    }
}
