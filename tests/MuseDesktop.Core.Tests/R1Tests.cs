using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>
/// R1 (v1.1.0, items 101–110): the command palette — the one fuzzy
/// matcher and its Settings-search adoption, the provider registry,
/// action history (ids only), the window MRU/tiling models, the
/// go-to-profile chords, and the generated commands page. The shell
/// pieces (palette window, switcher overlay, tray middle-click) stay
/// thin over these.
/// </summary>
public class R1Tests
{
    // ──────────────────────── #101/#108 the fuzzy matcher ────────────────────────

    [Fact]
    public void Fuzzy_BlankQueryMatchesEverythingAtZero()
    {
        Assert.Equal(new FuzzyScore(true, 0), FuzzyMatch.Score(null, "anything"));
        Assert.Equal(new FuzzyScore(true, 0), FuzzyMatch.Score("  ", "anything"));
        Assert.False(FuzzyMatch.Score("x", null).IsMatch);
        Assert.False(FuzzyMatch.Score("x", "").IsMatch);
    }

    [Fact]
    public void Fuzzy_SubsequenceMustBeInOrder()
    {
        Assert.True(FuzzyMatch.Score("odw", "Open Downloads").IsMatch);
        Assert.True(FuzzyMatch.Score("ODW", "Open Downloads").IsMatch); // case-insensitive
        Assert.False(FuzzyMatch.Score("wdo", "Open Downloads").IsMatch);
        Assert.False(FuzzyMatch.Score("xyz", "Open Downloads").IsMatch);
    }

    [Fact]
    public void Fuzzy_ExactTitleBeatsLongerTitleContainingIt()
    {
        var exact = FuzzyMatch.Score("downloads", "Downloads");
        var longer = FuzzyMatch.Score("downloads", "Open Downloads");
        Assert.True(exact.IsMatch && longer.IsMatch);
        Assert.True(exact.Score > longer.Score, $"{exact.Score} vs {longer.Score}");
    }

    [Fact]
    public void Fuzzy_WordStartBeatsMidWord()
    {
        var atWord = FuzzyMatch.Score("dl", "Downloads");
        var midWord = FuzzyMatch.Score("dl", "Guided log");
        Assert.True(atWord.IsMatch && midWord.IsMatch);
        Assert.True(atWord.Score > midWord.Score, $"{atWord.Score} vs {midWord.Score}");
    }

    [Fact]
    public void Fuzzy_WordScoreStaysInsideOneWord()
    {
        Assert.True(FuzzyMatch.WordScore("profile", "Account profiles settings").IsMatch);
        // Letters borrowed across three words do not count.
        Assert.False(FuzzyMatch.WordScore("profile", "protocol first run").IsMatch);
        Assert.True(FuzzyMatch.WordScore("zoom", "the zoom level").IsMatch);
        Assert.False(FuzzyMatch.WordScore("zoom", "zebra room").IsMatch);
    }

    // ──────────────────────── #108 Settings search adopts the matcher ────────────────────────

    [Fact]
    public void SettingsSearch_RanksTitleWordAboveKeywordWord()
    {
        // "zoom" is the Zoom section's TITLE and only a keyword of
        // Profiles/Accessibility — the title owner ranks first now.
        var matches = SettingsSearch.Search("zoom");
        Assert.Equal(SettingsSearch.ZoomId, matches[0].Id);
        Assert.Contains(matches, m => m.Id == SettingsSearch.ProfilesId);
    }

    [Fact]
    public void SettingsSearch_FuzzyFindsDownloads()
    {
        Assert.Equal(SettingsSearch.DownloadsId, SettingsSearch.Search("downloads")[0].Id);
        // A typo the old prefix rule missed (subsequence, not prefix).
        Assert.Contains(SettingsSearch.Search("dwnloads"), m => m.Id == SettingsSearch.DownloadsId);
        Assert.Contains(SettingsSearch.Search("down"), m => m.Id == SettingsSearch.DownloadsId);
    }

    [Fact]
    public void SettingsSearch_PinsSurviveTheAdoption()
    {
        Assert.Empty(SettingsSearch.Search(null));
        Assert.Empty(SettingsSearch.Search("zzzznothing"));
        // The S5 exact pin: only Profiles owns both terms.
        Assert.Equal(new[] { SettingsSearch.ProfilesId },
            SettingsSearch.Search("profile zoom").Select(m => m.Id).ToArray());
        Assert.Contains(SettingsSearch.Search("theme"), m => m.Id == SettingsSearch.AppearanceId);
        Assert.Contains(SettingsSearch.Search("microphone test"), m => m.Id == SettingsSearch.PrivacyId);
    }

