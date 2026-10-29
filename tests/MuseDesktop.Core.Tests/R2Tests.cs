using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>
/// R2 (v1.2.0, items 111–120): workspaces &amp; sessions — app routes
/// (never full URLs), the workspace model and its repairs, the apply
/// planner (incl. the in-flight-download guard decision and the
/// profile-switch case), the palette provider, the session snapshot,
/// and the crash journal's restore state machine (offer once, never
/// loop). The shell pieces (save/apply execution, tray menu, startup
/// wiring) stay thin over these.
/// </summary>
public class R2Tests
{
    private static readonly string[] BothProfiles = { Profiles.DefaultId, "work" };

    private static WorkspaceWindow Win(string profile, string route, bool main = false) =>
        new() { ProfileId = profile, Route = route, IsMain = main };

    private static AppSettings SettingsWithProfiles()
    {
        var s = new AppSettings();
        s.Profiles.Add(new AccountProfile { Id = "work", Name = "Work" });
        return s.Sanitized();
    }

    // ──────────────────────── #111 app routes ────────────────────────

    [Fact]
    public void Route_FromUrlTakesThePathOnly()
    {
        Assert.Equal("/chat", AppRoute.FromUrl("https://muse.ai/chat"));
        Assert.Equal("/", AppRoute.FromUrl("https://muse.ai/"));
        Assert.Equal("/", AppRoute.FromUrl("https://muse.ai"));
        Assert.Equal("/projects/abc", AppRoute.FromUrl("https://muse.ai/projects/abc"));
    }

    [Fact]
    public void Route_FromUrlStripsQueryAndFragment()
    {
        // The whole point: nothing from a query string (tokens, search
        // terms, conversation ids) can enter a saved layout.
        Assert.Equal("/chat", AppRoute.FromUrl("https://muse.ai/chat?token=secret&q=hello#frag"));
    }

    [Fact]
    public void Route_FromUrlRefusesOtherHostsAndSignInProviders()
    {
        Assert.Null(AppRoute.FromUrl("https://example.com/chat"));
        Assert.Null(AppRoute.FromUrl("https://muse.ai.evil.com/chat"));
        Assert.Null(AppRoute.FromUrl("https://accounts.google.com/signin"));
        Assert.Null(AppRoute.FromUrl("https://sub.muse.ai/chat"));
        Assert.Null(AppRoute.FromUrl("http://muse.ai/chat"));
        Assert.Null(AppRoute.FromUrl("not a url"));
        Assert.Null(AppRoute.FromUrl(null));
        Assert.Null(AppRoute.FromUrl(""));
    }

    [Fact]
    public void Route_SanitizeRejectsNonRoutes()
    {
        Assert.Equal("/chat", AppRoute.Sanitize("  /chat  "));
        Assert.Null(AppRoute.Sanitize("chat"));            // no leading slash
        Assert.Null(AppRoute.Sanitize("//evil.com/x"));    // protocol-relative
        Assert.Null(AppRoute.Sanitize("/a b"));            // whitespace
        Assert.Null(AppRoute.Sanitize("/a\\b"));           // backslash
        Assert.Null(AppRoute.Sanitize("/a?b=1"));          // a query is not a route
        Assert.Null(AppRoute.Sanitize("/a#b"));            // neither is a fragment
        Assert.Null(AppRoute.Sanitize("/" + new string('x', AppRoute.MaxLength)));
        Assert.Null(AppRoute.Sanitize(null));
        Assert.Null(AppRoute.Sanitize("   "));
    }

    [Fact]
    public void Route_ToUrlRebuildsOnTheHomeHost()
    {
        Assert.Equal("https://muse.ai/chat", AppRoute.ToUrl("/chat"));
        Assert.Equal("https://muse.ai/", AppRoute.ToUrl("/"));
        Assert.Equal("https://muse.ai/", AppRoute.ToUrl("garbage"));
        Assert.True(AppRoute.IsHome("/"));
        Assert.False(AppRoute.IsHome("/chat"));
    }

    // ──────────────────────── #119 glyphs ────────────────────────

