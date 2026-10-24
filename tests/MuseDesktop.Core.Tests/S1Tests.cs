using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>v0.5.0 Slice 1 (items 1–12) Core additions: navigation history,
/// load-failure classification, window title, per-profile placement,
/// always-on-top persistence, and the new-window policy audit (#11).</summary>
public class S1Tests
{
    // ── #3 navigation history ──

    [Fact]
    public void History_RecordsAndDedupesConsecutive()
    {
        var h = new NavigationHistory();
        h.Record("https://muse.ai/a");
        h.Record("https://muse.ai/a");
        h.Record("https://muse.ai/b");
        Assert.Equal(2, h.Entries.Count);
        Assert.Equal("https://muse.ai/b", h.Current!.Url);
    }

    [Fact]
    public void History_BackForwardAndDropdowns()
    {
        var h = new NavigationHistory();
        h.Record("https://muse.ai/1");
        h.Record("https://muse.ai/2");
        h.Record("https://muse.ai/3");
        Assert.Equal("https://muse.ai/2", h.GoBack()!.Url);
        Assert.Single(h.BackEntries());
        Assert.Equal("https://muse.ai/1", h.BackEntries()[0].Url);
        Assert.Single(h.ForwardEntries());
        Assert.Equal("https://muse.ai/3", h.GoForward()!.Url);
        Assert.Null(h.GoForward());
    }

    [Fact]
    public void History_FreshNavigationDropsForwardEntries()
    {
        var h = new NavigationHistory();
        h.Record("https://muse.ai/1");
        h.Record("https://muse.ai/2");
        h.GoBack();
        h.Record("https://muse.ai/9");
        Assert.Empty(h.ForwardEntries());
        Assert.Equal("https://muse.ai/9", h.Current!.Url);
    }

    [Fact]
    public void History_CapsAtMaxEntries()
    {
        var h = new NavigationHistory();
        for (var i = 0; i < NavigationHistory.MaxEntries + 10; i++)
            h.Record($"https://muse.ai/{i}");
        Assert.Equal(NavigationHistory.MaxEntries, h.Entries.Count);
        Assert.Equal($"https://muse.ai/{NavigationHistory.MaxEntries + 9}", h.Current!.Url);
    }

    [Fact]
    public void History_JumpToMovesPointer()
    {
        var h = new NavigationHistory();
        h.Record("https://muse.ai/1");
        h.Record("https://muse.ai/2");
        h.Record("https://muse.ai/3");
        var target = h.Entries[0];
        Assert.Equal("https://muse.ai/1", h.JumpTo(target)!.Url);
        Assert.Equal(2, h.ForwardEntries().Count);
    }

    // ── #9 recently closed page ──

    [Fact]
    public void History_LastClosedPage_TracksNavigationAwayAndCrashNote()
    {
        var h = new NavigationHistory();
        h.Record("https://muse.ai/a");
        Assert.Null(h.LastClosedPageUrl);
        h.Record("https://muse.ai/b");
        Assert.Equal("https://muse.ai/a", h.LastClosedPageUrl);
        h.NoteClosedPage("https://muse.ai/crashed");
        Assert.Equal("https://muse.ai/crashed", h.LastClosedPageUrl);
    }

    // ── #10 load-failure classification ──

    [Theory]
    [InlineData("Disconnected", LoadFailureCategory.Offline)]
    [InlineData("CannotConnect", LoadFailureCategory.Offline)]
    [InlineData("ConnectionReset", LoadFailureCategory.Offline)]
    [InlineData("ServerUnreachable", LoadFailureCategory.HostUnreachable)]
    [InlineData("Timeout", LoadFailureCategory.HostUnreachable)]
    [InlineData("HostNameNotResolved", LoadFailureCategory.DnsFailure)]
    [InlineData("CertificateExpired", LoadFailureCategory.Certificate)]
    [InlineData("CertificateIsInvalid", LoadFailureCategory.Certificate)]
    [InlineData("Unknown", LoadFailureCategory.Other)]
    [InlineData(null, LoadFailureCategory.Other)]
    public void LoadFailure_Classifies(string? status, LoadFailureCategory expected) =>
        Assert.Equal(expected, LoadFailureClassifier.Classify(status));

    [Fact]
    public void LoadFailure_MessagesArePlainAndDistinct()
    {
        Assert.Contains("offline", LoadFailureClassifier.MessageFor("Disconnected"));
        Assert.Contains("DNS", LoadFailureClassifier.MessageFor("HostNameNotResolved"));
        Assert.Contains("certificate", LoadFailureClassifier.MessageFor("CertificateExpired"));
    }

    // ── #12 window title ──

    [Fact]
    public void WindowTitle_SingleProfile()
    {
        Assert.Equal("My chat — MuseDesktop", WindowTitle.Compose("My chat", "Default", false));
        Assert.Equal("Muse — MuseDesktop", WindowTitle.Compose("", "Default", false));
        Assert.Equal("Muse — MuseDesktop", WindowTitle.Compose(null, null, false));
    }