    // ──────────────────────── #102 registry & providers ────────────────────────

    private static readonly AccountProfile WorkProfile = new() { Id = "work", Name = "Work" };

    private static CommandPalette DefaultPalette() => CommandRegistry.CreateDefault(
        new List<AccountProfile> { new(), WorkProfile },
        Profiles.DefaultId,
        new List<DownloadRecord>
        {
            new() { FileName = "report.pdf", FullPath = @"C:\Users\x\Downloads\report.pdf", SizeBytes = 2048, State = DownloadRecordState.Completed },
            new() { FileName = "lost.bin", FullPath = @"C:\Users\x\Downloads\lost.bin", State = DownloadRecordState.Failed },
        },
        new List<WindowDescriptor>
        {
            new("main", "A chat", "Default", null, IsMain: true, SupportsFocusMode: true, IsActive: true),
            new("secondary-1", "Another chat", "Default", "Blue", IsMain: false, SupportsFocusMode: false, IsActive: false),
        });

    [Fact]
    public void Registry_BuiltinActions_AreUniqueActionIds()
    {
        var actions = CommandRegistry.BuiltinActions;
        Assert.Equal(actions.Count, actions.Select(a => a.Id).Distinct().Count());
        Assert.All(actions, a =>
        {
            Assert.StartsWith("action.", a.Id);
            Assert.Equal(PaletteCommandSource.Action, a.Source);
            Assert.False(string.IsNullOrWhiteSpace(a.Title));
            Assert.False(string.IsNullOrWhiteSpace(a.Keywords));
        });
        Assert.Contains(actions, a => a.Id == "action.settings");
        Assert.Contains(actions, a => a.Id == "action.downloads");
        Assert.Contains(actions, a => a.Id == "action.switch-window");
        Assert.Contains(actions, a => a.Id == "action.commands-help");
        Assert.Contains(actions, a => a.Id == "action.exit");
    }

    [Fact]
    public void Providers_ProfilesSettingsHelpDownloadsWindows()
    {
        var all = DefaultPalette().All();
        // Profiles: one switch per profile, payload = the id, active marked.
        var profiles = all.Where(c => c.Source == PaletteCommandSource.Profile).ToList();
        Assert.Equal(2, profiles.Count);
        Assert.Equal("work", profiles.Single(c => c.Id == "profile.switch.work").Payload);
        Assert.Equal("Current profile", profiles.Single(c => c.Id == "profile.switch.default").Detail);
        // Settings: one per indexed section.
        Assert.Equal(SettingsSearch.Sections.Count,
            all.Count(c => c.Source == PaletteCommandSource.Settings));
        // Help: one per catalog page.
        Assert.Equal(HelpCatalog.Pages.Count,
            all.Count(c => c.Source == PaletteCommandSource.Help));
        // Downloads: open + reveal for the ONE completed record; the
        // failed record contributes nothing, and no id carries a path.
        var downloads = all.Where(c => c.Source == PaletteCommandSource.Download).ToList();
        Assert.Equal(2, downloads.Count);
        Assert.All(downloads, c => Assert.DoesNotContain(":", c.Id));
        Assert.Equal(@"C:\Users\x\Downloads\report.pdf", downloads[0].Payload);
        // Windows: switch + 4 actions for the secondary; the main
        // window additionally gets focus mode.
        Assert.Equal(6, all.Count(c => c.Payload == "main" && c.Source == PaletteCommandSource.Window));
        Assert.Equal(5, all.Count(c => c.Payload == "secondary-1" && c.Source == PaletteCommandSource.Window));
        Assert.Contains(all, c => c.Id == "window.focus-mode.main");
        Assert.DoesNotContain(all, c => c.Id == "window.focus-mode.secondary-1");
    }

    [Fact]
    public void Providers_WorkspacesStubIsRegisteredButEmpty()
    {
        Assert.Empty(new WorkspacesPaletteProvider().GetCommands());
        Assert.Contains("Workspaces", DefaultPalette().ProviderNames);
    }

    [Fact]
    public void Providers_DownloadsCapApplies()
    {
        var records = Enumerable.Range(1, 10)
            .Select(i => new DownloadRecord { FileName = $"f{i}.txt", FullPath = $@"C:\d\f{i}.txt", State = DownloadRecordState.Completed })
            .ToList();
        var commands = new DownloadsPaletteProvider(records, max: 3).GetCommands().ToList();
        Assert.Equal(6, commands.Count); // open + reveal per kept record
    }

