using System.Text.Json;
using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

public class DualWorkbenchTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MuseDesktop.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Missing repository.");
    }
    private static string Source(string file) => File.ReadAllText(Path.Combine(Root(), file));
    private static string Workbench => Source("src/MuseDesktop/DualWindow.Workbench.cs");

    [Fact]
    public void TwentyRequestedFeaturesHaveDistinctStableIds()
    {
        Assert.Equal(20, DualWorkbench.Features.Count);
        Assert.Equal(20, DualWorkbench.Features.Distinct().Count());
        Assert.Contains("downloads", DualWorkbench.Features);
        Assert.Contains("accessibility", DualWorkbench.Features);
    }

    [Theory]
    [InlineData("https://muse.ai/", true)]
    [InlineData("https://muse.ai/chat/one", true)]
    [InlineData("https://muse.ai/login", false)]
    [InlineData("https://accounts.google.com/callback", false)]
    [InlineData("https://muse.ai.evil.test/chat/one", false)]
    [InlineData("https://me:secret@muse.ai/chat/one", false)]
    [InlineData("https://muse.ai:8443/chat/one", false)]
    [InlineData("file:///C:/private.txt", false)]
    public void OptionalToolsInspectOnlyTrustedMuseRoutes(string url, bool expected) =>
        Assert.Equal(expected, DualWorkbench.CanInspect(url));

    [Fact]
    public void SavedPairsKeepSeparateProfilesAndSanitizePrivateOrExternalRoutes()
    {
        var pair = new DualPair
        {
            Name = "  Research\nPair  ", LeftProfileId = "work", RightProfileId = "personal",
            LeftRoute = "/chat/one?token=secret#fragment", RightRoute = "//evil.test/private",
            LeftZoom = 100, RightZoom = double.NaN,
        };
        pair.Sanitize();
        Assert.Equal("ResearchPair", pair.Name);
        Assert.Equal("work", pair.LeftProfileId);
        Assert.Equal("personal", pair.RightProfileId);
        Assert.Equal("/chat/one", pair.LeftRoute);
        Assert.Equal("/", pair.RightRoute);
        Assert.Equal(5, pair.LeftZoom);
        Assert.Equal(1, pair.RightZoom);
    }

    [Fact]
    public void TamperedPairsCannotTraverseProfileFolders()
    {
        var pair = new DualPair { LeftProfileId = "../bad", RightProfileId = "C:\\bad" };
        pair.Sanitize();
        Assert.Equal(Profiles.DefaultId, pair.LeftProfileId);
        Assert.Equal(Profiles.DefaultId, pair.RightProfileId);
    }

    [Fact]
    public void SessionCapsNotesPairsNamesAndWindowGeometry()
    {
        var state = new DualSession
        {
            Scratchpad = new string('n', 50000),
            Pairs = Enumerable.Range(0, 30).Select(i => new DualPair { Name = i + new string('x', 100) }).ToList(),
            LeftProfileId = "../bad", WindowLeft = double.PositiveInfinity,
            WindowTop = double.NaN, WindowWidth = -1, WindowHeight = 1e15,
        };
        state.Sanitize();
        Assert.Equal(32000, state.Scratchpad.Length);
        Assert.Equal(20, state.Pairs.Count);
        Assert.All(state.Pairs, p => Assert.Equal(64, p.Name.Length));
        Assert.Null(state.LeftProfileId);
        Assert.Equal(0, state.WindowLeft);
        Assert.Equal(0, state.WindowTop);
        Assert.Equal(840, state.WindowWidth);
        Assert.Equal(10000, state.WindowHeight);
    }

    [Fact]
    public void DuplicateOrEmptyNamesAreRemovedWithoutDeletingDistinctPairs()
    {
        var state = new DualSession { Pairs = new() {
            new() { Name = "Work" }, new() { Name = "work" }, new() { Name = "" }, new() { Name = "Personal" },
        }};
        state.Sanitize();
        Assert.Equal(new[] { "Work", "Personal" }, state.Pairs.Select(p => p.Name));
    }

    [Fact]
    public void WorkspaceRoundTripsWhileOldStateDoesNotOptIntoAutomaticOpening()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dual-workbench-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "sessions.json");
        try
        {
            var sessions = new DualSessions();
            var state = sessions.ForProfile("work");
            state.LeftProfileId = "work"; state.RightProfileId = "personal";
            state.Vertical = state.Swapped = state.RestoreOnLaunch = state.WasOpen = true;
            state.Scratchpad = "Explicit local notes";
            state.WindowLeft = -1200; state.WindowTop = 90;
            state.Pairs.Add(new DualPair { Name = "Pair", LeftProfileId = "work", RightProfileId = "personal" });
            DualSessionStore.Save(path, sessions);
            var loaded = DualSessionStore.Load(path).Find("work")!;
            Assert.True(loaded.RestoreOnLaunch && loaded.WasOpen && loaded.Vertical && loaded.Swapped);
            Assert.Equal("personal", loaded.RightProfileId);
            Assert.Equal(-1200, loaded.WindowLeft);
            Assert.Equal(90, loaded.WindowTop);
            Assert.Equal("Explicit local notes", loaded.Scratchpad);
            Assert.Single(loaded.Pairs);
            File.WriteAllText(path, "{\"byProfile\":{\"work\":{\"leftRoute\":\"/chat/old\"}}}");
            loaded = DualSessionStore.Load(path).Find("work")!;
            Assert.Equal("/chat/old", loaded.LeftRoute);
            Assert.False(loaded.RestoreOnLaunch);
            Assert.False(loaded.WasOpen);
            Assert.Empty(loaded.Scratchpad);
            Assert.Empty(loaded.Pairs);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReplyIndicatorsRequireARecognizedStreamingTransition()
    {
        var tracker = new DualReplyTracker();
        Assert.False(tracker.Observe(false, true)); // Idle is not "finished".
        Assert.False(tracker.Observe(true, true));
        Assert.True(tracker.Observe(false, true));
        Assert.False(tracker.Observe(false, true)); // No repeated notification.
        Assert.False(tracker.Observe(true, false));
        Assert.False(tracker.Observe(false, false)); // Visible/active completion.
        tracker.Observe(true, true);
        tracker.Reset();
        Assert.False(tracker.Observe(false, true)); // Navigation/privacy resets it.
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    [InlineData(0.45, 0.45)]
    public void ScrollMessagesAreFiniteAndClamped(double input, double expected)
    {
        Assert.Equal(expected, DualWorkbench.ScrollFraction(input));
        var script = DualToolsScripts.ApplyScroll(input);
        Assert.DoesNotContain("NaN", script);
        Assert.DoesNotContain("Infinity", script);
    }

    [Fact]
    public void SelectionTransferEscapesTextCapsLengthAndNeverSends()
    {
        var text = "\"; window.attack(); // <script>\n\\";
        var script = DualToolsScripts.InsertSelection(text);
        Assert.Contains(JsonSerializer.Serialize(text), script);
        Assert.Contains("boxes.length !== 1", script);
        Assert.Contains("old + (old ?", script); // Append, not replacement.
        Assert.Contains(JsonSerializer.Serialize(new string('x', 8000)),
            DualToolsScripts.InsertSelection(new string('x', 9000)));
        Assert.DoesNotContain(".click(", script);
        Assert.DoesNotContain(".submit(", script);
        Assert.DoesNotContain("requestSubmit", script);
        Assert.DoesNotContain("keydown", script);
    }

    [Fact]
    public void SwapExpandAndDetachDoNotNavigateOrRecreateLiveChats()
    {
        var source = Workbench;
        var swap = source[source.IndexOf("Tool(\"dual.swap\"")..source.IndexOf("Tool(\"dual.orientation\"")];
        Assert.Contains("_session.Swapped", swap);
        Assert.DoesNotContain("Navigate", swap);
        Assert.DoesNotContain("StartProfile", swap);
        var detach = source[source.IndexOf("private void DetachPane(")..source.IndexOf("private void ApplyPrivacy(")];
        Assert.Contains("host.Children.Add(pane.Browser)", detach);
        Assert.Contains("pane.Host.Children.Add(live)", detach);
        Assert.DoesNotContain("new WebView2", detach);
        Assert.DoesNotContain("Navigate", detach);
        Assert.Contains("Owner = this", detach);
    }

    [Fact]
    public void ProfilesPrivacyAndScriptsHaveExplicitSafetyGates()
    {
        var source = Workbench;
        Assert.Contains("ProfileLock.IsLockActive", source);
        Assert.Contains("window.Unlocked", source);
        Assert.Contains("ProfileThrottles", source);
        Assert.Contains("Visibility.Hidden", source); // Native HWND, not just a WPF overlay.
        Assert.Contains("pane.Covered", source);
        Assert.Contains("location.origin !== 'https://muse.ai'", source);
        Assert.Contains("generation == pane.Generation", source);
        Assert.Contains("core.Source != destination", source);
        Assert.Contains("prompt.ShowDialog() != true", source);
        Assert.DoesNotContain("BuildSendScript", source);
        Assert.DoesNotContain("AddScriptToExecuteOnDocumentCreated", source);
    }

    [Fact]
    public void DownloadsReuseSafetyPoliciesWithoutCrossAccountHiddenRetries()
    {
        var main = Source("src/MuseDesktop/MainWindow.xaml.cs");
        Assert.Contains("HandleDownloadStarting(e, folder", main);
        Assert.Contains("allowQuietQueue: profileId == _settings.ActiveProfileId", main);
        Assert.Contains("_dualNonActiveDownloadUris.Contains(uri)", main);
        var dual = Source("src/MuseDesktop/DualWindow.xaml.cs");
        Assert.Contains("_download(pane.ProfileId, e)", dual);
        Assert.Contains("generation != pane.Generation || pane.Covered", dual);
    }

    [Fact]
    public void AllTwentyFeaturesHaveConcreteShellEntryPoints()
    {
        foreach (var entry in new[] { "dual.swap", "ProfilePicker", "SavePair", "dual.orientation",
            "ExpandPane", "DetachPane", "SetActive", "HandleWorkbenchKey", "DocumentTitleChanged",
            "ContextMenuRequested", "TransferSelectionAsync", "OnScratchpadChanged", "PollRepliesAsync",
            "FindBothAsync", "DualToolsScripts.StartScroll", "RecoverPaneAsync", "RestoreOnLaunch",
            "ApplyPrivacy", "AutomationEvents.LiveRegionChanged" })
            Assert.Contains(entry, Workbench);
        Assert.Contains("_download(pane.ProfileId, e)", Source("src/MuseDesktop/DualWindow.xaml.cs"));
    }

    [Fact]
    public void ScratchpadIsExplicitSeparateFromComposerAndSettingsExports()
    {
        Assert.DoesNotContain(typeof(AppSettings).GetProperties(), p => p.Name.Contains("Scratchpad"));
        Assert.Contains("Scratchpad.Text", Workbench);
        Assert.Contains("_notesTimer.Stop()", Workbench);
        Assert.Contains("PlacementClamp.Clamp", Workbench);
        Assert.Contains("RestoreBounds", Workbench);
        Assert.Contains("OnOwnerClosing", Workbench);
        var main = Source("src/MuseDesktop/MainWindow.xaml.cs");
        Assert.Contains("session is { RestoreOnLaunch: true, WasOpen: true }", main);
        Assert.Contains("_safeMode || !_webReady || !IsVisible", main);
    }
}