    [Fact]
    public void WindowTitle_MultipleProfilesIncludesProfileName()
    {
        Assert.Equal("My chat — Work — MuseDesktop", WindowTitle.Compose("My chat", "Work", true));
    }

    // ── #5 always-on-top ──

    [Fact]
    public void AlwaysOnTop_DefaultsFalseAndRoundTrips()
    {
        Assert.False(new AppSettings().AlwaysOnTop);
        var json = SettingsStore.Export(new AppSettings { AlwaysOnTop = true }.Sanitized());
        Assert.True(SettingsStore.TryImport(json, out var imported, out _));
        Assert.True(imported!.AlwaysOnTop);
    }

    // ── #8 per-profile placement ──

    [Fact]
    public void Placement_MigratesLegacyGlobalIntoDefaultProfile()
    {
        var s = new AppSettings { WindowLeft = 10, WindowTop = 20, WindowWidth = 900, WindowHeight = 700, WindowMaximized = true };
        s.Sanitized();
        var p = s.WindowPlacementByProfile[Profiles.DefaultId];
        Assert.Equal(10, p.Left);
        Assert.Equal(20, p.Top);
        Assert.Equal(900, p.Width);
        Assert.Equal(700, p.Height);
        Assert.True(p.Maximized);
    }

    [Fact]
    public void Placement_IsolatedPerProfile()
    {
        var s = new AppSettings();
        s.Profiles.Add(new AccountProfile { Id = "work", Name = "Work" });
        s.ActiveProfileId = Profiles.DefaultId;
        s.Sanitized();
        s.SetActivePlacement(new WindowPlacement { Left = 1, Top = 2, Width = 800, Height = 600 });
        s.ActiveProfileId = "work";
        // Work has no placement yet: falls back to the (mirrored) global values, not Default's entry object.
        var workPlacement = s.ActivePlacement;
        Assert.Equal(800, workPlacement.Width);
        s.SetActivePlacement(new WindowPlacement { Left = 50, Top = 60, Width = 1000, Height = 800 });
        s.ActiveProfileId = Profiles.DefaultId;
        Assert.Equal(1, s.ActivePlacement.Left);
        Assert.Equal(800, s.ActivePlacement.Width);
        s.ActiveProfileId = "work";
        Assert.Equal(50, s.ActivePlacement.Left);
        Assert.Equal(1000, s.ActivePlacement.Width);
    }

    [Fact]
    public void Placement_DropsUnknownProfilesAndClampsSizes()
    {
        var s = new AppSettings();
        s.WindowPlacementByProfile["ghost"] = new WindowPlacement { Width = 10, Height = 99999 };
        s.Sanitized();
        Assert.False(s.WindowPlacementByProfile.ContainsKey("ghost"));
        var p = s.WindowPlacementByProfile[Profiles.DefaultId];
        Assert.InRange(p.Width, 480, 10000);
    }

    [Fact]
    public void Placement_RoundTripsThroughExportImport()
    {
        var s = new AppSettings();
        s.Sanitized();
        s.SetActivePlacement(new WindowPlacement { Left = 33, Top = 44, Width = 1111, Height = 777, Maximized = true });
        var json = SettingsStore.Export(s);
        Assert.True(SettingsStore.TryImport(json, out var imported, out _));
        Assert.Equal(33, imported!.ActivePlacement.Left);
        Assert.Equal(1111, imported.ActivePlacement.Width);
        Assert.True(imported.ActivePlacement.Maximized);
    }

    // ── #11 middle / Ctrl+click audit ──
    // Middle-click and Ctrl+click on a link surface in WebView2 exactly as a
    // NewWindowRequested event; the shell routes those through
    // UrlPolicy.ClassifyNewWindow. These tests pin that classification for
    // every host class so the audit conclusion stays tested, not asserted.

    [Theory]
    [InlineData("https://muse.ai/chat/1", LinkDisposition.InApp)]
    [InlineData("https://chat.muse.ai/x", LinkDisposition.InApp)]
    [InlineData("https://accounts.google.com/signin", LinkDisposition.InApp)]
    [InlineData("https://login.microsoftonline.com/x", LinkDisposition.InApp)]
    [InlineData("https://example.com/page", LinkDisposition.ExternalBrowser)]
    [InlineData("http://example.com/page", LinkDisposition.ExternalBrowser)]
    [InlineData("https://muse.ai.evil.com/phish", LinkDisposition.ExternalBrowser)]
    [InlineData("https://evilmuse.ai/phish", LinkDisposition.ExternalBrowser)]
    [InlineData("javascript:alert(1)", LinkDisposition.Block)]
    [InlineData("file:///C:/secret.txt", LinkDisposition.Block)]
    public void NewWindowPolicy_CoversMiddleAndCtrlClickHosts(string url, LinkDisposition expected) =>
        Assert.Equal(expected, UrlPolicy.ClassifyNewWindow(url));
}
