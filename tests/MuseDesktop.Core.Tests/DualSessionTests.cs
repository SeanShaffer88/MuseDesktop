using System.Text.Json;
using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

public class DualSessionTests
{
    [Theory]
    [InlineData("https://muse.ai/chat/one?token=secret#fragment", "/chat/one")]
    [InlineData("https://muse.ai/", "/")]
    [InlineData("https://muse.ai/chat", "/chat")]
    [InlineData("https://muse.ai/settings", null)]
    [InlineData("https://accounts.google.com/callback?code=secret", null)]
    [InlineData("https://muse.ai/login?code=secret", null)]
    [InlineData("https://muse.ai.evil.test/chat/one", null)]
    [InlineData("https://person:password@muse.ai/chat/one", null)]
    [InlineData("http://muse.ai/chat/one", null)]
    [InlineData("https://muse.ai:8443/chat/one", null)]
    public void OnlySafeHomeAndChatRoutesAreRemembered(string url, string? expected)
    {
        Assert.Equal(expected, DualMode.RestorableRoute(url));
    }

    [Fact]
    public void ChatRoutesAreIndependentAndProfileScoped()
    {
        var sessions = new DualSessions();
        Assert.True(sessions.RememberChat("work", true, "https://muse.ai/chat/work-left"));
        Assert.True(sessions.RememberChat("work", false, "https://muse.ai/chat/work-right"));
        Assert.True(sessions.RememberChat("personal", true, "https://muse.ai/chat/personal"));
        Assert.Equal("/chat/work-left", sessions.Find("work")!.LeftRoute);
        Assert.Equal("/chat/work-right", sessions.Find("work")!.RightRoute);
        Assert.Equal("/chat/personal", sessions.Find("personal")!.LeftRoute);
        Assert.Equal("/", sessions.Find("personal")!.RightRoute);
        Assert.Null(sessions.Find("missing"));
    }

    [Fact]
    public void FailedAuthAndUnchangedNavigationsDoNotOverwriteTheSavedChat()
    {
        var sessions = new DualSessions();
        sessions.RememberChat("work", true, "https://muse.ai/chat/last-good");
        Assert.False(sessions.RememberChat("work", true, "https://muse.ai/login?code=private"));
        Assert.False(sessions.RememberChat("work", true, "https://muse.ai/chat/last-good?token=ignored"));
        Assert.Equal("/chat/last-good", sessions.Find("work")!.LeftRoute);
        Assert.False(sessions.RememberChat("../invalid", true, "https://muse.ai/chat/one"));
    }

    [Theory]
    [InlineData(0.65, 0.65)]
    [InlineData(0, 0.2)]
    [InlineData(1, 0.8)]
    [InlineData(double.NaN, 0.5)]
    [InlineData(double.PositiveInfinity, 0.5)]
    public void DividerFractionsStaySafe(double input, double expected) =>
        Assert.Equal(expected, DualSession.CleanFraction(input));

    [Theory]
    [InlineData(1.5, 1.5)]
    [InlineData(-2, 1)]
    [InlineData(99, 5)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    public void ZoomValuesStaySafe(double input, double expected) =>
        Assert.Equal(expected, DualSession.CleanZoom(input));

    [Fact]
    public void TamperedStateCannotRestoreAnExternalPageOrToken()
    {
        var sessions = new DualSessions
        {
            ByProfile = new()
            {
                ["work"] = new()
                {
                    LeftRoute = "/chat/one?secret=removed#also-removed",
                    RightRoute = "//evil.test/chat/one",
                    LeftFraction = -100,
                },
                ["../bad"] = new(),
                ["empty"] = null!,
            },
        };
        sessions.Sanitize();
        Assert.Single(sessions.ByProfile);
        Assert.Equal("/chat/one", sessions.Find("work")!.LeftRoute);
        Assert.Equal("/", sessions.Find("work")!.RightRoute);
        Assert.Equal(0.2, sessions.Find("work")!.LeftFraction);
    }

    [Fact]
    public void StoreRoundTripsBothChatsDividerAndIndependentZoomWithoutQueries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "muse-dual-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "dual-sessions.json");
        try
        {
            var sessions = new DualSessions();
            sessions.RememberChat("work", true, "https://muse.ai/chat/one?secret=removed#fragment");
            sessions.RememberChat("work", false, "https://muse.ai/chat/two");
            var state = sessions.ForProfile("work");
            state.LeftFraction = 0.65;
            state.LeftZoom = 1.5;
            state.RightZoom = 0.75;
            DualSessionStore.Save(path, sessions);
            var loaded = DualSessionStore.Load(path).Find("work")!;
            Assert.Equal("/chat/one", loaded.LeftRoute);
            Assert.Equal("/chat/two", loaded.RightRoute);
            Assert.Equal(0.65, loaded.LeftFraction);
            Assert.Equal(1.5, loaded.LeftZoom);
            Assert.Equal(0.75, loaded.RightZoom);
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("secret", json);
            Assert.DoesNotContain("fragment", json);
            Assert.DoesNotContain("https://", json);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CorruptFilesArePreservedAndMissingFilesStartEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "muse-dual-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "dual-sessions.json");
        try
        {
            Assert.Empty(DualSessionStore.Load(path).ByProfile);
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{ broken");
            Assert.Empty(DualSessionStore.Load(path).ByProfile);
            Assert.False(File.Exists(path));
            Assert.Equal("{ broken", File.ReadAllText(Assert.Single(Directory.GetFiles(dir, "*.corrupt-*"))));
            File.WriteAllText(path, JsonSerializer.Serialize(new { byProfile = (object?)null }));
            Assert.Empty(DualSessionStore.Load(path).ByProfile);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