    [Fact]
    public void Palette_AllDedupesByIdFirstProviderWins()
    {
        var palette = new CommandPalette();
        var provider = new StaticPaletteProvider("dup", CommandRegistry.BuiltinActions);
        palette.Register(provider);
        palette.Register(provider);
        Assert.Equal(CommandRegistry.BuiltinActions.Count, palette.All().Count);
    }

    // ──────────────────────── #101 palette search ────────────────────────

    [Fact]
    public void Palette_BlankQuery_PinsRecentsFirstThenEverything()
    {
        var history = new ActionHistory();
        history.RecordId("action.downloads");
        history.RecordId("action.settings"); // most recent
        var palette = DefaultPalette();
        var results = palette.Search("", history, maxResults: 500);
        Assert.Equal(palette.All().Count, results.Count);
        Assert.Equal("action.settings", results[0].Command.Id);
        Assert.Equal("action.downloads", results[1].Command.Id);
        Assert.True(results[0].IsRecent && results[1].IsRecent);
        Assert.False(results[2].IsRecent);
    }

    [Fact]
    public void Palette_QueryRanksTheObviousWinnerFirst()
    {
        var palette = DefaultPalette();
        Assert.Equal("action.downloads", palette.Search("downloads")[0].Command.Id);
        Assert.Equal("profile.switch.work", palette.Search("work profile")[0].Command.Id);
        Assert.Contains(palette.Search("mic"), r => r.Command.Id == "help.open.microphone-denied");
        Assert.Empty(palette.Search("qqqzzz"));
    }

    [Fact]
    public void Palette_RecencyBoostsTypedSearch()
    {
        var palette = DefaultPalette();
        var plain = palette.Search("help");
        var history = new ActionHistory();
        history.RecordId("action.help");
        var boosted = palette.Search("help", history);
        Assert.Equal("action.help", boosted[0].Command.Id);
        Assert.True(boosted[0].IsRecent);
        Assert.True(boosted[0].Score >= plain.First(r => r.Command.Id == "action.help").Score);
    }

    [Fact]
    public void Palette_MaxResultsIsRespected()
    {
        Assert.Equal(3, DefaultPalette().Search("", null, 3).Count);
        Assert.Single(DefaultPalette().Search("e", null, 0)); // clamped, never zero
    }

    // ──────────────────────── #106 action history ────────────────────────

    private static PaletteCommand CommandOf(string id) =>
        CommandRegistry.BuiltinActions.Single(a => a.Id == id);

    [Fact]
    public void History_RecordsActionsOnly_NeverPayloads()
    {
        var history = new ActionHistory();
        Assert.True(history.Record(CommandOf("action.downloads")));
        Assert.False(history.Record(new PaletteCommand(
            "profile.switch.work", "x", null, "Profiles", "", PaletteCommandSource.Profile, Payload: "work")));
        Assert.False(history.Record(new PaletteCommand(
            "download.open.0", "x", null, "Downloads", "", PaletteCommandSource.Download, Payload: @"C:\secret\file.pdf")));
        Assert.False(history.RecordId("download.open.0"));
        Assert.False(history.RecordId("window.switch.main"));
        Assert.Equal(new[] { "action.downloads" }, history.RecentIds);
        // The serialized form is ids and nothing else.
        Assert.DoesNotContain("secret", history.Serialize());
        Assert.DoesNotContain("work", history.Serialize());
    }

    [Fact]
    public void History_MostRecentFirst_Deduped_Capped()
    {
        var history = new ActionHistory();
        history.RecordId("action.settings");
        history.RecordId("action.downloads");
        history.RecordId("action.settings");
        Assert.Equal(new[] { "action.settings", "action.downloads" }, history.RecentIds);
        Assert.Equal(0, history.RankOf("action.settings"));
        Assert.Equal(-1, history.RankOf("action.help"));

        var big = new ActionHistory();
        foreach (var id in Enumerable.Range(1, 20).Select(i => $"action.a{i}"))
            big.RecordId(id);
        Assert.Equal(ActionHistory.MaxEntries, big.RecentIds.Count);
        Assert.Equal("action.a20", big.RecentIds[0]);
    }

    [Fact]
    public void History_RoundTrips_AndCorruptionIsEmpty()
    {
        var history = new ActionHistory();
        history.RecordId("action.help");
        history.RecordId("action.settings");
        var back = ActionHistory.Deserialize(history.Serialize());
        Assert.Equal(history.RecentIds, back.RecentIds);

        Assert.Empty(ActionHistory.Deserialize(null).RecentIds);
        Assert.Empty(ActionHistory.Deserialize("not json").RecentIds);
        Assert.Empty(ActionHistory.Deserialize("{\"x\":1}").RecentIds);
        // A hand-edited file cannot smuggle non-action ids in.
        var smuggled = ActionHistory.Deserialize("[\"action.help\",\"download.open.0\",\"profile.switch.work\"]");
        Assert.Equal(new[] { "action.help" }, smuggled.RecentIds);
    }