    [Fact]
    public void Glyphs_SanitizeToPaletteKeysOnly()
    {
        Assert.Equal("Star", WorkspaceGlyphs.Sanitize("star"));
        Assert.Equal("Star", WorkspaceGlyphs.Sanitize(" Star "));
        Assert.Null(WorkspaceGlyphs.Sanitize("rocket"));
        Assert.Null(WorkspaceGlyphs.Sanitize(null));
        Assert.Null(WorkspaceGlyphs.Sanitize(""));
        Assert.Equal("★", WorkspaceGlyphs.SymbolFor("Star"));
        Assert.Null(WorkspaceGlyphs.SymbolFor("rocket"));
        Assert.Null(WorkspaceGlyphs.SymbolFor(null));
    }

    [Fact]
    public void Glyphs_MenuLabelPrefixesTheSymbol()
    {
        Assert.Equal("★ Focus", WorkspaceGlyphs.MenuLabel(new Workspace { Name = "Focus", Glyph = "Star" }));
        Assert.Equal("Focus", WorkspaceGlyphs.MenuLabel(new Workspace { Name = "Focus" }));
        Assert.Equal("Focus", WorkspaceGlyphs.MenuLabel(new Workspace { Name = "Focus", Glyph = "bogus" }));
    }

    // ──────────────────────── #111/#120 model repairs ────────────────────────

    [Fact]
    public void Workspaces_EnsureValidDropsBadIdsAndDedupes()
    {
        var s = SettingsWithProfiles();
        s.Workspaces.Add(new Workspace { Id = "ok-one", Name = "One" });
        s.Workspaces.Add(new Workspace { Id = "ok-one", Name = "Dupe" });
        s.Workspaces.Add(new Workspace { Id = "BAD ID!", Name = "Nope" });
        s.Workspaces.Add(new Workspace { Id = "", Name = "Empty" });
        Workspaces.EnsureValid(s);
        Assert.Single(s.Workspaces);
        Assert.Equal("One", s.Workspaces[0].Name);
    }

    [Fact]
    public void Workspaces_EnsureValidRepairsNameGlyphAccent()
    {
        var s = SettingsWithProfiles();
        s.Workspaces.Add(new Workspace
        {
            Id = "w1",
            Name = "  " + new string('n', 50) + "  ",
            Glyph = "rocket",
            AccentColor = "blue",
        });
        Workspaces.EnsureValid(s);
        var w = s.Workspaces[0];
        Assert.Equal(40, w.Name.Length);
        Assert.Null(w.Glyph);
        Assert.Equal("Blue", w.AccentColor);
    }

    [Fact]
    public void Workspaces_EnsureValidRepairsWindows()
    {
        var s = SettingsWithProfiles();
        var w = new Workspace { Id = "w1", Name = "Mix" };
        w.Windows.Add(Win("ghost-profile", "/chat", main: true));      // profile gone → dropped
        w.Windows.Add(Win(Profiles.DefaultId, "not-a-route", main: true)); // bad route → Home
        w.Windows.Add(Win(Profiles.DefaultId, "/projects", main: true));   // second main → unflagged
        w.Windows.Add(new WorkspaceWindow
        {
            ProfileId = "work",
            Route = "/ok",
            Placement = new WindowPlacement { Width = 5, Height = 99999 },
        });
        s.Workspaces.Add(w);
        Workspaces.EnsureValid(s);
        var wins = s.Workspaces[0].Windows;
        Assert.Equal(3, wins.Count);
        Assert.Equal(AppRoute.Home, wins[0].Route);
        Assert.True(wins[0].IsMain);
        Assert.False(wins[1].IsMain);
        Assert.Equal(1100, wins[2].Placement!.Width);
        Assert.Equal(760, wins[2].Placement!.Height);
    }

    [Fact]
    public void Workspaces_EnsureValidFlagsFirstWindowMainWhenNoneFlagged()
    {
        var wins = Workspaces.SanitizeWindows(
            new List<WorkspaceWindow> { Win("default", "/a"), Win("default", "/b") }, BothProfiles);
        Assert.True(wins[0].IsMain);
        Assert.False(wins[1].IsMain);
    }

