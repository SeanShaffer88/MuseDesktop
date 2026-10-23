using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>v0.4.0 Core additions: download history, download status text,
/// per-profile startup choice, and settings export/import.</summary>
public class V040Tests
{
    // ── download history ──

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"musedesktop-history-{Guid.NewGuid():N}.json");

    private static DownloadRecord Rec(string name, int minutesAgo, long size = 1024) => new()
    {
        FileName = name,
        FullPath = $@"C:\Downloads\{name}",
        SizeBytes = size,
        CompletedUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
    };

    [Fact]
    public void History_RoundTrips_NewestFirst()
    {
        var path = TempPath();
        try
        {
            DownloadHistoryStore.Add(path, Rec("old.zip", 30));
            DownloadHistoryStore.Add(path, Rec("new.zip", 1));
            var loaded = DownloadHistoryStore.Load(path);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("new.zip", loaded[0].FileName);
            Assert.Equal("old.zip", loaded[1].FileName);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void History_CapsAtMaxEntries()
    {
        var path = TempPath();
        try
        {
            var many = Enumerable.Range(0, DownloadHistoryStore.MaxEntries + 10)
                .Select(i => Rec($"f{i}.zip", i))
                .ToList();
            DownloadHistoryStore.Save(path, many);
            Assert.Equal(DownloadHistoryStore.MaxEntries, DownloadHistoryStore.Load(path).Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void History_MissingOrCorruptFile_LoadsEmpty()
    {
        var path = TempPath();
        Assert.Empty(DownloadHistoryStore.Load(path));
        File.WriteAllText(path, "{ not json");
        try { Assert.Empty(DownloadHistoryStore.Load(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void History_Clear_Empties()
    {
        var path = TempPath();
        try
        {
            DownloadHistoryStore.Add(path, Rec("a.zip", 0));
            DownloadHistoryStore.Clear(path);
            Assert.Empty(DownloadHistoryStore.Load(path));
        }
        finally { File.Delete(path); }
    }

    // ── download text ──

    [Theory]
    [InlineData(0, "1 sec")]
    [InlineData(30, "30 sec")]
    [InlineData(59, "59 sec")]
    [InlineData(61, "2 min")]
    [InlineData(600, "10 min")]
    public void FormatEta_MatchesTheAppsWording(double seconds, string expected) =>
        Assert.Equal(expected, DownloadText.FormatEta(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(11534336, "11 MB")]
    [InlineData(1610612736, "1.5 GB")]
    public void FormatBytes_UsesBinaryUnits(long bytes, string expected) =>
        Assert.Equal(expected, DownloadText.FormatBytes(bytes));

    [Fact]
    public void ProgressStatus_WithTotalAndEta()
    {
        var now = DateTimeOffset.UtcNow;
        var (text, right) = DownloadText.ProgressStatus("report.pdf", 512 * 1024, 2048 * 1024, now.AddSeconds(90), now);
        Assert.Equal("Downloading report.pdf — 25%, about 2 min left", text);
        Assert.Equal("512 KB of 2048 KB", right);
    }

    [Fact]
    public void ProgressStatus_UnknownTotal()
    {
        var now = DateTimeOffset.UtcNow;
        var (text, right) = DownloadText.ProgressStatus("report.pdf", 2048, null, null, now);
        Assert.Equal("Downloading report.pdf…", text);
        Assert.Equal("2 KB received", right);
    }

    // ── per-profile startup ──

    [Fact]
    public void StartupUrl_StartAtHomeProfile_IgnoresLastPage()
    {
        var settings = new AppSettings();
        settings.LastUrlByProfile[Profiles.DefaultId] = "https://muse.ai/chat/abc";
        settings.Profiles[0].StartAtHome = true;
        Assert.Equal(UrlPolicy.HomeUrl, settings.Sanitized().StartupUrl);
    }

    [Fact]
    public void StartupUrl_DefaultProfile_RestoresLastPage()
    {
        var settings = new AppSettings();
        settings.LastUrlByProfile[Profiles.DefaultId] = "https://muse.ai/chat/abc";
        Assert.Equal("https://muse.ai/chat/abc", settings.Sanitized().StartupUrl);
    }

    // ── settings export / import ──

    [Fact]
    public void ExportImport_RoundTrips()
    {
        var settings = new AppSettings { ZoomFactor = 1.5, DownloadsFolder = @"D:\Files", CloseToTray = false };
        settings.Profiles[0].StartAtHome = true;
        var json = SettingsStore.Export(settings.Sanitized());
        Assert.True(SettingsStore.TryImport(json, out var imported, out var error));
        Assert.Null(error);
        Assert.NotNull(imported);
        Assert.Equal(1.5, imported!.ZoomFactor);
        Assert.Equal(@"D:\Files", imported.DownloadsFolder);
        Assert.False(imported.CloseToTray);
        Assert.True(imported.Profiles[0].StartAtHome);
    }

    [Fact]
    public void Import_Garbage_FailsWithPlainError()
    {
        Assert.False(SettingsStore.TryImport("hello", out var settings, out var error));
        Assert.Null(settings);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Import_SanitizesOutOfRangeValues()
    {
        var json = SettingsStore.Export(new AppSettings { ZoomFactor = 99 });
        Assert.True(SettingsStore.TryImport(json, out var imported, out _));
        Assert.Equal(ZoomMath.Max, imported!.ZoomFactor);
    }
}