    [Fact]
    public void History_StoreRoundTripsThroughAFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "musedesktop-r1-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "palette-history.json");
        try
        {
            Assert.Empty(ActionHistoryStore.Load(path).RecentIds);
            Assert.True(ActionHistoryStore.Record(path, CommandOf("action.help")));
            Assert.False(ActionHistoryStore.Record(path, new PaletteCommand(
                "profile.switch.work", "x", null, "Profiles", "", PaletteCommandSource.Profile, Payload: "work")));
            Assert.Equal(new[] { "action.help" }, ActionHistoryStore.Load(path).RecentIds);
            File.WriteAllText(path, "garbage{");
            Assert.Empty(ActionHistoryStore.Load(path).RecentIds);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup is best-effort */ }
        }
    }

    // ──────────────────────── #103/#104 window MRU & tiling ────────────────────────

    private static WindowDescriptor Win(string id) =>
        new(id, $"Title {id}", "Default", null, IsMain: id == "main", SupportsFocusMode: false, IsActive: false);

    [Fact]
    public void Mru_TouchOrdersMostRecentFirst_UnseenKeepInputOrder()
    {
        var mru = new WindowMruList();
        mru.Touch("a");
        mru.Touch("b");
        mru.Touch("a");
        var sorted = mru.Sort(new[] { Win("a"), Win("b"), Win("c") });
        Assert.Equal(new[] { "a", "b", "c" }, sorted.Select(w => w.Id).ToArray());
        // Removing an id only forgets its MRU position — a window
        // that is still open sorts as "unseen", in input order.
        mru.Remove("a");
        Assert.Equal(new[] { "b", "a", "c" }, mru.Sort(new[] { Win("a"), Win("b"), Win("c") }).Select(w => w.Id).ToArray());
        // An MRU id with no living window prunes itself out.
        mru.Touch("ghost");
        Assert.Equal(new[] { "b", "a", "c" }, mru.Sort(new[] { Win("a"), Win("b"), Win("c") }).Select(w => w.Id).ToArray());
    }

    [Fact]
    public void Mru_PreviousIsTheWindowYouWereJustIn()
    {
        var mru = new WindowMruList();
        mru.Touch("main");
        mru.Touch("secondary-1");
        mru.Touch("main"); // current
        Assert.Equal("secondary-1", mru.Previous(new[] { Win("main"), Win("secondary-1") }, "main")?.Id);
        Assert.Null(mru.Previous(new[] { Win("main") }, "main"));
        Assert.Equal(0, WindowMruList.DefaultSelectionIndex(0));
        Assert.Equal(0, WindowMruList.DefaultSelectionIndex(1));
        Assert.Equal(1, WindowMruList.DefaultSelectionIndex(3));
    }

    [Fact]
    public void Tiling_HalvesAndMonitorCycle()
    {
        var area = new ScreenRect(0, 0, 1920, 1040);
        Assert.Equal(new ScreenRect(0, 0, 960, 1040), WindowTiling.Half(area, TileSide.Left));
        Assert.Equal(new ScreenRect(960, 0, 960, 1040), WindowTiling.Half(area, TileSide.Right));
        // Odd width: the left half takes the extra pixel, halves meet exactly.
        var odd = new ScreenRect(100, 50, 1919, 900);
        var left = WindowTiling.Half(odd, TileSide.Left);
        var right = WindowTiling.Half(odd, TileSide.Right);
        Assert.Equal(left.Right, right.Left);
        Assert.Equal(odd.Width, left.Width + right.Width);

        Assert.Equal(1, WindowTiling.NextIndex(0, 2));
        Assert.Equal(0, WindowTiling.NextIndex(1, 2));
        Assert.Equal(0, WindowTiling.NextIndex(5, 3));
        Assert.Equal(-1, WindowTiling.NextIndex(0, 0));
    }

    [Fact]
    public void Tiling_LargestOverlapPicksTheHomeMonitor()
    {
        var monitors = new[] { new ScreenRect(0, 0, 1920, 1080), new ScreenRect(1920, 0, 1920, 1080) };
        Assert.Equal(0, WindowTiling.IndexOfLargestOverlap(new ScreenRect(100, 100, 800, 600), monitors));
        Assert.Equal(1, WindowTiling.IndexOfLargestOverlap(new ScreenRect(2000, 100, 800, 600), monitors));
        Assert.Equal(-1, WindowTiling.IndexOfLargestOverlap(new ScreenRect(0, 5000, 100, 100), monitors));
        Assert.Equal(-1, WindowTiling.IndexOfLargestOverlap(new ScreenRect(0, 0, 10, 10), Array.Empty<ScreenRect>()));
    }

    // ──────────────────────── #105 profile chords ────────────────────────

    [Fact]
    public void Chords_MapOneToNine_AndStopThere()
    {
        for (var i = 0; i < 9; i++)
        {
            var gesture = ProfileChords.ForIndex(i);
            Assert.NotNull(gesture);
            Assert.Equal($"Ctrl+Shift+D{i + 1}", gesture!.Value.Format());
            Assert.True(ProfileChords.TryIndex(gesture.Value, out var back));
            Assert.Equal(i, back);
        }
        Assert.Null(ProfileChords.ForIndex(9));
        Assert.Null(ProfileChords.ForIndex(-1));
    }

    [Fact]
    public void Chords_RejectNonChords_AndCollideWithNothing()
    {
        Assert.False(ProfileChords.TryIndex(new ShortcutGesture(GestureModifiers.Control | GestureModifiers.Shift, "D"), out _));
        Assert.False(ProfileChords.TryIndex(new ShortcutGesture(GestureModifiers.Control, "D1"), out _));
        Assert.False(ProfileChords.TryIndex(new ShortcutGesture(GestureModifiers.Control | GestureModifiers.Shift, "D0"), out _));
        // No chord may equal any catalog gesture (fixed or remappable
        // default) — the shell checks remaps first, but a chord that
        // STARTS life colliding would be broken out of the box.
        var catalogGestures = new HashSet<ShortcutGesture>();
        foreach (var action in ShortcutCatalog.All)
            foreach (var text in action.Gestures)
                if (ShortcutGesture.TryParse(text, out var g))
                    catalogGestures.Add(g);
        for (var i = 0; i < 9; i++)
            Assert.DoesNotContain(ProfileChords.ForIndex(i)!.Value, catalogGestures);
    }

    [Fact]
    public void Chords_ResolveProfilesByPosition()
    {
        var profiles = new List<AccountProfile> { new(), WorkProfile, new() { Id = "play", Name = "Play" } };
        Assert.Equal("default", ProfileChords.ProfileIdAt(profiles, 0));
        Assert.Equal("work", ProfileChords.ProfileIdAt(profiles, 1));
        Assert.Null(ProfileChords.ProfileIdAt(profiles, 3));
        Assert.Equal(9, ProfileChords.ChordCount(32));
        Assert.Equal(2, ProfileChords.ChordCount(2));
    }

    // ──────────────────────── #107 palette text ────────────────────────

    [Fact]
    public void PaletteText_SummariesAndAnnouncements()
    {
        Assert.Equal("No commands available", PaletteText.ResultSummary(0, null));
        Assert.Equal("12 commands", PaletteText.ResultSummary(12, " "));
        Assert.Equal("No commands match 'xyz'", PaletteText.ResultSummary(0, "xyz"));
        Assert.Equal("3 commands match 'set'", PaletteText.ResultSummary(3, "set"));
        Assert.Equal("Open Settings, 1 of 12",
            PaletteText.SelectionAnnouncement(CommandOf("action.settings"), 0, 12));
    }

    // ──────────────────────── #109 the generated commands page ────────────────────────

    [Fact]
    public void CommandsHelp_ListsEveryCommand_AndIsDeterministic()
    {
        var listing = CommandRegistry.HelpListing();
        var text = CommandsHelp.Render(listing);
        Assert.All(listing, c => Assert.Contains(c.Title, text));
        Assert.Contains("Actions", text);
        Assert.Contains("Settings", text);
        Assert.Contains("Ctrl+,", text); // the Settings action's gesture survives rendering
        // Same set, different input order: identical page.
        var shuffled = listing.Reverse().ToList();
        Assert.Equal(text, CommandsHelp.Render(shuffled));
    }

    [Fact]
    public void CommandsHelp_TheCommandsPageIsInTheCatalog()
    {
        var page = HelpCatalog.ById(HelpCatalog.CommandsPageId);
        Assert.NotNull(page);
        Assert.Equal(HelpAction.None, page!.Action);
        Assert.Contains(HelpCatalog.Search("command palette"), p => p.Id == HelpCatalog.CommandsPageId);
    }
}