    [Fact]
    public void Workspaces_EnsureValidCapsCounts()
    {
        var s = SettingsWithProfiles();
        for (var i = 0; i < Workspaces.MaxWorkspaces + 4; i++)
            s.Workspaces.Add(new Workspace { Id = $"w{i}", Name = $"W{i}" });
        var many = new Workspace { Id = "many", Name = "Many" };
        for (var i = 0; i < Workspaces.MaxWindowsPerWorkspace + 5; i++)
            many.Windows.Add(Win("default", $"/r{i}"));
        s.Workspaces.Add(many);
        Workspaces.EnsureValid(s);
        Assert.Equal(Workspaces.MaxWorkspaces, s.Workspaces.Count);
        var capped = Workspaces.SanitizeWindows(many.Windows, BothProfiles);
        Assert.Equal(Workspaces.MaxWindowsPerWorkspace, capped.Count);
    }

    [Fact]
    public void Workspaces_PointersRepairToNull()
    {
        var s = SettingsWithProfiles();
        s.Workspaces.Add(new Workspace { Id = "real", Name = "Real" });
        s.StartupWorkspaceId = "ghost";
        s.ActiveWorkspaceId = "ghost";
        s.PendingWorkspaceApplyId = "ghost";
        s.Sanitized();
        Assert.Null(s.StartupWorkspaceId);
        Assert.Null(s.ActiveWorkspaceId);
        Assert.Null(s.PendingWorkspaceApplyId);
        s.StartupWorkspaceId = "real";
        s.Sanitized();
        Assert.Equal("real", s.StartupWorkspaceId);
    }

    [Fact]
    public void Workspaces_NextDefaultNameSkipsTaken()
    {
        var s = SettingsWithProfiles();
        Assert.Equal("Workspace", Workspaces.NextDefaultName(s));
        s.Workspaces.Add(new Workspace { Id = "a", Name = "Workspace" });
        s.Workspaces.Add(new Workspace { Id = "b", Name = "workspace 2" });
        Assert.Equal("Workspace 3", Workspaces.NextDefaultName(s));
    }

    [Fact]
    public void Workspaces_DuplicateDeepCopiesAndRenames()
    {
        var s = SettingsWithProfiles();
        var source = new Workspace
        {
            Id = "orig",
            Name = "Focus",
            Glyph = "Star",
            LastAppliedUtc = DateTimeOffset.UtcNow,
        };
        source.Windows.Add(new WorkspaceWindow
        {
            ProfileId = "default",
            Route = "/chat",
            IsMain = true,
            Placement = new WindowPlacement { Left = 10, Width = 900 },
        });
        s.Workspaces.Add(source);
        var copy = Workspaces.Duplicate(source, s);
        Assert.NotEqual(source.Id, copy.Id);
        Assert.True(Workspaces.IsValidId(copy.Id));
        Assert.Equal("Focus copy", copy.Name);
        Assert.Null(copy.LastAppliedUtc);
        // Deep copy: mutating the copy's placement leaves the source alone.
        copy.Windows[0].Placement!.Left = 999;
        Assert.Equal(10, source.Windows[0].Placement!.Left);
        s.Workspaces.Add(copy);
        Assert.Equal("Focus copy 2", Workspaces.Duplicate(source, s).Name);
    }

    // ──────────────────────── #112 the apply planner ────────────────────────

    private static OpenWindowState Open(string id, string route, bool main = false, string profile = "default") =>
        new(id, profile, route, main);

    [Fact]
    public void Planner_ExactMatchIsANoOp()
    {
        var target = new List<WorkspaceWindow> { Win("default", "/chat", main: true), Win("default", "/projects") };
        var current = new List<OpenWindowState> { Open("main", "/chat", main: true), Open("s1", "/projects") };
        var plan = WorkspaceApplyPlanner.Plan(target, current, "default", downloadsInFlight: false);
        Assert.Equal(WorkspaceApplyKind.Apply, plan.Kind);
        Assert.True(plan.IsNoOp);
        Assert.Equal(new[] { "main", "s1" }, plan.KeptWindowIds);
        Assert.False(plan.MainRouteChanges);
    }

