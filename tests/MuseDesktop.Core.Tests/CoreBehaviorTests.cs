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
}
