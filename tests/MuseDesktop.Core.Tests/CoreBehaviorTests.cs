using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

public class CoreBehaviorTests
{
    [Theory]
    [InlineData(1.0, 1.10)]
    [InlineData(1.10, 1.25)]
    [InlineData(2.0, 2.0)]
    [InlineData(0.95, 1.0)]
    public void Zoom_StepUp(double from, double to) =>
        Assert.Equal(to, ZoomMath.StepUp(from), precision: 3);

    [Theory]
    [InlineData(1.0, 0.90)]
    [InlineData(0.50, 0.50)]
    [InlineData(1.05, 1.0)]
    public void Zoom_StepDown(double from, double to) =>
        Assert.Equal(to, ZoomMath.StepDown(from), precision: 3);

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-3.0, 1.0)]
    [InlineData(99.0, 2.0)]
    [InlineData(0.01, 0.50)]
    [InlineData(1.25, 1.25)]
    public void Zoom_Clamp(double input, double expected) =>
        Assert.Equal(expected, ZoomMath.Clamp(input), precision: 3);

    [Fact]
    public void Zoom_Format() => Assert.Equal("67%", ZoomMath.FormatPercent(0.67));

    [Fact]
    public void Settings_Roundtrip()
    {
        var dir = Directory.CreateTempSubdirectory("musedesktop-tests");
        try
        {
            var path = Path.Combine(dir.FullName, "nested", "settings.json");
            var saved = new AppSettings
            {
                WindowWidth = 1500,
                WindowHeight = 900,
                WindowMaximized = true,
                ZoomFactor = 1.25,
                CloseToTray = true,
                LastUrl = "https://muse.ai/chat/xyz",
            };
            SettingsStore.Save(path, saved);
            Assert.False(File.Exists(path + ".tmp"), "atomic save must not leave the temp file behind");

            var loaded = SettingsStore.Load(path);
            Assert.Equal(1500, loaded.WindowWidth);
            Assert.Equal(900, loaded.WindowHeight);
            Assert.True(loaded.WindowMaximized);
            Assert.Equal(1.25, loaded.ZoomFactor);
            Assert.True(loaded.CloseToTray);
            Assert.Equal("https://muse.ai/chat/xyz", loaded.LastUrl);
            Assert.Equal("https://muse.ai/chat/xyz", loaded.StartupUrl);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Settings_MissingFile_GivesDefaults() =>
        Assert.Equal(UrlPolicy.HomeUrl, SettingsStore.Load(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")).StartupUrl);

    [Fact]
    public void Settings_CorruptFile_GivesDefaultsAndKeepsACopy()
    {
        var dir = Directory.CreateTempSubdirectory("musedesktop-tests");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path, "{ this is not json !!");
            var loaded = SettingsStore.Load(path);
            Assert.Equal(1280, loaded.WindowWidth);
            Assert.Single(Directory.GetFiles(dir.FullName, "settings.json.corrupt-*"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Settings_Sanitized_DropsForeignLastUrlAndBadSizes()
    {
        var s = new AppSettings
        {
            WindowWidth = 10,
            WindowHeight = 99999,
            ZoomFactor = 42,
            LastUrl = "https://example.com/phish",
        }.Sanitized();
        Assert.Equal(1280, s.WindowWidth);
        Assert.Equal(860, s.WindowHeight);
        Assert.Equal(2.0, s.ZoomFactor);
        Assert.Null(s.LastUrl);
    }

    [Fact]
    public void Log_Sanitize_KeepsOneBoundedLine()
    {
        var clean = DiagnosticLog.Sanitize("line1\nline2\r\n" + new string('x', 900));
        Assert.DoesNotContain("\n", clean);
        Assert.DoesNotContain("\r", clean);
        Assert.Equal(500, clean.Length);
    }

    [Fact]
    public void Log_WritesTimestampedLines()
    {
        var dir = Directory.CreateTempSubdirectory("musedesktop-tests");
        try
        {
            var log = new DiagnosticLog(Path.Combine(dir.FullName, "logs", "app.log"));
            log.Info("started https://muse.ai/ host only");
            var text = File.ReadAllText(log.Path);
            Assert.Contains("[INFO] started", text);
        }
        finally { dir.Delete(recursive: true); }
    }

    // ---- v0.2.0: settings defaults + sanitize ----

    [Fact]
    public void Settings_V020Defaults()
    {
        var s = new AppSettings();
        Assert.True(s.CloseToTray);          // default since v0.1.3
        Assert.True(s.CheckForUpdates);
        Assert.True(s.SuspendWhenInTray);
        Assert.False(s.AskWhereToSave);
        Assert.Null(s.DownloadsFolder);
    }

    [Fact]
    public void Settings_Sanitized_NormalizesDownloadsFolder()
    {
        Assert.Null(new AppSettings { DownloadsFolder = "   " }.Sanitized().DownloadsFolder);
        Assert.Equal(@"C:\Data\Dl", new AppSettings { DownloadsFolder = "  C:\\Data\\Dl  " }.Sanitized().DownloadsFolder);
    }

    [Fact]
    public void Settings_Roundtrip_V020Fields()
    {
        var dir = Directory.CreateTempSubdirectory("musedesktop-tests");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            SettingsStore.Save(path, new AppSettings
            {
                DownloadsFolder = @"D:\Muse downloads",
                AskWhereToSave = true,
                CheckForUpdates = false,
                SuspendWhenInTray = false,
            });
            var loaded = SettingsStore.Load(path);
            Assert.Equal(@"D:\Muse downloads", loaded.DownloadsFolder);
            Assert.True(loaded.AskWhereToSave);
            Assert.False(loaded.CheckForUpdates);
            Assert.False(loaded.SuspendWhenInTray);
        }
        finally { dir.Delete(recursive: true); }
    }

    // ---- v0.2.0: update logic ----

    [Theory]
    [InlineData("v0.2.0", "0.1.3", true)]
    [InlineData("v0.1.3", "0.1.3", false)]
    [InlineData("v0.1.2", "0.1.3", false)]
    [InlineData("0.3.0", "0.2.0", true)]
    [InlineData("v1.0.0", "0.9.9", true)]
    [InlineData("v0.2.0-beta", "0.1.3", false)]  // pre-release tags never count
    [InlineData("garbage", "0.1.3", false)]
    [InlineData("", "0.1.3", false)]
    [InlineData(null, "0.1.3", false)]
    public void Update_IsNewer(string? tag, string current, bool expected) =>
        Assert.Equal(expected, UpdateInfo.IsNewer(tag, Version.Parse(current)));

    [Fact]
    public void Update_ParseLatestReleaseJson()
    {
        var json = """{"tag_name":"v0.2.0","html_url":"https://github.com/SeanShaffer88/MuseDesktop/releases/tag/v0.2.0","name":"x"}""";
        var parsed = UpdateInfo.ParseLatestReleaseJson(json);
        Assert.NotNull(parsed);
        Assert.Equal("v0.2.0", parsed!.Value.Tag);
        Assert.Contains("/releases/tag/v0.2.0", parsed.Value.Url);
        Assert.Null(UpdateInfo.ParseLatestReleaseJson("{}"));
        Assert.Null(UpdateInfo.ParseLatestReleaseJson("not json"));
    }
}