    [Fact]
    public void Planner_OpensMissingAndClosesExtras()
    {
        var target = new List<WorkspaceWindow> { Win("default", "/", main: true), Win("default", "/a"), Win("default", "/b") };
        var current = new List<OpenWindowState> { Open("main", "/", main: true), Open("s1", "/a"), Open("s2", "/old") };
        var plan = WorkspaceApplyPlanner.Plan(target, current, "default", downloadsInFlight: false);
        Assert.Single(plan.ToOpen);
        Assert.Equal("/b", plan.ToOpen[0].Route);
        Assert.Equal(new[] { "s2" }, plan.ToCloseWindowIds);
        Assert.Contains("s1", plan.KeptWindowIds);
        Assert.False(plan.IsNoOp);
    }

    [Fact]
    public void Planner_MainWindowNavigatesNeverCloses()
    {
        var target = new List<WorkspaceWindow> { Win("default", "/projects", main: true) };
        var current = new List<OpenWindowState> { Open("main", "/chat", main: true), Open("s1", "/chat") };
        var plan = WorkspaceApplyPlanner.Plan(target, current, "default", downloadsInFlight: false);
        Assert.True(plan.MainRouteChanges);
        Assert.Equal("/projects", plan.MainWindow!.Route);
        Assert.Equal(new[] { "s1" }, plan.ToCloseWindowIds);
        Assert.DoesNotContain("main", plan.ToCloseWindowIds);
    }

    [Fact]
    public void Planner_DuplicateRoutesMatchAsMultiset()
    {
        // Two windows on Home in the target, one open: open one more,
        // close none.
        var target = new List<WorkspaceWindow> { Win("default", "/", main: true), Win("default", "/"), Win("default", "/") };
        var current = new List<OpenWindowState> { Open("main", "/", main: true), Open("s1", "/") };
        var plan = WorkspaceApplyPlanner.Plan(target, current, "default", downloadsInFlight: false);
        Assert.Single(plan.ToOpen);
        Assert.Empty(plan.ToCloseWindowIds);
    }

    [Fact]
    public void Planner_OtherProfileMeansSwitchFirst()
    {
        var target = new List<WorkspaceWindow> { Win("work", "/chat", main: true) };
        var current = new List<OpenWindowState> { Open("main", "/", main: true) };
        var plan = WorkspaceApplyPlanner.Plan(target, current, "default", downloadsInFlight: false);
        Assert.Equal(WorkspaceApplyKind.SwitchProfileFirst, plan.Kind);
        Assert.Equal("work", plan.RequiredProfileId);
        Assert.Empty(plan.ToOpen);
        Assert.Empty(plan.ToCloseWindowIds);
    }

    [Fact]
    public void Planner_ForeignEntriesInMixedTargetAreSkippedAndCounted()
    {
        var target = new List<WorkspaceWindow>
        {
            Win("default", "/", main: true),
            Win("work", "/chat"),   // foreign to the target's own profile
        };
        var current = new List<OpenWindowState> { Open("main", "/", main: true) };
        var plan = WorkspaceApplyPlanner.Plan(target, current, "default", downloadsInFlight: false);
        Assert.Equal(WorkspaceApplyKind.Apply, plan.Kind);
        Assert.Equal(1, plan.SkippedOtherProfileCount);
        Assert.Empty(plan.ToOpen);
    }

    [Fact]
    public void Planner_DownloadGuardOnlyWhenClosingWithInFlightDownload()
    {
        var target = new List<WorkspaceWindow> { Win("default", "/", main: true) };
        var current = new List<OpenWindowState> { Open("main", "/", main: true), Open("s1", "/x") };
        Assert.True(WorkspaceApplyPlanner.Plan(target, current, "default", downloadsInFlight: true)
            .RequiresDownloadConfirmation);
        Assert.False(WorkspaceApplyPlanner.Plan(target, current, "default", downloadsInFlight: false)
            .RequiresDownloadConfirmation);
        // Nothing to close → no guard even mid-download.
        var openOnly = new List<WorkspaceWindow> { Win("default", "/", main: true), Win("default", "/x"), Win("default", "/y") };
        Assert.False(WorkspaceApplyPlanner.Plan(openOnly, current, "default", downloadsInFlight: true)
            .RequiresDownloadConfirmation);
    }

