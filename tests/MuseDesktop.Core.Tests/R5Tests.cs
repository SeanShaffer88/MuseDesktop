using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>
/// R5 (v1.5.0, items 141–150): focus &amp; productivity — the focus
/// timer with its gentle break suggestion, the Pomodoro session
/// timer, the reading list, window groups-lite, full-text history
/// search, the weekly digest, command scheduling, quick notes (+ its
/// global hotkey), tiling presets, and the stats dashboard. The
/// invariants that matter most: nothing here can hold a full page
/// URL (routes only), the break reminder is dismissible state and
/// never a block, paused session time never counts as work, a
/// schedule can only name a real built-in action, and every store is
/// local-only counts/text in its own file.
/// </summary>
public class R5Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(-5));

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "md-r5-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* temp cleanup is best-effort */ }
        }
    }

    // ─────────────────────── timer text ───────────────────────

    [Fact]
    public void TimerText_FormatsCountdownsAndTotals()
    {
        Assert.Equal("00:00", TimerText.Format(TimeSpan.Zero));
        Assert.Equal("12:34", TimerText.Format(new TimeSpan(0, 12, 34)));
        Assert.Equal("1:02:34", TimerText.Format(new TimeSpan(1, 2, 34)));
        Assert.Equal("00:00", TimerText.Format(TimeSpan.FromMinutes(-5)));
        Assert.Equal("45m", TimerText.FormatMinutes(45));
        Assert.Equal("2h 05m", TimerText.FormatMinutes(125));
        Assert.Equal("0m", TimerText.FormatMinutes(-3));
    }

    // ─────────────────────── focus timer (#141) ───────────────────────

    [Fact]
    public void FocusTimer_CompletesOnce_AndBanksFullBlock()
    {
        var timer = new FocusTimer();
        timer.Start(Now, 25);
        Assert.Equal(FocusTimerState.Focusing, timer.State);
        Assert.Equal(TimeSpan.FromMinutes(10), timer.Remaining(Now.AddMinutes(15)));

        var early = timer.Tick(Now.AddMinutes(24));
        Assert.False(early.FocusJustCompleted);

        var done = timer.Tick(Now.AddMinutes(25));
        Assert.True(done.FocusJustCompleted);
        Assert.Equal(FocusTimerState.BreakSuggested, timer.State);
        Assert.True(timer.ReminderPending);
        Assert.Equal(25, timer.FocusMinutesBanked);

        // The completion is reported exactly once.
        var again = timer.Tick(Now.AddMinutes(26));
        Assert.False(again.FocusJustCompleted);
        Assert.Equal(25, timer.FocusMinutesBanked);
    }

    [Fact]
    public void FocusTimer_ReminderIsDismissible_AndBreakRuns()
    {
        var timer = new FocusTimer();
        timer.Start(Now, 25);
        timer.Tick(Now.AddMinutes(25));

        // Dismiss: state only — nothing else happens, nothing blocks.
        timer.DismissReminder();
        Assert.Equal(FocusTimerState.Idle, timer.State);
        Assert.False(timer.ReminderPending);

        // Take the break instead, on a second run.
        timer.Start(Now, 25);
        timer.Tick(Now.AddMinutes(25));
        Assert.True(timer.StartBreak(Now.AddMinutes(25), 5));
        Assert.Equal(FocusTimerState.OnBreak, timer.State);
        var breakDone = timer.Tick(Now.AddMinutes(30));
        Assert.True(breakDone.BreakJustCompleted);
        Assert.Equal(FocusTimerState.Idle, timer.State);
    }

    [Fact]
    public void FocusTimer_StopEarly_BanksWholeMinutesOnly()
    {
        var timer = new FocusTimer();
        timer.Start(Now, 25);
        var banked = timer.Stop(Now.AddMinutes(12).AddSeconds(59));
        Assert.Equal(12, banked);
        Assert.Equal(12, timer.FocusMinutesBanked);
        Assert.Equal(FocusTimerState.Idle, timer.State);
        Assert.Equal(0, timer.Stop(Now)); // stopping idle banks nothing
    }

    [Fact]
    public void FocusTimer_ClampsLengths_AndStatusLines()
    {
        var timer = new FocusTimer();
        timer.Start(Now, 9999);
        Assert.Equal(TimeSpan.FromMinutes(240), timer.Remaining(Now));
        Assert.Contains("Focus", timer.StatusLine(Now));
        timer.Tick(Now.AddMinutes(240));
        Assert.Contains("break", timer.StatusLine(Now.AddMinutes(240)));
        Assert.False(timer.StartBreak(Now, 5) is false); // valid from BreakSuggested
    }

    // ─────────────────────── session timer (#142) ───────────────────────

    private static SessionTimer NewSession() => new(SessionTimerConfig.Default);

    [Fact]
    public void SessionTimer_CyclesWorkBreakLongBreak()
    {
        var timer = NewSession();
        var t = Now;
        timer.Start(t);
        Assert.Equal(SessionPhase.Work, timer.Phase);
        Assert.Contains("cycle 1 of 4", timer.StatusLine(t));

        // Four work blocks: short breaks after 1–3, long break after 4.
        for (var cycle = 1; cycle <= 4; cycle++)
        {
            var work = timer.Tick(t = t.AddMinutes(25));
            Assert.True(work.PhaseChanged);
            Assert.Equal(25, work.FocusMinutesBanked);
            Assert.Equal(
                cycle < 4 ? SessionPhase.ShortBreak : SessionPhase.LongBreak,
                work.EnteredPhase);
            var breakLength = cycle < 4 ? 5 : 15;
            var back = timer.Tick(t = t.AddMinutes(breakLength));
            Assert.True(back.PhaseChanged);
            Assert.Equal(SessionPhase.Work, back.EnteredPhase);
            Assert.Equal(0, back.FocusMinutesBanked);
        }
        Assert.Equal(4, timer.TotalWorkBlocks);
        Assert.Equal(100, timer.FocusMinutesBanked);
        Assert.Equal(0, timer.CycleInSet); // the long break reset the set
    }

    [Fact]
    public void SessionTimer_PauseFreezesCountdown_AndNeverCountsAsWork()
    {
        var timer = NewSession();
        timer.Start(Now);
        timer.Pause(Now.AddMinutes(10));
        Assert.True(timer.IsPaused);
        // An hour passes while paused: no boundary is crossed.
        var tick = timer.Tick(Now.AddMinutes(70));
        Assert.False(tick.PhaseChanged);
        Assert.Equal(TimeSpan.FromMinutes(15), timer.Remaining(Now.AddMinutes(70)));
        timer.Resume(Now.AddMinutes(70));
        // 15 real work minutes remain after the resume.
        var done = timer.Tick(Now.AddMinutes(85));
        Assert.True(done.PhaseChanged);
        Assert.Equal(SessionPhase.ShortBreak, done.EnteredPhase);
    }

    [Fact]
    public void SessionTimer_StopMidWork_BanksElapsed_AndSleepDoesNotFastForward()
    {
        var timer = NewSession();
        timer.Start(Now);
        var banked = timer.Stop(Now.AddMinutes(17).AddSeconds(30));
        Assert.Equal(17, banked);
        Assert.False(timer.IsRunning);

        // Asleep through several phases: exactly ONE boundary is crossed.
        timer.Start(Now);
        var tick = timer.Tick(Now.AddHours(5));
        Assert.True(tick.PhaseChanged);
        Assert.Equal(SessionPhase.ShortBreak, tick.EnteredPhase);
        Assert.Equal(TimeSpan.FromMinutes(5), timer.Remaining(Now.AddHours(5)));
    }

    [Fact]
    public void SessionTimer_ConfigSanitizes()
    {
        var config = new SessionTimerConfig(0, 9999, -5, 99).Sanitized();
        Assert.Equal(1, config.WorkMinutes);
        Assert.Equal(120, config.BreakMinutes);
        Assert.Equal(1, config.LongBreakMinutes);
        Assert.Equal(12, config.CyclesBeforeLongBreak);
    }

    // ─────────────────────── tiling presets (#149) ───────────────────────

    [Fact]
    public void TilingPresets_ThirdsTileTheAreaExactly()
    {
        var area = new ScreenRect(0, 0, 1000, 600);
        var left = WindowTilingPresets.Rect(area, TilePreset.ThirdLeft);
        var center = WindowTilingPresets.Rect(area, TilePreset.ThirdCenter);
        var right = WindowTilingPresets.Rect(area, TilePreset.ThirdRight);
        Assert.Equal(0, left.Left);
        Assert.Equal(left.Left + left.Width, center.Left);
        Assert.Equal(center.Left + center.Width, right.Left);
        Assert.Equal(1000, right.Left + right.Width);
        Assert.All(new[] { left, center, right }, r => Assert.Equal(600, r.Height));

        var twoLeft = WindowTilingPresets.Rect(area, TilePreset.TwoThirdsLeft);
        Assert.Equal(right.Left, twoLeft.Left + twoLeft.Width);
        var twoRight = WindowTilingPresets.Rect(area, TilePreset.TwoThirdsRight);
        Assert.Equal(left.Left + left.Width, twoRight.Left);
    }

    [Fact]
    public void TilingPresets_QuartersAndHalvesAgreeWithR1Geometry()
    {
        var area = new ScreenRect(10, 20, 999, 501);
        Assert.Equal(WindowTiling.Half(area, TileSide.Left),
            WindowTilingPresets.Rect(area, TilePreset.HalfLeft));
        Assert.Equal(WindowTiling.Half(area, TileSide.Right),
            WindowTilingPresets.Rect(area, TilePreset.HalfRight));
        var tl = WindowTilingPresets.Rect(area, TilePreset.QuarterTopLeft);
        var bl = WindowTilingPresets.Rect(area, TilePreset.QuarterBottomLeft);
        var tr = WindowTilingPresets.Rect(area, TilePreset.QuarterTopRight);
        Assert.Equal(area.Left, tl.Left);
        Assert.Equal(area.Top + tl.Height, bl.Top);
        Assert.Equal(area.Height, tl.Height + bl.Height);
        Assert.Equal(tl.Left + tl.Width, tr.Left);
        Assert.Equal(area.Width, tl.Width + tr.Width);
    }

    [Fact]
    public void TilingPresets_ParseAndTitles()
    {
        Assert.True(WindowTilingPresets.TryParse("QuarterTopLeft", out var preset));
        Assert.Equal(TilePreset.QuarterTopLeft, preset);
        Assert.False(WindowTilingPresets.TryParse("Diagonal", out _));
        Assert.False(WindowTilingPresets.TryParse(null, out _));
        Assert.All(WindowTilingPresets.All, p =>
            Assert.False(string.IsNullOrWhiteSpace(WindowTilingPresets.Title(p))));
    }

    // ─────────────────────── window groups-lite (#144) ───────────────────────

    [Fact]
    public void WindowGroups_AssignMovesBetweenGroups_AndPruneDropsTheDead()
    {
        var set = new WindowGroupSet();
        var a = set.Create("Deep work", new[] { "main", "w2" })!;
        var b = set.Create("Later")!;
        Assert.NotNull(a);
        Assert.True(set.Assign("w2", b.Id));
        Assert.Equal(b.Id, set.GroupOf("w2")!.Id);
        Assert.Equal(new[] { "main" }, set.LiveMembers(a.Id, new[] { "main", "w2" }));

        // "main" closes: the group that held only it prunes away.
        set.Prune(new[] { "w2" });
        Assert.Single(set.Groups);
        Assert.Equal(b.Id, set.Groups[0].Id);
        Assert.Null(set.GroupOf("main"));
    }

    [Fact]
    public void WindowGroups_NameRulesCapAndRename()
    {
        var set = new WindowGroupSet();
        Assert.Null(set.Create("   "));
        Assert.Null(set.Create(null));
        var group = set.Create("  My   Group  ")!;
        Assert.Equal("My Group", group.Name);
        Assert.True(set.Rename(group.Id, "Renamed"));
        Assert.Equal("Renamed", group.Name);
        Assert.False(set.Rename(group.Id, " "));
        Assert.False(set.Rename("nope", "Name"));
        Assert.True(set.Delete(group.Id));
        Assert.Empty(set.Groups);

        for (var i = 0; i < WindowGroupSet.MaxGroups; i++)
            Assert.NotNull(set.Create($"G{i}", new[] { $"w{i}" }));
        Assert.Null(set.Create("One too many", new[] { "wx" }));
    }

    // ─────────────────────── reading list (#143) ───────────────────────

    [Fact]
    public void ReadingList_StoresRoutesOnly_AndRefusesForeignUrls()
    {
        var lists = new ReadingLists();
        var entry = lists.Add("p1", "A chat", "https://muse.ai/chat/abc?token=secret", Now);
        Assert.NotNull(entry);
        Assert.Equal("/chat/abc", entry!.Route); // query (and its token) never stored
        Assert.Null(lists.Add("p1", "Other site", "https://example.com/page", Now));
        Assert.Null(lists.Add("p1", "Sign-in", "https://accounts.google.com/login", Now));
        Assert.Single(lists.EntriesFor("p1"));
        Assert.Empty(lists.EntriesFor("p2"));
    }

    [Fact]
    public void ReadingList_ReAddRefreshes_ReadStateAndRemovalWork()
    {
        var lists = new ReadingLists();
        var first = lists.Add("p1", "Old title", "https://muse.ai/projects", Now)!;
        lists.Add("p1", "Second", "https://muse.ai/chat/x", Now);
        Assert.True(lists.SetRead("p1", first.Id, true));

        var again = lists.Add("p1", "New title", "https://muse.ai/projects", Now.AddHours(1));
        Assert.Equal(first.Id, again!.Id);       // same entry, not a dupe
        Assert.Equal("New title", again.Title);
        Assert.True(again.IsRead);               // read state survives a re-save
        Assert.Equal(again.Id, lists.EntriesFor("p1")[0].Id); // and it leads the list
        Assert.Equal(1, lists.UnreadCount("p1"));

        Assert.True(lists.Remove("p1", first.Id));
        Assert.Single(lists.EntriesFor("p1"));
        Assert.False(lists.Remove("p1", first.Id));
    }

    [Fact]
    public void ReadingList_PrunesReadFirst_AndSanitizeDropsBadRoutes()
    {
        var lists = new ReadingLists();
        for (var i = 0; i < ReadingLists.MaxPerProfile + 5; i++)
        {
            var e = lists.Add("p1", $"Page {i}", $"https://muse.ai/chat/{i}", Now.AddMinutes(i))!;
            if (i < 5)
                lists.SetRead("p1", e.Id, true);
        }
        var entries = lists.EntriesFor("p1");
        Assert.Equal(ReadingLists.MaxPerProfile, entries.Count);
        // The five read ones were the first to go.
        Assert.DoesNotContain(entries, e => e.Title == "Page 0");

        lists.ByProfile["p1"].Add(new ReadingListEntry
        {
            Id = "bad",
            Title = "Forged",
            Route = "https://evil.example/steal",
            AddedUtc = Now,
        });
        lists.Sanitize();
        Assert.DoesNotContain(lists.EntriesFor("p1"), e => e.Id == "bad");
    }

    [Fact]
    public void ReadingList_StoreRoundTrips()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "reading-list.json");
        var lists = new ReadingLists();
        lists.Add("p1", "Kept", "https://muse.ai/chat/kept", Now);
        ReadingListStore.Save(path, lists);
        var back = ReadingListStore.Load(path);
        var entry = Assert.Single(back.EntriesFor("p1"));
        Assert.Equal("Kept", entry.Title);
        Assert.Equal("/chat/kept", entry.Route);

        File.WriteAllText(path, "{not json");
        Assert.Empty(ReadingListStore.Load(path).EntriesFor("p1"));
    }

    [Fact]
    public void ReadingList_PaletteProvider_OffersRoutesAsPayloads()
    {
        var lists = new ReadingLists();
        var a = lists.Add("p1", "Alpha", "https://muse.ai/chat/a", Now)!;
        lists.Add("p1", "Beta", "https://muse.ai/chat/b", Now.AddMinutes(1));
        lists.SetRead("p1", a.Id, true);
        var commands = new ReadingListPaletteProvider(lists.EntriesFor("p1")).GetCommands().ToList();
        Assert.Equal(2, commands.Count);
        Assert.All(commands, c => Assert.Equal(PaletteCommandSource.ReadingList, c.Source));
        Assert.All(commands, c => Assert.StartsWith("/", c.Payload));
        // Unread leads.
        Assert.Equal("Beta", commands[0].Title);
        Assert.Contains("unread", commands[0].Detail);
    }

    // ─────────────────────── history search (#145) ───────────────────────

    private static List<NavigationEntry> SampleHistory() => new()
    {
        new() { Url = "https://muse.ai/chat/1", Title = "Trip planning" },
        new() { Url = "https://muse.ai/projects", Title = "Projects" },
        new() { Url = "https://muse.ai/chat/2", Title = "Trip budget spreadsheet" },
        new() { Url = "https://muse.ai/chat/1", Title = "Trip planning (updated)" },
    };

    [Fact]
    public void HistorySearch_MatchesEveryTermAcrossTitleAndPath()
    {
        var hits = HistorySearch.Search("trip", SampleHistory());
        Assert.Equal(2, hits.Count);
        // The duplicate URL collapsed to its most recent visit/title.
        Assert.Contains(hits, h => h.DisplayTitle == "Trip planning (updated)");

        var both = HistorySearch.Search("trip budget", SampleHistory());
        var hit = Assert.Single(both);
        Assert.Equal("/chat/2", hit.Path);

        // A path-only term still finds the page.
        Assert.Single(HistorySearch.Search("projects", SampleHistory()));
        Assert.Empty(HistorySearch.Search("zanzibar", SampleHistory()));
    }

    [Fact]
    public void HistorySearch_BlankQueryIsMostRecentFirst_AndPathsNeverCarryQueries()
    {
        var hits = HistorySearch.Search("", SampleHistory());
        Assert.Equal(3, hits.Count);
        Assert.Equal("Trip planning (updated)", hits[0].DisplayTitle);

        var withQuery = new List<NavigationEntry>
        {
            new() { Url = "https://muse.ai/chat/9?token=abc", Title = "" },
        };
        var hit = Assert.Single(HistorySearch.Search(null, withQuery));
        Assert.Equal("/chat/9", hit.Path);
        Assert.Equal("/chat/9", hit.DisplayTitle); // title falls back to the path
    }

    [Fact]
    public void HistorySearch_PaletteProvider_DedupesAndCarriesUrlInProcessOnly()
    {
        var commands = new HistoryPaletteProvider(SampleHistory()).GetCommands().ToList();
        Assert.Equal(3, commands.Count);
        Assert.All(commands, c => Assert.Equal(PaletteCommandSource.History, c.Source));
        Assert.Contains(commands, c => c.Payload == "https://muse.ai/chat/1");
        // The action history refuses these commands outright (R1's rule).
        var history = new ActionHistory();
        Assert.All(commands, c => Assert.False(history.Record(c)));
    }

    // ─────────────────────── quick notes (#148) ───────────────────────

    [Fact]
    public void Notes_AddCapsDeletesAndCounts()
    {
        var notes = new ProfileNotes();
        Assert.Null(notes.Add("p1", "   ", Now));
        var note = notes.Add("p1", "  Remember the thing  ", Now)!;
        Assert.Equal("Remember the thing", note.Text);
        notes.Add("p1", "Second", Now.AddMinutes(1));
        notes.Add("p2", "Other profile", Now.AddMinutes(2));
        Assert.Equal("Second", notes.NotesFor("p1")[0].Text);
        Assert.Equal(3, notes.CountSince(Now));
        Assert.Equal(1, notes.CountSince(Now.AddMinutes(2)));
        Assert.True(notes.Delete("p1", note.Id));
        Assert.Single(notes.NotesFor("p1"));
        Assert.False(notes.Delete("p1", note.Id));
    }

    [Fact]
    public void Notes_StoreRoundTrips_AndSanitizeDropsBlank()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "notes.json");
        var notes = new ProfileNotes();
        notes.Add("p1", "Kept note", Now);
        notes.ByProfile["p1"].Add(new NoteEntry { Id = "blank", Text = " ", CreatedUtc = Now });
        ProfileNotesStore.Save(path, notes);
        var back = ProfileNotesStore.Load(path);
        var kept = Assert.Single(back.NotesFor("p1"));
        Assert.Equal("Kept note", kept.Text);
    }

    [Fact]
    public void QuickNoteHotkey_ResolvesUnderTheGlobalRules_PlusShowHideEquality()
    {
        var none = new Dictionary<string, string>();
        // Off by default state.
        var off = QuickNoteHotkey.Resolve(false, null, none, "Ctrl+Alt+M");
        Assert.Equal(HotkeyResolutionKind.Disabled, off.Kind);
        // Default gesture is ready.
        var ready = QuickNoteHotkey.Resolve(true, null, none, "Ctrl+Alt+M");
        Assert.Equal(HotkeyResolutionKind.Ready, ready.Kind);
        Assert.Equal(QuickNoteHotkey.DefaultGesture, ready.Gesture);
        // Equal to the show/hide gesture: blocked, naming the owner.
        var clash = QuickNoteHotkey.Resolve(true, "Ctrl+Alt+M", none, "Ctrl+Alt+M");
        Assert.Equal(HotkeyResolutionKind.BlockedByConflict, clash.Kind);
        Assert.Contains("show/hide", clash.ConflictOwner);
        // A stored gesture without Ctrl/Alt falls back to the default.
        var bare = QuickNoteHotkey.Resolve(true, "N", none, "Ctrl+Alt+M");
        Assert.Equal(QuickNoteHotkey.DefaultGesture, bare.Gesture);
        // TrySet validates the same rules for the Settings capture:
        // the default note gesture is fine; the show/hide gesture is not.
        Assert.True(QuickNoteHotkey.TrySetGesture(none, "Ctrl+Alt+M",
            QuickNoteHotkey.DefaultGesture, out var err));
        Assert.Null(err);
        Assert.False(QuickNoteHotkey.TrySetGesture(none, "Ctrl+Alt+M",
            GlobalHotkey.DefaultGesture, out err));
        Assert.NotNull(err);
    }

    // ─────────────────────── weekly stats + dashboard (#150) ───────────────────────

    [Fact]
    public void WeeklyStats_WeekStartsMondayUtc_AndRecordsPerWeek()
    {
        // 2026-10-26 is a Monday; the Sunday before belongs to the prior week.
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero),
            WeeklyStats.WeekStartUtc(Now));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero),
            WeeklyStats.WeekStartUtc(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero)));

        var stats = new WeeklyStats();
        stats.RecordLaunch("p1", Now);
        stats.RecordLaunch("p1", Now);
        stats.RecordDownload("p1", Now);
        stats.RecordFocusMinutes("p1", 50, Now);
        stats.RecordFocusMinutes("p1", 0, Now);   // ignored
        stats.RecordFocusMinutes("p1", -5, Now);  // ignored
        stats.RecordLaunch("p1", Now.AddDays(-8)); // last week — separate bucket
        var counts = stats.CountsFor("p1", Now);
        Assert.Equal(2, counts.Launches);
        Assert.Equal(1, counts.DownloadsCompleted);
        Assert.Equal(50, counts.FocusMinutes);
        Assert.Equal(1, stats.CountsFor("p1", Now.AddDays(-8)).Launches);
        Assert.Equal(0, stats.CountsFor("nobody", Now).Launches);
    }

    [Fact]
    public void WeeklyStats_PrunesBeyond26Weeks_AndStoreRoundTrips()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "weekly-stats.json");
        var stats = new WeeklyStats();
        stats.RecordLaunch("p1", Now.AddDays(-7 * 40)); // ancient — pruned by the next record
        stats.RecordLaunch("p1", Now);
        Assert.Equal(0, stats.CountsFor("p1", Now.AddDays(-7 * 40)).Launches);
        WeeklyStatsStore.Save(path, stats);
        var back = WeeklyStatsStore.Load(path);
        Assert.Equal(1, back.CountsFor("p1", Now).Launches);
    }

    [Fact]
    public void StatsReport_RowsTotalsAndFractions()
    {
        var settings = new AppSettings();
        settings.Profiles.Add(new AccountProfile { Id = "p1", Name = "Work" });
        settings.Profiles.Add(new AccountProfile { Id = "p2", Name = "Personal" });
        var stats = new WeeklyStats();
        stats.RecordLaunch("p1", Now);
        stats.RecordLaunch("p1", Now);
        stats.RecordLaunch("p2", Now);
        stats.RecordFocusMinutes("p2", 90, Now);

        var report = StatsReport.Build(stats, settings, Now);
        Assert.Equal(WeeklyStats.WeekStartUtc(Now), report.WeekStartUtc);
        Assert.Equal(3, report.TotalLaunches);
        Assert.Equal(90, report.TotalFocusMinutes);
        var work = report.Rows.First(r => r.ProfileId == "p1");
        var personal = report.Rows.First(r => r.ProfileId == "p2");
        Assert.Equal(1.0, work.LaunchFraction);       // the week's best launches
        Assert.Equal(0.5, personal.LaunchFraction);
        Assert.Equal(1.0, personal.FocusFraction);
        Assert.Equal(0.0, work.FocusFraction);
        Assert.Equal("Work", work.Name);
    }

    // ─────────────────────── weekly digest (#146) ───────────────────────

    [Fact]
    public void Digest_SummarizesTheWeekFromLocalStoresOnly()
    {
        var settings = new AppSettings();
        settings.Profiles.Add(new AccountProfile { Id = "p1", Name = "Work" });
        var stats = new WeeklyStats();
        stats.RecordLaunch("p1", Now);
        stats.RecordFocusMinutes("p1", 75, Now);
        var reading = new ReadingLists();
        reading.Add("p1", "Saved", "https://muse.ai/chat/s", Now);
        var notes = new ProfileNotes();
        notes.Add("p1", "A note", Now);
        notes.Add("p1", "An old note", Now.AddDays(-10)); // last week — not counted
        var downloads = new List<DownloadRecord>
        {
            new() { FileName = "a.pdf", FullPath = "/x/a.pdf", State = DownloadRecordState.Completed, CompletedUtc = Now },
            new() { FileName = "b.pdf", FullPath = "/x/b.pdf", State = DownloadRecordState.Failed, CompletedUtc = Now },
            new() { FileName = "c.pdf", FullPath = "/x/c.pdf", State = DownloadRecordState.Completed, CompletedUtc = Now.AddDays(-9) },
        };

        var digest = WeeklyDigest.Build(stats, settings, reading, notes, downloads,
            openWindowCount: 3, Now);
        var text = digest.RenderText();
        Assert.Contains("Launches: 1", text);
        Assert.Contains("Downloads completed: 1", text);
        Assert.Contains("Focus time: 1h 15m", text);
        Assert.Contains("Notes taken: 1", text);
        Assert.Contains("1 saved this week, 1 unread", text);
        Assert.Contains("Windows open right now: 3", text);
        Assert.Contains("Most active profile: Work", text);
        Assert.Contains("Nothing was sent anywhere", text);
    }

    // ─────────────────────── command scheduling (#147) ───────────────────────

    [Fact]
    public void Scheduler_StartupDailyAndOnce_FireOnceAndStamp()
    {
        var rules = new List<ScheduledCommand>
        {
            new() { Id = "a", CommandId = "action.settings", Trigger = ScheduleTriggerKind.OnStartup, Enabled = true },
            new() { Id = "b", CommandId = "action.downloads", Trigger = ScheduleTriggerKind.DailyAt, DailyMinutes = 9 * 60, Enabled = true },
            new() { Id = "c", CommandId = "action.help", Trigger = ScheduleTriggerKind.OnceAt, OnceAtUtc = Now.AddHours(-1), Enabled = true },
            new() { Id = "d", CommandId = "action.about", Trigger = ScheduleTriggerKind.DailyAt, DailyMinutes = 9 * 60, Enabled = false },
        };
        var due = CommandScheduler.Evaluate(rules, Now, startupPending: true);
        Assert.Equal(new[] { "a", "b", "c" }, due.Select(r => r.Id).ToArray());

        // Same tick again: startup consumed by the caller, daily + once stamped.
        var again = CommandScheduler.Evaluate(rules, Now, startupPending: false);
        Assert.Empty(again);
        Assert.Equal("2026-10-26", rules[1].LastRunLocalDate);
        Assert.True(rules[2].HasFired);

        // Tomorrow the daily rule fires again; the once rule never does.
        var tomorrow = CommandScheduler.Evaluate(rules, Now.AddDays(1), startupPending: false);
        Assert.Equal(new[] { "b" }, tomorrow.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Scheduler_DailyWaitsForItsTime_AndUnknownCommandsNeverFire()
    {
        var rules = new List<ScheduledCommand>
        {
            new() { Id = "late", CommandId = "action.settings", Trigger = ScheduleTriggerKind.DailyAt, DailyMinutes = 13 * 60, Enabled = true },
            new() { Id = "bogus", CommandId = "action.no-such-thing", Trigger = ScheduleTriggerKind.OnStartup, Enabled = true },
            new() { Id = "payload", CommandId = "profile.switch.p1", Trigger = ScheduleTriggerKind.OnStartup, Enabled = true },
        };
        Assert.Empty(CommandScheduler.Evaluate(rules, Now, startupPending: true));
        var due = CommandScheduler.Evaluate(rules, Now.AddHours(1), startupPending: false);
        Assert.Equal(new[] { "late" }, due.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Scheduler_NextDueFindsNearestActualDeadline()
    {
        var rules = new List<ScheduledCommand>
        {
            new() { Id = "daily", CommandId = "action.settings", Trigger = ScheduleTriggerKind.DailyAt, DailyMinutes = 13 * 60, Enabled = true },
            new() { Id = "once", CommandId = "action.help", Trigger = ScheduleTriggerKind.OnceAt, OnceAtUtc = Now.AddMinutes(20), Enabled = true },
            new() { Id = "startup", CommandId = "action.about", Trigger = ScheduleTriggerKind.OnStartup, Enabled = true },
        };
        Assert.Equal(Now, CommandScheduler.NextDue(rules, Now, startupPending: true));
        Assert.Equal(Now.AddMinutes(20), CommandScheduler.NextDue(rules, Now, startupPending: false));
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 13, 0, 0, TimeSpan.FromHours(-5)),
            CommandScheduler.NextDue(rules, Now.AddHours(2), startupPending: false));

        rules[1].HasFired = true;
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 13, 0, 0, TimeSpan.FromHours(-5)),
            CommandScheduler.NextDue(rules, Now, startupPending: false));
    }

    [Fact]
    public void Palette_SearchFindsFullNoteTextAndReturnsNoteIdOnlyAsPayload()
    {
        var notes = new ProfileNotes();
        var note = notes.Add("p1", "Remember this unusual quasar detail", Now)!;
        var palette = CommandRegistry.CreateDefault(
            Array.Empty<AccountProfile>(), "p1",
            Array.Empty<DownloadRecord>(), Array.Empty<WindowDescriptor>(),
            noteEntries: notes.NotesFor("p1"));

        var result = Assert.Single(palette.Search("quasar", new ActionHistory()));
        Assert.Equal(PaletteCommandSource.Note, result.Command.Source);
        Assert.Equal($"note.open.{note.Id}", result.Command.Id);
        Assert.Equal(note.Id, result.Command.Payload);
        Assert.Contains("Remember this unusual quasar detail", result.Command.Title);
        Assert.False(new ActionHistory().Record(result.Command));
    }

    [Fact]
    public void Scheduler_SanitizeAndTryCreate()
    {
        Assert.Null(CommandScheduler.Sanitize(new ScheduledCommand { CommandId = "action.nope" }));
        Assert.Null(CommandScheduler.Sanitize(new ScheduledCommand
        {
            CommandId = "action.settings",
            Trigger = ScheduleTriggerKind.OnceAt,
            OnceAtUtc = null,
        }));
        var ok = CommandScheduler.Sanitize(new ScheduledCommand
        {
            CommandId = "action.settings",
            Trigger = ScheduleTriggerKind.DailyAt,
            DailyMinutes = 99999,
        });
        Assert.NotNull(ok);
        Assert.False(string.IsNullOrWhiteSpace(ok!.Id));
        Assert.Equal(1439, ok.DailyMinutes);

        Assert.False(CommandScheduler.TryCreate("action.nope",
            ScheduleTriggerKind.OnStartup, 0, null, out var rule, out var error));
        Assert.Null(rule);
        Assert.NotNull(error);
        Assert.True(CommandScheduler.TryCreate("action.settings",
            ScheduleTriggerKind.DailyAt, 9 * 60, null, out rule, out error));
        Assert.Equal("action.settings", rule!.CommandId);
        Assert.Contains("09:00", rule.Describe());
        Assert.Contains("Open Settings", rule.Describe());
    }

    // ─────────────────────── registry + palette integration ───────────────────────

    [Fact]
    public void Registry_R5ActionsExist_AndAreSchedulable()
    {
        foreach (var id in new[]
                 {
                     "action.focus-timer", "action.session-timer",
                     "action.reading-list-add", "action.reading-list",
                     "action.quick-note", "action.notes",
                     "action.weekly-digest", "action.stats-dashboard",
                 })
        {
            Assert.True(CommandScheduler.IsKnownCommand(id), id);
        }
    }

    [Fact]
    public void Palette_WindowsProvider_OffersPresetsForTheActiveWindowOnly()
    {
        var windows = new[]
        {
            new WindowDescriptor("main", "Main page", "Work", null,
                IsMain: true, SupportsFocusMode: true, IsActive: true),
            new WindowDescriptor("w2", "Second page", "Work", null,
                IsMain: false, SupportsFocusMode: false, IsActive: false),
        };
        var commands = new WindowsPaletteProvider(windows).GetCommands().ToList();
        var presets = commands.Where(c => c.Id.StartsWith("window.tile-preset.")).ToList();
        Assert.NotEmpty(presets);
        Assert.All(presets, c => Assert.Equal("main", c.Payload));
        // Halves are NOT duplicated as presets (tile-left/right cover them).
        Assert.DoesNotContain(presets, c => c.Id.Contains("HalfLeft") || c.Id.Contains("HalfRight"));
        Assert.Contains(presets, c => c.Id == "window.tile-preset.QuarterTopLeft.main");
    }

    [Fact]
    public void Palette_CreateDefault_IncludesReadingAndHistoryWhenGiven()
    {
        var lists = new ReadingLists();
        lists.Add("p1", "Saved page", "https://muse.ai/chat/s", Now);
        var palette = CommandRegistry.CreateDefault(
            Array.Empty<AccountProfile>(), "p1",
            Array.Empty<DownloadRecord>(), Array.Empty<WindowDescriptor>(),
            readingEntries: lists.EntriesFor("p1"),
            historyEntries: SampleHistory());
        var all = palette.All();
        Assert.Contains(all, c => c.Source == PaletteCommandSource.ReadingList);
        Assert.Contains(all, c => c.Source == PaletteCommandSource.History);
        Assert.Contains(all, c => c.Id == "action.quick-note");

        // Without the inputs, those sections simply don't appear.
        var bare = CommandRegistry.CreateDefault(
            Array.Empty<AccountProfile>(), "p1",
            Array.Empty<DownloadRecord>(), Array.Empty<WindowDescriptor>());
        Assert.DoesNotContain(bare.All(), c => c.Source == PaletteCommandSource.ReadingList);
        Assert.DoesNotContain(bare.All(), c => c.Source == PaletteCommandSource.History);
    }

    // ─────────────────────── settings integration ───────────────────────

    [Fact]
    public void Settings_R5FieldsSanitize_AndSchedulesDropBogusRules()
    {
        var settings = new AppSettings
        {
            FocusTimerMinutes = 0,
            FocusBreakMinutes = 9999,
            SessionWorkMinutes = -1,
            SessionCyclesBeforeLongBreak = 42,
            QuickNoteHotkeyGesture = "plain letter",
            CommandSchedules = new List<ScheduledCommand>
            {
                new() { CommandId = "action.settings", Trigger = ScheduleTriggerKind.OnStartup },
                new() { CommandId = "window.switch.main", Trigger = ScheduleTriggerKind.OnStartup },
                new() { CommandId = "action.help", Trigger = ScheduleTriggerKind.OnceAt }, // no time — drops
            },
        };
        var clean = settings.Sanitized();
        Assert.Equal(1, clean.FocusTimerMinutes);
        Assert.Equal(120, clean.FocusBreakMinutes);
        Assert.Equal(1, clean.SessionWorkMinutes);
        Assert.Equal(12, clean.SessionCyclesBeforeLongBreak);
        Assert.Null(clean.QuickNoteHotkeyGesture); // unusable reads as "the default"
        var rule = Assert.Single(clean.CommandSchedules);
        Assert.Equal("action.settings", rule.CommandId);
        Assert.False(string.IsNullOrWhiteSpace(rule.Id));
    }

    [Fact]
    public void Settings_SearchFindsTheFocusSection()
    {
        Assert.Contains(SettingsSearch.Search("pomodoro"), s => s.Id == SettingsSearch.FocusId);
        Assert.Contains(SettingsSearch.Search("reading list"), s => s.Id == SettingsSearch.FocusId);
        Assert.Contains(SettingsSearch.Search("quick note"), s => s.Id == SettingsSearch.FocusId);
        // Older sections keep their pinned queries.
        Assert.Equal(SettingsSearch.DownloadsId, SettingsSearch.Search("downloads")[0].Id);
    }

    [Fact]
    public void Settings_ResetRestoresR5Preferences_ButKeepsSchedules()
    {
        var settings = new AppSettings
        {
            FocusTimerMinutes = 99,
            QuickNoteHotkeyEnabled = true,
            CommandSchedules = new List<ScheduledCommand>
            {
                new() { Id = "keep", CommandId = "action.settings", Trigger = ScheduleTriggerKind.OnStartup },
            },
        };
        SettingsReset.ApplyDefaults(settings);
        Assert.Equal(25, settings.FocusTimerMinutes);
        Assert.False(settings.QuickNoteHotkeyEnabled);
        Assert.Single(settings.CommandSchedules); // automation is user data, preserved
    }
}
