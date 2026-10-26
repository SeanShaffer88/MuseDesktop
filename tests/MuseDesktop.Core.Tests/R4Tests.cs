using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>
/// R4 (v1.4.0, items 131–140): downloads round 2 — sort rules with a
/// previewable plan and an undo journal, scheduling (bandwidth governor
/// + quiet hours), the duplicate guard, batch link capture, the speed
/// history, verify-after-download, history notes, archiving, bounded
/// auto-retry, and soft per-profile quotas. The invariants that matter
/// most are the safety ones: a plan never overwrites, a rule can never
/// point outside the downloads folder, undo never overwrites either,
/// the governor never touches a user pause, non-network failures never
/// auto-retry, and a quota never blocks or deletes.
/// </summary>
public class R4Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(-5));

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "md-r4-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string Make(string name, string content = "x")
        {
            var full = System.IO.Path.Combine(Path, name);
            File.WriteAllText(full, content);
            return full;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* temp cleanup is best-effort */ }
        }
    }

    private static DownloadRecord Completed(string fullPath, long size = 100, DateTimeOffset? when = null) => new()
    {
        FileName = System.IO.Path.GetFileName(fullPath),
        FullPath = fullPath,
        SizeBytes = size,
        CompletedUtc = when ?? Now,
        State = DownloadRecordState.Completed,
    };

    // ──────────────────────── #131 categories + rules ────────────────────────

    [Theory]
    [InlineData("photo.JPG", DownloadFileCategory.Images)]
    [InlineData("report.pdf", DownloadFileCategory.Documents)]
    [InlineData("backup.zip", DownloadFileCategory.Archives)]
    [InlineData("song.flac", DownloadFileCategory.Audio)]
    [InlineData("clip.mkv", DownloadFileCategory.Video)]
    [InlineData("setup.msi", DownloadFileCategory.Programs)]
    [InlineData("mystery.xyz", DownloadFileCategory.Other)]
    [InlineData("noextension", DownloadFileCategory.Other)]
    public void FileTypes_CategoryFor_KnownAndUnknown(string name, DownloadFileCategory expected) =>
        Assert.Equal(expected, DownloadFileTypes.CategoryFor(name));

    [Theory]
    [InlineData("*invoice*", "MyInvoice2026.pdf", true)]
    [InlineData("*invoice*", "receipt.pdf", false)]
    [InlineData("report?.pdf", "report1.pdf", true)]
    [InlineData("report?.pdf", "report12.pdf", false)]
    [InlineData("*.PDF", "a.pdf", true)]
    [InlineData("*", "anything.txt", true)]
    [InlineData("exact.txt", "exact.txt", true)]
    [InlineData("exact.txt", "exact2.txt", false)]
    public void SortRule_GlobMatch(string pattern, string value, bool expected) =>
        Assert.Equal(expected, DownloadSortRule.GlobMatch(pattern, value));

    [Fact]
    public void SortRule_Matches_ByExtensionOrPattern()
    {
        var rule = new DownloadSortRule
        {
            Extensions = new List<string> { ".pdf" },
            NamePattern = "*invoice*",
            TargetSubfolder = "Docs",
        };
        Assert.True(rule.Matches("anything.pdf"));
        Assert.True(rule.Matches("invoice-9.txt"));
        Assert.False(rule.Matches("photo.png"));
    }

    [Fact]
    public void SortRule_Sanitized_RejectsEscapingSubfolders()
    {
        Assert.Null(new DownloadSortRule { Extensions = new List<string> { ".pdf" }, TargetSubfolder = @"..\evil" }.Sanitized());
        Assert.Null(new DownloadSortRule { Extensions = new List<string> { ".pdf" }, TargetSubfolder = @"C:\abs" }.Sanitized());
        Assert.Null(new DownloadSortRule { Extensions = new List<string> { ".pdf" }, TargetSubfolder = "/abs" }.Sanitized());
        Assert.Null(new DownloadSortRule { Extensions = new List<string> { ".pdf" }, TargetSubfolder = "" }.Sanitized());
        Assert.Null(new DownloadSortRule { TargetSubfolder = "Docs" }.Sanitized()); // no matcher at all

        var ok = new DownloadSortRule
        {
            Extensions = new List<string> { "PDF", ".Docx", ".bad ext" },
            TargetSubfolder = "a/b",
        }.Sanitized();
        Assert.NotNull(ok);
        Assert.Equal(@"a\b", ok!.TargetSubfolder);
        Assert.Equal(new List<string> { ".docx" }, ok.Extensions); // "PDF" lacks the dot, ".bad ext" has a space
    }

    [Fact]
    public void SortRule_ParseLines_ExtensionsPatternsAndErrors()
    {
        var rules = DownloadSortRule.ParseLines(
            "# comment\n" +
            "pdf, docx = Documents\n" +
            "*invoice* -> Finance\n" +
            "\n" +
            "no separator here\n" +
            "zip = ..\\escape\n",
            out var errors);
        Assert.Equal(2, rules.Count);
        Assert.Equal(new List<string> { ".pdf", ".docx" }, rules[0].Extensions);
        Assert.Equal("Documents", rules[0].TargetSubfolder);
        Assert.Equal("*invoice*", rules[1].NamePattern);
        Assert.Equal("Finance", rules[1].TargetSubfolder);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void SortRule_FormatLines_RoundTripsThroughParse()
    {
        var original = new List<DownloadSortRule>
        {
            new() { Extensions = new List<string> { ".pdf", ".docx" }, TargetSubfolder = "Documents" },
            new() { NamePattern = "*invoice*", TargetSubfolder = "Finance" },
        };
        var parsed = DownloadSortRule.ParseLines(DownloadSortRule.FormatLines(original), out var errors);
        Assert.Empty(errors);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(original[0].Extensions, parsed[0].Extensions);
        Assert.Equal(original[1].NamePattern, parsed[1].NamePattern);
    }

    // ──────────────────────── #131 planner ────────────────────────

    [Fact]
    public void SortPlanner_CustomRuleBeatsCategory_AndCategoriesCatchTheRest()
    {
        using var dir = new TempDir();
        var pdf = dir.Make("a.pdf");
        var png = dir.Make("b.png");
        var records = new[] { Completed(pdf), Completed(png) };
        var custom = new List<DownloadSortRule>
        {
            new() { Name = "Papers", Extensions = new List<string> { ".pdf" }, TargetSubfolder = "Papers" },
        };
        var moves = DownloadSortPlanner.Plan(records, dir.Path, custom, useCategoryRules: true, File.Exists);
        Assert.Equal(2, moves.Count);
        Assert.Equal(System.IO.Path.Combine(dir.Path, "Papers", "a.pdf"), moves.Single(m => m.SourcePath == pdf).DestinationPath);
        Assert.Equal("Papers", moves.Single(m => m.SourcePath == pdf).RuleName);
        Assert.Equal(System.IO.Path.Combine(dir.Path, "Images", "b.png"), moves.Single(m => m.SourcePath == png).DestinationPath);
    }

    [Fact]
    public void SortPlanner_SkipsFailed_Subfoldered_AndUnmatched()
    {
        using var dir = new TempDir();
        var sub = Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, "Sub")).FullName;
        var inSub = System.IO.Path.Combine(sub, "c.pdf");
        File.WriteAllText(inSub, "x");
        var failed = Completed(dir.Make("d.pdf"));
        failed.State = DownloadRecordState.Failed;
        var unmatched = Completed(dir.Make("e.unknownext"));
        var moves = DownloadSortPlanner.Plan(
            new[] { Completed(inSub), failed, unmatched },
            dir.Path,
            Array.Empty<DownloadSortRule>(),
            useCategoryRules: false,
            File.Exists);
        Assert.Empty(moves);
    }

    [Fact]
    public void SortPlanner_NeverOverwrites_DiskOrPlannedCollisionsRename()
    {
        using var dir = new TempDir();
        var destDir = Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, "Documents")).FullName;
        File.WriteAllText(System.IO.Path.Combine(destDir, "a.pdf"), "old");
        var first = dir.Make("a.pdf");
        // Two records claiming the same source name in the folder (the
        // second is the same path — plans must still not collide with
        // each other).
        var records = new[] { Completed(first), Completed(first) };
        var moves = DownloadSortPlanner.Plan(records, dir.Path, Array.Empty<DownloadSortRule>(), true, File.Exists);
        Assert.Equal(System.IO.Path.Combine(dir.Path, "Documents", "a (1).pdf"), moves[0].DestinationPath);
        Assert.Equal(System.IO.Path.Combine(dir.Path, "Documents", "a (2).pdf"), moves[1].DestinationPath);
    }

    [Fact]
    public void SortPlanner_ArchiveHook_BeatsRulesForOldFilesOnly()
    {
        using var dir = new TempDir();
        var old = Completed(dir.Make("old.pdf"), when: Now.AddDays(-40));
        var fresh = Completed(dir.Make("new.pdf"), when: Now.AddDays(-1));
        var archiveRoot = System.IO.Path.Combine(dir.Path, "Archive");
        var moves = DownloadSortPlanner.Plan(
            new[] { old, fresh }, dir.Path, Array.Empty<DownloadSortRule>(),
            useCategoryRules: true, File.Exists,
            new DownloadArchiveOptions(archiveRoot, AfterDays: 30), Now);
        var oldMove = moves.Single(m => m.SourcePath == old.FullPath);
        Assert.Equal("Archive", oldMove.RuleName);
        Assert.StartsWith(archiveRoot, oldMove.DestinationPath);
        var freshMove = moves.Single(m => m.SourcePath == fresh.FullPath);
        Assert.Equal("Documents", freshMove.RuleName);
    }

    // ──────────────────────── #138 archive planner ────────────────────────

    [Fact]
    public void ArchivePlanner_DatedFolder_SkipsAlreadyArchived()
    {
        using var dir = new TempDir();
        var when = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var loose = Completed(dir.Make("loose.pdf"), when: when);
        var archiveRoot = System.IO.Path.Combine(dir.Path, "Archive");
        var already = Completed(System.IO.Path.Combine(archiveRoot, "2026-10-19", "done.pdf"), when: when);
        var moves = DownloadSortPlanner.PlanArchive(new[] { loose, already }, archiveRoot, _ => false);
        var move = Assert.Single(moves);
        Assert.Equal(
            System.IO.Path.Combine(archiveRoot, when.ToLocalTime().ToString("yyyy-MM-dd"), "loose.pdf"),
            move.DestinationPath);
        Assert.Equal("Archive", move.RuleName);
    }

    // ──────────────────────── #131 journal + undo ────────────────────────

    [Fact]
    public void SortJournal_RoundTrips_AppendLastRemove()
    {
        using var dir = new TempDir();
        var journal = System.IO.Path.Combine(dir.Path, "journal.json");
        Assert.Null(DownloadSortJournalStore.LastBatch(journal));
        DownloadSortJournalStore.Append(journal, new DownloadSortBatch
        {
            AppliedUtc = Now,
            Moves = new List<DownloadMove> { new("a", "b", "Docs") },
        });
        DownloadSortJournalStore.Append(journal, new DownloadSortBatch
        {
            AppliedUtc = Now,
            Moves = new List<DownloadMove> { new("c", "d", "Images") },
        });
        var batches = DownloadSortJournalStore.Load(journal);
        Assert.Equal(2, batches.Count);
        Assert.Equal("Images", DownloadSortJournalStore.LastBatch(journal)!.Moves[0].RuleName);
        Assert.True(DownloadSortJournalStore.RemoveLast(journal));
        Assert.Equal("Docs", DownloadSortJournalStore.LastBatch(journal)!.Moves[0].RuleName);
        Assert.True(DownloadSortJournalStore.RemoveLast(journal));
        Assert.False(DownloadSortJournalStore.RemoveLast(journal));
    }

    [Fact]
    public void SortPlanner_PlanUndo_Reverses_SkipsVanished_NeverOverwrites()
    {
        using var dir = new TempDir();
        var sortedA = System.IO.Path.Combine(dir.Path, "Documents", "a.pdf");
        var sortedB = System.IO.Path.Combine(dir.Path, "Documents", "b.pdf");
        var gone = System.IO.Path.Combine(dir.Path, "Documents", "gone.pdf");
        Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, "Documents"));
        File.WriteAllText(sortedA, "a");
        File.WriteAllText(sortedB, "b");
        // Something new now sits at a's original home — undo renames around it.
        var blocker = dir.Make("a.pdf", "new");
        var batch = new DownloadSortBatch
        {
            Moves = new List<DownloadMove>
            {
                new(blocker.Replace("a.pdf", "a-original.pdf"), sortedA, "Documents"),
                new(System.IO.Path.Combine(dir.Path, "b.pdf"), sortedB, "Documents"),
                new(System.IO.Path.Combine(dir.Path, "gone.pdf"), gone, "Documents"),
            },
        };
        // Fix the first move's source to a's true original path.
        batch.Moves[0].SourcePath = blocker;
        var undo = DownloadSortPlanner.PlanUndo(batch, File.Exists);
        Assert.Equal(2, undo.Count); // the vanished file is skipped
        Assert.Equal(System.IO.Path.Combine(dir.Path, "a (1).pdf"),
            undo.Single(m => m.SourcePath == sortedA).DestinationPath);
        Assert.Equal(System.IO.Path.Combine(dir.Path, "b.pdf"),
            undo.Single(m => m.SourcePath == sortedB).DestinationPath);
    }

    // ──────────────────────── history store: notes (#137), verdicts (#136), path rewrites ────────────────────────

    [Fact]
    public void HistoryStore_SetNote_TrimsCapsAndClears()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "downloads.json");
        var record = Completed(System.IO.Path.Combine(dir.Path, "a.pdf"));
        DownloadHistoryStore.Add(path, record);

        Assert.True(DownloadHistoryStore.SetNote(path, record, "  keep this one  "));
        Assert.Equal("keep this one", DownloadHistoryStore.Load(path)[0].Note);

        Assert.True(DownloadHistoryStore.SetNote(path, record, new string('n', 250)));
        Assert.Equal(200, DownloadHistoryStore.Load(path)[0].Note!.Length);

        Assert.True(DownloadHistoryStore.SetNote(path, record, null));
        Assert.Null(DownloadHistoryStore.Load(path)[0].Note);

        var other = Completed(System.IO.Path.Combine(dir.Path, "nope.pdf"));
        Assert.False(DownloadHistoryStore.SetNote(path, other, "x"));
    }

    [Fact]
    public void HistoryStore_SetHashVerified_RoundTrips()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "downloads.json");
        var record = Completed(System.IO.Path.Combine(dir.Path, "a.pdf"));
        DownloadHistoryStore.Add(path, record);
        Assert.Null(DownloadHistoryStore.Load(path)[0].HashVerified);
        Assert.True(DownloadHistoryStore.SetHashVerified(path, record, false));
        Assert.Equal(false, DownloadHistoryStore.Load(path)[0].HashVerified);
        Assert.True(DownloadHistoryStore.SetHashVerified(path, record, true));
        Assert.Equal(true, DownloadHistoryStore.Load(path)[0].HashVerified);
    }

    [Fact]
    public void HistoryStore_RewritePaths_UpdatesMatchingEntriesOnly()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "downloads.json");
        var a = Completed(System.IO.Path.Combine(dir.Path, "a.pdf"));
        var b = Completed(System.IO.Path.Combine(dir.Path, "b.pdf"));
        DownloadHistoryStore.Add(path, a);
        DownloadHistoryStore.Add(path, b);
        var moved = System.IO.Path.Combine(dir.Path, "Documents", "a.pdf");
        var changed = DownloadHistoryStore.RewritePaths(path,
            new Dictionary<string, string> { [a.FullPath.ToUpperInvariant()] = moved });
        Assert.Equal(1, changed);
        var loaded = DownloadHistoryStore.Load(path);
        Assert.Equal(moved, loaded.Single(r => r.FileName == "a.pdf").FullPath);
        Assert.Equal(b.FullPath, loaded.Single(r => r.FileName == "b.pdf").FullPath);
    }

    // ──────────────────────── #132 schedule ────────────────────────

    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 10, 26, hour, minute, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void Schedule_DisabledOrOutsideWindow_Allows()
    {
        Assert.True(DownloadSchedule.Evaluate(false, 9 * 60, 17 * 60, 100_000_000, At(12, 0), 500_000_000).Allowed);
        Assert.True(DownloadSchedule.Evaluate(true, 9 * 60, 17 * 60, 100_000_000, At(7, 30), 500_000_000).Allowed);
        Assert.True(DownloadSchedule.Evaluate(true, 9 * 60, 17 * 60, 100_000_000, At(17, 0), 500_000_000).Allowed);
    }

    [Fact]
    public void Schedule_InsideWindow_SmallAndUnknownPass_LargeWaits()
    {
        const long mb100 = 100L * 1024 * 1024;
        Assert.True(DownloadSchedule.Evaluate(true, 9 * 60, 17 * 60, mb100, At(12, 0), mb100 - 1).Allowed);
        Assert.True(DownloadSchedule.Evaluate(true, 9 * 60, 17 * 60, mb100, At(12, 0), null).Allowed);
        var held = DownloadSchedule.Evaluate(true, 9 * 60, 17 * 60, mb100, At(12, 0), mb100);
        Assert.False(held.Allowed);
        Assert.NotNull(held.Reason);
        Assert.Equal(At(17, 0), held.NextChance);
    }

    [Fact]
    public void Schedule_ZeroThreshold_HoldsEverything_WrapWindowEndsTomorrow()
    {
        var held = DownloadSchedule.Evaluate(true, 22 * 60, 6 * 60, 0, At(23, 30), null);
        Assert.False(held.Allowed);
        Assert.Equal(At(6, 0).AddDays(1), held.NextChance);
        // A same-day window's small-hour side: 05:00 is inside 22:00–06:00.
        var heldEarly = DownloadSchedule.Evaluate(true, 22 * 60, 6 * 60, 0, At(5, 0), 1);
        Assert.False(heldEarly.Allowed);
        Assert.Equal(At(6, 0), heldEarly.NextChance);
    }

    [Theory]
    [InlineData("9:00", 540)]
    [InlineData("09:05", 545)]
    [InlineData("17:30", 1050)]
    [InlineData("0:00", 0)]
    public void TimeOfDayText_Parses(string text, int expected) =>
        Assert.True(TimeOfDayText.TryParseMinutes(text, out var minutes) && minutes == expected);

    [Theory]
    [InlineData("24:00")]
    [InlineData("9:60")]
    [InlineData("9")]
    [InlineData("")]
    [InlineData("nine")]
    [InlineData(null)]
    public void TimeOfDayText_Rejects(string? text) =>
        Assert.False(TimeOfDayText.TryParseMinutes(text, out _));

    [Fact]
    public void TimeOfDayText_Formats() => Assert.Equal("09:05", TimeOfDayText.Format(545));

    // ──────────────────────── #132 bandwidth governor ────────────────────────

    private static ActiveDownload Transfer(string id, double speed, DownloadActivityState state = DownloadActivityState.InProgress) => new()
    {
        Id = id,
        FileName = id + ".bin",
        SpeedBytesPerSecond = speed,
        State = state,
    };

    [Fact]
    public void Governor_OverLimit_PausesNewestOnly()
    {
        var governor = new BandwidthGovernor();
        var items = new List<ActiveDownload> { Transfer("a", 600), Transfer("b", 600) };
        var (pause, resume) = governor.Evaluate(items, 1000);
        Assert.Equal(new[] { "b" }, pause);
        Assert.Empty(resume);
        Assert.Equal(new[] { "b" }, governor.GovernorPausedIds);
    }

    [Fact]
    public void Governor_Headroom_ResumesItsOwnOldest_NeverTheUsersPause()
    {
        var governor = new BandwidthGovernor();
        var items = new List<ActiveDownload> { Transfer("a", 600), Transfer("b", 600) };
        governor.Evaluate(items, 1000); // pauses b
        items[1].State = DownloadActivityState.Paused;
        items[1].SpeedBytesPerSecond = 0;
        items.Add(Transfer("user", 0, DownloadActivityState.Paused)); // paused by hand
        var (pause, resume) = governor.Evaluate(items, 1000);
        Assert.Empty(pause);
        Assert.Equal(new[] { "b" }, resume);
        Assert.Empty(governor.GovernorPausedIds);
    }

    [Fact]
    public void Governor_NoLimit_ResumesEverythingItPaused()
    {
        var governor = new BandwidthGovernor();
        var items = new List<ActiveDownload> { Transfer("a", 600), Transfer("b", 600) };
        governor.Evaluate(items, 1000);
        items[1].State = DownloadActivityState.Paused;
        var (_, resume) = governor.Evaluate(items, 0);
        Assert.Equal(new[] { "b" }, resume);
    }

    [Fact]
    public void Governor_BetweenBands_DoesNothing_NoFlapping()
    {
        var governor = new BandwidthGovernor();
        var items = new List<ActiveDownload> { Transfer("a", 600), Transfer("b", 600) };
        governor.Evaluate(items, 1000); // pauses b (total 1200 > 1000)
        items[1].State = DownloadActivityState.Paused;
        items[1].SpeedBytesPerSecond = 0;
        items[0].SpeedBytesPerSecond = 900; // under the limit, over the 85% resume band
        var (pause, resume) = governor.Evaluate(items, 1000);
        Assert.Empty(pause);
        Assert.Empty(resume);
        Assert.Equal(new[] { "b" }, governor.GovernorPausedIds);
    }

    // ──────────────────────── #133 duplicate guard ────────────────────────

    [Fact]
    public void DuplicateGuard_NameAndSizeSemantics()
    {
        // FileName is set explicitly (the app is Windows; on this Linux
        // host Path.GetFileName would not split a C:\ path).
        DownloadRecord Rec(string name, long size) => new()
        {
            FileName = name,
            FullPath = @"C:\Downloads\" + name,
            SizeBytes = size,
            CompletedUtc = Now,
            State = DownloadRecordState.Completed,
        };
        var history = new List<DownloadRecord> { Rec("report.pdf", 500), Rec("failed.pdf", 700) };
        history[1].State = DownloadRecordState.Failed;

        Assert.NotNull(DuplicateDownloadGuard.FindInHistory("REPORT.pdf", 500, history));
        Assert.Null(DuplicateDownloadGuard.FindInHistory("report.pdf", 501, history)); // size differs
        Assert.NotNull(DuplicateDownloadGuard.FindInHistory("report.pdf", null, history)); // size unknown
        Assert.Null(DuplicateDownloadGuard.FindInHistory("failed.pdf", 700, history)); // failures don't count
        Assert.Null(DuplicateDownloadGuard.FindInHistory("other.pdf", 500, history));
        Assert.Null(DuplicateDownloadGuard.FindInHistory("", 500, history));
    }

    [Fact]
    public void DuplicateGuard_SameContent_SizeGateThenHash()
    {
        using var dir = new TempDir();
        var a = dir.Make("a.bin", "identical payload");
        var copy = dir.Make("b.bin", "identical payload");
        var sameSize = dir.Make("c.bin", "different payload"); // same length, different bytes
        var shorter = dir.Make("d.bin", "short");
        Assert.True(DuplicateDownloadGuard.SameContent(a, copy));
        Assert.False(DuplicateDownloadGuard.SameContent(a, sameSize));
        Assert.False(DuplicateDownloadGuard.SameContent(a, shorter));
        Assert.False(DuplicateDownloadGuard.SameContent(a, System.IO.Path.Combine(dir.Path, "missing.bin")));
    }

    // ──────────────────────── #136 hash verification ────────────────────────

    [Theory]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", true)]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", true)]
    [InlineData("ba78 16bf 8f01 cfea 4141 40de 5dae 2223 b003 61a3 9617 7a9c b410 ff61 f200 15ad", true)]
    [InlineData("ba7816bf", false)]
    [InlineData("zz7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ag", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Hash_TryNormalize(string? text, bool expected) =>
        Assert.Equal(expected, DownloadHash.TryNormalizeSha256(text, out _));

    [Fact]
    public void Hash_Verify_KnownVector_PassFailAndBadExpected()
    {
        using var dir = new TempDir();
        // SHA-256("abc") — the standard test vector.
        var file = dir.Make("abc.bin", "abc");
        const string abcHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        var pass = DownloadHash.Verify(file, abcHash);
        Assert.True(pass.ExpectedValid);
        Assert.True(pass.Matches);
        Assert.Equal(abcHash, pass.ActualSha256);

        var fail = DownloadHash.Verify(file, new string('0', 64));
        Assert.True(fail.ExpectedValid);
        Assert.False(fail.Matches);

        var bad = DownloadHash.Verify(file, "not a hash");
        Assert.False(bad.ExpectedValid);
        Assert.NotNull(bad.Error);

        var missing = DownloadHash.Verify(System.IO.Path.Combine(dir.Path, "nope.bin"), abcHash);
        Assert.True(missing.ExpectedValid);
        Assert.False(missing.Matches);
        Assert.NotNull(missing.Error);
    }

    // ──────────────────────── #134 batch capture ────────────────────────

    [Fact]
    public void BatchParser_ValidatesDedupesAndNames()
    {
        var result = BatchLinkParser.Parse(
            "# shopping list\n" +
            "https://example.com/files/Report%202026.pdf\n" +
            "https://example.com/files/Report%202026.pdf/\n" + // duplicate (trailing slash)
            "HTTPS://EXAMPLE.COM/manual.zip\n" +
            "ftp://example.com/nope.zip\n" +
            "not a link\n" +
            "/relative/path.pdf\n" +
            "https://example.com/\n" +
            "\n");
        Assert.Equal(3, result.Links.Count);
        Assert.Equal("Report 2026.pdf", result.Links[0].FileName);
        Assert.Equal("manual.zip", result.Links[1].FileName);
        Assert.Equal("download", result.Links[2].FileName); // no file segment
        Assert.Equal(3, result.Rejected.Count);
    }

    [Fact]
    public void BatchParser_CapsAtMaxLinks()
    {
        var lines = Enumerable.Range(1, BatchLinkParser.MaxLinks + 5)
            .Select(i => $"https://example.com/f{i}.zip");
        var result = BatchLinkParser.Parse(string.Join("\n", lines));
        Assert.Equal(BatchLinkParser.MaxLinks, result.Links.Count);
        Assert.Equal(5, result.Rejected.Count);
    }

    [Theory]
    [InlineData(0, 5, 4)]
    [InlineData(3, 5, 1)]
    [InlineData(4, 5, 0)]
    [InlineData(9, 5, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(2, 1, 1)]
    public void Concurrency_StartableNow(int active, int queued, int expected) =>
        Assert.Equal(expected, DownloadConcurrency.StartableNow(active, queued));

    // ──────────────────────── #135 speed history ────────────────────────

    [Fact]
    public void SpeedHistory_BoundedAndPeaks()
    {
        var history = new DownloadSpeedHistory();
        for (var i = 0; i < DownloadSpeedHistory.MaxSamples + 50; i++)
            history.Add(Now.AddSeconds(i), i);
        Assert.Equal(DownloadSpeedHistory.MaxSamples, history.Count);
        Assert.Equal(DownloadSpeedHistory.MaxSamples + 49, history.Peak);
        history.Add(Now, double.NaN);
        Assert.Equal(0, history.Samples[^1].BytesPerSecond);
    }

    [Fact]
    public void SpeedHistory_NormalizedPoints_ShapeAndDownsampling()
    {
        var history = new DownloadSpeedHistory();
        Assert.Empty(history.NormalizedPoints(10));
        history.Add(Now, 0);
        history.Add(Now.AddSeconds(1), 100);
        history.Add(Now.AddSeconds(2), 50);
        var points = history.NormalizedPoints(10);
        Assert.Equal(3, points.Count);
        Assert.Equal((0d, 0d), points[0]);
        Assert.Equal((0.5, 1d), points[1]);
        Assert.Equal((1d, 0.5), points[2]);

        // Downsampling averages buckets: 4 samples → 2 points.
        var dense = new DownloadSpeedHistory();
        foreach (var v in new[] { 0d, 100d, 100d, 0d })
            dense.Add(Now, v);
        var two = dense.NormalizedPoints(2);
        Assert.Equal(2, two.Count);
        Assert.Equal(0.5, two[0].Y); // (0+100)/2 over peak 100
        Assert.Equal(0.5, two[1].Y); // (100+0)/2 over peak 100
    }

    // ──────────────────────── #139 auto-retry policy ────────────────────────

    [Theory]
    [InlineData("NetworkFailed", true)]
    [InlineData("NetworkTimeout", true)]
    [InlineData("NetworkDisconnected", true)]
    [InlineData("ServerFailed", true)]
    [InlineData("ServerContentLengthMismatch", true)]
    [InlineData("UserCanceled", false)]
    [InlineData("UserPaused", false)]
    [InlineData("FileNoSpace", false)]
    [InlineData("FileAccessDenied", false)]
    [InlineData(null, false)]
    [InlineData("SomethingElse", false)]
    public void RetryPolicy_RetryableReasons(string? reason, bool expected) =>
        Assert.Equal(expected, DownloadRetryPolicy.IsRetryableReason(reason));

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 30)]
    [InlineData(3, 90)]
    [InlineData(4, 270)]
    [InlineData(5, 300)] // capped at 5 minutes
    [InlineData(0, 10)]  // attempts are 1-based; junk reads as the first
    public void RetryPolicy_BackoffCurve(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), DownloadRetryPolicy.DelayForAttempt(attempt));

    [Fact]
    public void RetryPolicy_BoundsAndSwitch()
    {
        Assert.Equal(1, DownloadRetryPolicy.SanitizeMaxAttempts(0));
        Assert.Equal(5, DownloadRetryPolicy.SanitizeMaxAttempts(99));
        Assert.Equal(3, DownloadRetryPolicy.SanitizeMaxAttempts(3));

        Assert.True(DownloadRetryPolicy.ShouldRetry(true, "NetworkFailed", 0, 3));
        Assert.True(DownloadRetryPolicy.ShouldRetry(true, "NetworkFailed", 2, 3));
        Assert.False(DownloadRetryPolicy.ShouldRetry(true, "NetworkFailed", 3, 3)); // bound hit
        Assert.False(DownloadRetryPolicy.ShouldRetry(false, "NetworkFailed", 0, 3)); // switched off
        Assert.False(DownloadRetryPolicy.ShouldRetry(true, "FileNoSpace", 0, 3)); // wrong reason
    }

    // ──────────────────────── #140 quotas ────────────────────────

    [Theory]
    [InlineData(0, 1000, DownloadQuotaLevel.Ok)]
    [InlineData(500, 0, DownloadQuotaLevel.NoCap)]
    [InlineData(100, 1000, DownloadQuotaLevel.Ok)]
    [InlineData(799, 1000, DownloadQuotaLevel.Ok)]
    [InlineData(800, 1000, DownloadQuotaLevel.Warning)] // exactly 80%
    [InlineData(1000, 1000, DownloadQuotaLevel.Full)]
    [InlineData(1001, 1000, DownloadQuotaLevel.Over)]
    public void Quota_Evaluate_Levels(long used, long quota, DownloadQuotaLevel expected) =>
        Assert.Equal(expected, DownloadQuota.Evaluate(used, quota).Level);

    [Fact]
    public void Quota_ForFolder_MeasuresAndDescribes()
    {
        using var dir = new TempDir();
        dir.Make("a.bin", new string('x', 500));
        var sub = Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, "Documents")).FullName;
        File.WriteAllText(System.IO.Path.Combine(sub, "b.bin"), new string('y', 400));
        var status = DownloadQuota.ForFolder(dir.Path, 1000);
        Assert.Equal(900, status.UsedBytes);
        Assert.Equal(DownloadQuotaLevel.Warning, status.Level);
        Assert.NotNull(DownloadQuota.Describe(status));
        Assert.Null(DownloadQuota.Describe(DownloadQuota.Evaluate(10, 1000)));
        Assert.Null(DownloadQuota.Describe(DownloadQuota.Evaluate(10, 0)));
        // A folder that doesn't exist is "nothing used", never an error.
        Assert.Equal(0, DownloadQuota.ForFolder(System.IO.Path.Combine(dir.Path, "nope"), 1000).UsedBytes);
    }

    // ──────────────────────── settings plumbing (R4 fields) ────────────────────────

    [Fact]
    public void Settings_R4Fields_SanitizeRepairs()
    {
        var settings = new AppSettings
        {
            DownloadQuietStartMinutes = -5,
            DownloadQuietEndMinutes = 9999,
            DownloadBandwidthLimitKBps = -1,
            DownloadQuietLargeFileMB = 999_999,
            DownloadArchiveAfterDays = 999_999,
            DownloadArchiveFolder = "   ",
            DownloadAutoRetryMaxAttempts = 99,
            DownloadSortRules = new List<DownloadSortRule>
            {
                new() { Extensions = new List<string> { ".pdf" }, TargetSubfolder = "Docs" },
                new() { Extensions = new List<string> { ".pdf" }, TargetSubfolder = @"..\evil" },
            },
        }.Sanitized();
        Assert.Equal(9 * 60, settings.DownloadQuietStartMinutes);
        Assert.Equal(17 * 60, settings.DownloadQuietEndMinutes);
        Assert.Equal(0, settings.DownloadBandwidthLimitKBps);
        Assert.Equal(102_400, settings.DownloadQuietLargeFileMB);
        Assert.Equal(3650, settings.DownloadArchiveAfterDays);
        Assert.Null(settings.DownloadArchiveFolder);
        Assert.Equal(5, settings.DownloadAutoRetryMaxAttempts);
        Assert.Single(settings.DownloadSortRules);
    }

    [Fact]
    public void Settings_R4Fields_RoundTripThroughJson()
    {
        var settings = new AppSettings
        {
            AutoSortDownloads = true,
            DownloadSortUseCategories = false,
            DownloadBandwidthLimitKBps = 512,
            DownloadQuietHoursEnabled = true,
            DownloadQuietStartMinutes = 8 * 60,
            DownloadQuietEndMinutes = 18 * 60,
            DownloadQuietLargeFileMB = 42,
            DownloadDuplicateGuard = false,
            DownloadArchiveFolder = @"D:\Archive",
            DownloadArchiveAfterDays = 14,
            DownloadAutoRetryEnabled = false,
            DownloadAutoRetryMaxAttempts = 2,
            DownloadSortRules = new List<DownloadSortRule>
            {
                new() { Name = "Papers", Extensions = new List<string> { ".pdf" }, TargetSubfolder = "Papers" },
            },
        };
        settings.Profiles[0].DownloadQuotaBytes = 123_456;
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.Sanitized();
        Assert.True(back.AutoSortDownloads);
        Assert.False(back.DownloadSortUseCategories);
        Assert.Equal(512, back.DownloadBandwidthLimitKBps);
        Assert.True(back.DownloadQuietHoursEnabled);
        Assert.Equal(8 * 60, back.DownloadQuietStartMinutes);
        Assert.Equal(42, back.DownloadQuietLargeFileMB);
        Assert.False(back.DownloadDuplicateGuard);
        Assert.Equal(@"D:\Archive", back.DownloadArchiveFolder);
        Assert.Equal(14, back.DownloadArchiveAfterDays);
        Assert.False(back.DownloadAutoRetryEnabled);
        Assert.Equal(2, back.DownloadAutoRetryMaxAttempts);
        Assert.Single(back.DownloadSortRules);
        Assert.Equal(123_456, back.Profiles[0].DownloadQuotaBytes);
    }

    [Fact]
    public void Settings_ProfileQuota_NegativeReadsAsNoCap()
    {
        var settings = new AppSettings();
        settings.Profiles[0].DownloadQuotaBytes = -10;
        Assert.Equal(0, settings.Sanitized().Profiles[0].DownloadQuotaBytes);
    }
}