    [Fact]
    public void Planner_EmptyTargetClosesSecondariesOnly()
    {
        var current = new List<OpenWindowState> { Open("main", "/chat", main: true), Open("s1", "/x") };
        var plan = WorkspaceApplyPlanner.Plan(new List<WorkspaceWindow>(), current, "default", downloadsInFlight: false);
        Assert.Equal(WorkspaceApplyKind.Apply, plan.Kind);
        Assert.Null(plan.MainWindow);
        Assert.Equal(new[] { "s1" }, plan.ToCloseWindowIds);
    }

    // ──────────────────────── #115/#119 the palette provider ────────────────────────

    private static Workspace SampleWorkspace(string id, string name, string? glyph = null) => new()
    {
        Id = id,
        Name = name,
        Glyph = glyph,
        Windows = new List<WorkspaceWindow> { Win("default", "/", main: true), Win("default", "/chat") },
    };

    [Fact]
    public void Provider_OneApplyCommandPerWorkspace()
    {
        var provider = new WorkspacesPaletteProvider(
            new List<Workspace> { SampleWorkspace("w1", "Focus", "Star"), SampleWorkspace("w2", "Reading") },
            activeWorkspaceId: "w1", startupWorkspaceId: "w2");
        var commands = provider.GetCommands().ToList();
        Assert.Equal(2, commands.Count);
        var focus = commands.Single(c => c.Id == "workspace.apply.w1");
        Assert.Equal("★ Apply workspace: Focus", focus.Title);
        Assert.Equal("w1", focus.Payload);
        Assert.Equal(PaletteCommandSource.Workspace, focus.Source);
        Assert.Equal("Workspaces", focus.Category);
        Assert.Contains("current", focus.Detail);
        Assert.Contains("2 windows", focus.Detail!);
        var reading = commands.Single(c => c.Id == "workspace.apply.w2");
        Assert.Equal("Apply workspace: Reading", reading.Title);
        Assert.Contains("opens at launch", reading.Detail!);
    }

    [Fact]
    public void Provider_NoWorkspacesNoCommands()
    {
        Assert.Empty(new WorkspacesPaletteProvider().GetCommands());
        Assert.Empty(new WorkspacesPaletteProvider(
            new List<Workspace>(), null, null).GetCommands());
    }

    [Fact]
    public void Registry_CreateDefaultCarriesWorkspaceCommands()
    {
        var palette = CommandRegistry.CreateDefault(
            new List<AccountProfile> { new() }, Profiles.DefaultId,
            Array.Empty<DownloadRecord>(), Array.Empty<WindowDescriptor>(),
            new List<Workspace> { SampleWorkspace("w1", "Focus") },
            activeWorkspaceId: null, startupWorkspaceId: null);
        Assert.Contains(palette.All(), c => c.Id == "workspace.apply.w1");
        Assert.Contains("Workspaces", palette.ProviderNames);
        // The palette finds a workspace by name.
        var hits = palette.Search("focus");
        Assert.Contains(hits, r => r.Command.Id == "workspace.apply.w1");
        // Workspace commands are not action history — nothing a command
        // carried (a workspace id) may be remembered.
        var history = new ActionHistory();
        Assert.False(history.Record(hits.First(r => r.Command.Id == "workspace.apply.w1").Command));
        Assert.Empty(history.RecentIds);
    }

    [Fact]
    public void Registry_SaveAndSnapshotActionsExist()
    {
        Assert.Contains(CommandRegistry.BuiltinActions, a => a.Id == "action.save-workspace");
        Assert.Contains(CommandRegistry.BuiltinActions, a => a.Id == "action.snapshot-session");
        Assert.Contains(CommandRegistry.BuiltinActions, a => a.Id == "action.restore-session");
        // The generated commands page lists them (they're built-ins).
        var page = CommandsHelp.Render(CommandRegistry.HelpListing());
        Assert.Contains("Save current arrangement as workspace", page);
    }

    // ──────────────────────── #114 session snapshot ────────────────────────

    [Fact]
    public void Snapshot_RoundTripsThroughItsStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"md-snap-{Guid.NewGuid():N}.json");
        try
        {
            var snapshot = SessionSnapshot.Capture(new[]
            {
                Win("default", "/chat", main: true),
                Win("work", "/projects"),
            }, DateTimeOffset.UtcNow, new[] { "/chat", "/projects" }, activeMainTabIndex: 1);
            SessionSnapshotStore.Save(path, snapshot);
            var loaded = SessionSnapshotStore.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.Windows.Count);
            Assert.Equal("/chat", loaded.Windows[0].Route);
            Assert.True(loaded.Windows[0].IsMain);
            Assert.Equal(new[] { "/chat", "/projects" }, loaded.MainTabRoutes);
            Assert.Equal(1, loaded.ActiveMainTabIndex);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Snapshot_MissingOrCorruptReadsAsNone()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"md-snap-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "snap.json");
        Assert.Null(SessionSnapshotStore.Load(path));
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, "{ not json");
        Assert.Null(SessionSnapshotStore.Load(path));
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void Snapshot_SanitizeDropsDeadProfilesAndBadRoutes()
    {
        var snapshot = new SessionSnapshot
        {
            Windows = new List<WorkspaceWindow>
            {
                Win("ghost", "/chat", main: true),
                Win("default", "::::", main: true),
            },
        };
        var clean = snapshot.Sanitized(BothProfiles);
        Assert.Single(clean.Windows);
        Assert.Equal(AppRoute.Home, clean.Windows[0].Route);
        Assert.True(clean.Windows[0].IsMain);
        Assert.Equal(new[] { AppRoute.Home }, clean.MainTabRoutes);
    }

    [Fact]
    public void Snapshot_SanitizesAndBoundsTabRoutesAndRepairsActiveIndex()
    {
        var snapshot = new SessionSnapshot
        {
            Windows = new List<WorkspaceWindow> { Win("default", "/home", main: true) },
            MainTabRoutes = new List<string> { "/bad?token=secret", "//evil", "/tab0" }
                .Concat(Enumerable.Range(1, 12).Select(i => $"/tab{i}"))
                .ToList(),
            ActiveMainTabIndex = 2,
        };
        var clean = snapshot.Sanitized(BothProfiles);
        Assert.Equal(SessionSnapshot.MaxMainTabs, clean.MainTabRoutes.Count);
        Assert.Equal("/tab0", clean.MainTabRoutes[0]);
        Assert.Equal("/tab9", clean.MainTabRoutes[^1]);
        Assert.Equal(0, clean.ActiveMainTabIndex);
        Assert.DoesNotContain(clean.MainTabRoutes, route => route.Contains('?') || route.StartsWith("//"));
    }

    [Fact]
    public void Snapshot_OlderFileFallsBackToItsMainRoute()
    {
        var snapshot = new SessionSnapshot
        {
            Windows = new List<WorkspaceWindow> { Win("default", "/chat", main: true) },
        };
        var clean = snapshot.Sanitized(BothProfiles);
        Assert.Equal(new[] { "/chat" }, clean.MainTabRoutes);
        Assert.Equal(0, clean.ActiveMainTabIndex);
    }

    // ──────────────────────── #117/#118 crash journal ────────────────────────

    private static SessionJournal Journal(bool clean, bool restored = false, bool healthy = false, int windows = 2) => new()
    {
        CleanExit = clean,
        WasRestored = restored,
        BecameHealthy = healthy,
        Windows = Enumerable.Range(0, windows)
            .Select(i => Win("default", i == 0 ? "/" : $"/w{i}", main: i == 0)).ToList(),
        MainTabRoutes = windows > 0
            ? Enumerable.Range(0, Math.Min(windows, 2)).Select(i => i == 0 ? "/" : $"/w{i}").ToList()
            : new List<string>(),
        ActiveMainTabIndex = windows > 1 ? 1 : 0,
    };

    [Theory]
    [InlineData(false, false, CrashRestoreDecision.None)]                 // no journal at all
    public void CrashPolicy_NoJournalNoRestore(bool _1, bool _2, CrashRestoreDecision expected)
    {
        Assert.Equal(expected, CrashRestorePolicy.Decide(null, autoSaveEnabled: false));
        Assert.Equal(expected, CrashRestorePolicy.Decide(null, autoSaveEnabled: true));
        Assert.Equal(CrashRestoreDecision.None,
            CrashRestorePolicy.Decide(Journal(clean: false, windows: 0), autoSaveEnabled: false));
    }

    [Fact]
    public void CrashPolicy_CleanExitRestoresOnlyWithAutoSave()
    {
        Assert.Equal(CrashRestoreDecision.None,
            CrashRestorePolicy.Decide(Journal(clean: true), autoSaveEnabled: false));
        Assert.Equal(CrashRestoreDecision.RestoreSilently,
            CrashRestorePolicy.Decide(Journal(clean: true), autoSaveEnabled: true));
    }

    [Fact]
    public void CrashPolicy_CrashOffersOnceOrRestoresSilently()
    {
        Assert.Equal(CrashRestoreDecision.OfferRestore,
            CrashRestorePolicy.Decide(Journal(clean: false), autoSaveEnabled: false));
        Assert.Equal(CrashRestoreDecision.RestoreSilently,
            CrashRestorePolicy.Decide(Journal(clean: false), autoSaveEnabled: true));
    }

    [Fact]
    public void CrashPolicy_RestoredThenDiedUnhealthyNeverLoops()
    {
        // The restore itself is the suspect: a session restored from a
        // crash that died before its first successful load must not be
        // offered (or silently restored) again — either way round.
        var journal = Journal(clean: false, restored: true, healthy: false);
        Assert.Equal(CrashRestoreDecision.SuppressRestoreLoop,
            CrashRestorePolicy.Decide(journal, autoSaveEnabled: false));
        Assert.Equal(CrashRestoreDecision.SuppressRestoreLoop,
            CrashRestorePolicy.Decide(journal, autoSaveEnabled: true));
        // But a restored session that DID become healthy earns the
        // normal treatment when it later crashes.
        var healthyCrash = Journal(clean: false, restored: true, healthy: true);
        Assert.Equal(CrashRestoreDecision.OfferRestore,
            CrashRestorePolicy.Decide(healthyCrash, autoSaveEnabled: false));
    }

    [Fact]
    public void Journal_RoundTripsThroughItsStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"md-journal-{Guid.NewGuid():N}.json");
        try
        {
            SessionJournalStore.Save(path, Journal(clean: false, restored: true));
            var loaded = SessionJournalStore.Load(path);
            Assert.NotNull(loaded);
            Assert.False(loaded!.CleanExit);
            Assert.True(loaded.WasRestored);
            Assert.Equal(2, loaded.Windows.Count);
            Assert.Equal(new[] { "/", "/w1" }, loaded.MainTabRoutes);
            Assert.Equal(1, loaded.ActiveMainTabIndex);
            File.WriteAllText(path, "garbage{");
            Assert.Null(SessionJournalStore.Load(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Journal_SanitizesRoutesAndFallsBackForOlderFiles()
    {
        var journal = new SessionJournal
        {
            Windows = new List<WorkspaceWindow> { Win("default", "/main", main: true) },
            MainTabRoutes = new List<string> { "//bad", "/chat?token=secret", "/tab" },
            ActiveMainTabIndex = 2,
        };
        var clean = journal.Sanitized(BothProfiles);
        Assert.Equal(new[] { "/tab" }, clean.MainTabRoutes);
        Assert.Equal(0, clean.ActiveMainTabIndex);

        var older = new SessionJournal
        {
            Windows = new List<WorkspaceWindow> { Win("default", "/main", main: true) },
        }.Sanitized(BothProfiles);
        Assert.Equal(new[] { "/main" }, older.MainTabRoutes);
        Assert.Equal(0, older.ActiveMainTabIndex);
    }

    // ──────────────────────── #116 portable workspaces ────────────────────────

    private static AppSettings SettingsWithWorkspace()
    {
        var s = SettingsWithProfiles();
        var ws = new Workspace
        {
            Id = "focus1",
            Name = "Focus",
            Glyph = "Star",
            AccentColor = "Blue",
        };
        ws.Windows.Add(Win(Profiles.DefaultId, "/chat", main: true));
        ws.Windows.Add(Win("work", "/projects"));
        s.Workspaces.Add(ws);
        s.StartupWorkspaceId = "focus1";
        return s.Sanitized();
    }

    [Fact]
    public void Portable_WorkspacesRoundTripThroughExportImport()
    {
        var s = SettingsWithWorkspace();
        var json = SettingsStore.Export(s);
        Assert.Contains("\"workspaces\"", json);
        Assert.True(SettingsStore.TryImport(json, out var back, out var error), error);
        var ws = Assert.Single(back!.Workspaces);
        Assert.Equal("Focus", ws.Name);
        Assert.Equal("Star", ws.Glyph);
        Assert.Equal("Blue", ws.AccentColor);
        Assert.Equal(2, ws.Windows.Count);
        Assert.Equal("focus1", back.StartupWorkspaceId);
        // Session state is NOT a Settings-clone adoption surface, but a
        // plain import is a restore: pointers survive when valid.
    }

    [Fact]
    public void Portable_WorkspacesSurviveEncryptedRoundTrip()
    {
        var s = SettingsWithWorkspace();
        var encrypted = EncryptedExport.Encrypt(SettingsStore.Export(s), "correct horse");
        Assert.True(EncryptedExport.TryDecrypt(encrypted, "correct horse",
            out var plain, out _, out var message), message);
        Assert.True(SettingsStore.TryImport(plain!, out var back, out var error), error);
        Assert.Single(back!.Workspaces);
        Assert.Equal("/projects", back.Workspaces[0].Windows[1].Route);
    }

    [Fact]
    public void Portable_OlderFileWithoutWorkspacesLoadsEmpty()
    {
        // A pre-R2 settings file has no workspaces field at all.
        Assert.True(SettingsStore.TryImport("{\"zoomFactor\":1.25}", out var back, out var error), error);
        Assert.Empty(back!.Workspaces);
        Assert.Null(back.StartupWorkspaceId);
        Assert.False(back.AutoSaveSession);
    }

    [Fact]
    public void Portable_CloneCarriesWorkspacesDeeply()
    {
        var s = SettingsWithWorkspace();
        var clone = s.Clone();
        clone.Workspaces[0].Windows[0].Route = "/changed";
        Assert.Equal("/chat", s.Workspaces[0].Windows[0].Route);
    }

    // ──────────────────────── reset + search wiring ────────────────────────

    [Fact]
    public void Reset_KeepsWorkspacesButResetsTheChoices()
    {
        var s = SettingsWithWorkspace();
        s.AutoSaveSession = true;
        SettingsReset.ApplyDefaults(s);
        Assert.Single(s.Workspaces);                    // user data, like profiles
        Assert.Null(s.StartupWorkspaceId);              // a preference — reset
        Assert.False(s.AutoSaveSession);                // a preference — reset
    }

    [Fact]
    public void SettingsSearch_WorkspaceWordsFindTheSection()
    {
        Assert.Contains(SettingsSearch.Sections, s => s.Id == SettingsSearch.WorkspacesId);
        Assert.Contains(SettingsSearch.Search("workspace"), s => s.Id == SettingsSearch.WorkspacesId);
        Assert.Contains(SettingsSearch.Search("snapshot"), s => s.Id == SettingsSearch.WorkspacesId);
        Assert.Contains(SettingsSearch.Search("auto save session"), s => s.Id == SettingsSearch.WorkspacesId);
    }

    [Fact]
    public void Settings_WorkspacesRideSanitizedOnEveryLoad()
    {
        var s = new AppSettings();
        s.Workspaces.Add(new Workspace { Id = "w1", Name = "One" });
        s.Workspaces[0].Windows.Add(Win("missing-profile", "/x", main: true));
        var clean = s.Sanitized();
        Assert.Empty(clean.Workspaces[0].Windows);
    }
}
