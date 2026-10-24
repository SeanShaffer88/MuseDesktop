using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>v0.6.0 Slice 2 (items 13–22) Core additions: active-download
/// model + aggregate status, rate sampling + speed wording, history
/// cap/search/filter/sort + failure records + removal, collision-free
/// names, per-profile downloads folder, and the new settings.</summary>
public class S2Tests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"musedesktop-s2-{Guid.NewGuid():N}.json");

    private static DownloadRecord Rec(string name, int minutesAgo, long size = 1024,
        DownloadRecordState state = DownloadRecordState.Completed) => new()
    {
        FileName = name,
        FullPath = $@"C:\Downloads\{name}",
        SizeBytes = size,
        CompletedUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        State = state,
    };

    private static ActiveDownload Active(string id, long bytes, long? total,
        DownloadActivityState state = DownloadActivityState.InProgress, double speed = 0) => new()
    {
        Id = id,
        FileName = $"{id}.zip",
        FullPath = $@"C:\Downloads\{id}.zip",
        BytesReceived = bytes,
        TotalBytes = total,
        State = state,
        SpeedBytesPerSecond = speed,
    };

    // ── #22 speed wording ──

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(-5, "0 B/s")]
    [InlineData(512, "512 B/s")]
    [InlineData(1024, "1 KB/s")]
    [InlineData(1536, "1.5 KB/s")]
    [InlineData(1048576, "1 MB/s")]
    [InlineData(11534336, "11 MB/s")]
    public void FormatSpeed_MatchesFormatBytesWithPerSecond(double bps, string expected) =>
        Assert.Equal(expected, DownloadText.FormatSpeed(bps));

    [Fact]
    public void ProgressStatus_WithSpeed_KeepsMainTextAppendsSpeedToDetail()
    {
        var now = DateTimeOffset.UtcNow;
        var plain = DownloadText.ProgressStatus("report.pdf", 512 * 1024, 2048 * 1024, now.AddSeconds(90), now);
        var withSpeed = DownloadText.ProgressStatus("report.pdf", 512 * 1024, 2048 * 1024, now.AddSeconds(90), now, 1536);
        Assert.Equal(plain.Text, withSpeed.Text);
        Assert.Equal("512 KB of 2048 KB · 1.5 KB/s", withSpeed.Right);
    }

    [Fact]
    public void ProgressStatus_ZeroSpeed_IdenticalToPlain()
    {
        var now = DateTimeOffset.UtcNow;
        var plain = DownloadText.ProgressStatus("report.pdf", 2048, null, null, now);
        var zero = DownloadText.ProgressStatus("report.pdf", 2048, null, null, now, 0);
        Assert.Equal(plain, zero);
    }

    // ── #22 rate sampler ──

    [Fact]
    public void RateSampler_FirstSampleIsZeroThenInstantRateThenSmoothed()
    {
        var sampler = new DownloadRateSampler();
        var t0 = DateTimeOffset.UtcNow;
        Assert.Equal(0, sampler.Sample(0, t0));
        // 2000 bytes in 2 s -> 1000 B/s, taken whole as the first real rate.
        Assert.Equal(1000, sampler.Sample(2000, t0.AddSeconds(2)), precision: 6);
        // 2000 bytes in 1 s -> instant 2000, EWMA: 1000 + 0.3 * 1000 = 1300.
        Assert.Equal(1300, sampler.Sample(4000, t0.AddSeconds(3)), precision: 6);
    }

    [Fact]
    public void RateSampler_ByteCountGoingBackwardsResets()
    {
        var sampler = new DownloadRateSampler();
        var t0 = DateTimeOffset.UtcNow;
        sampler.Sample(5000, t0);
        sampler.Sample(7000, t0.AddSeconds(1));
        Assert.Equal(0, sampler.Sample(100, t0.AddSeconds(2)));
        Assert.Equal(0, sampler.BytesPerSecond);
    }

    [Fact]
    public void RateSampler_ResetClearsRate()
    {
        var sampler = new DownloadRateSampler();
        var t0 = DateTimeOffset.UtcNow;
        sampler.Sample(0, t0);
        sampler.Sample(3000, t0.AddSeconds(1));
        Assert.True(sampler.BytesPerSecond > 0);
        sampler.Reset();
        Assert.Equal(0, sampler.BytesPerSecond);
        Assert.Equal(0, sampler.Sample(9999, t0.AddSeconds(2)));
    }

    // ── #13 active set / aggregate ──

    [Fact]
    public void ActiveSet_AddFindRemove()
    {
        var set = new ActiveDownloadSet();
        var a = Active("a", 1, 10);
        set.Add(a);
        Assert.Same(a, set.Find("a"));
        Assert.Null(set.Find("zzz"));
        Assert.True(set.Remove("a"));
        Assert.False(set.Remove("a"));
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void ActiveSet_OverallFractionSumsKnownTotals()
    {
        var set = new ActiveDownloadSet();
        set.Add(Active("a", 50, 100));
        set.Add(Active("b", 150, 300));
        Assert.Equal(0.5, set.OverallFraction!.Value, precision: 6);
        set.Add(Active("c", 999, null)); // unknown total: excluded
        Assert.Equal(0.5, set.OverallFraction!.Value, precision: 6);
    }

    [Fact]
    public void ActiveSet_OverallFractionNullWithoutKnownTotals()
    {
        var set = new ActiveDownloadSet();
        set.Add(Active("a", 10, null));
        Assert.Null(set.OverallFraction);
        Assert.Null(new ActiveDownloadSet().OverallFraction);
    }

    [Fact]
    public void ActiveSet_FlagsAndTotalSpeed()
    {
        var set = new ActiveDownloadSet();
        set.Add(Active("a", 1, 10, DownloadActivityState.Paused, speed: 500));
        Assert.False(set.AnyTransferring);
        Assert.True(set.AnyPaused);
        Assert.Equal(0, set.TotalSpeed); // paused downloads don't transfer
        set.Add(Active("b", 1, 10, DownloadActivityState.InProgress, speed: 700));
        Assert.True(set.AnyTransferring);
        Assert.Equal(700, set.TotalSpeed);
    }

    [Fact]
    public void AggregateStatus_MultipleDownloads()
    {
        var now = DateTimeOffset.UtcNow;
        var set = new ActiveDownloadSet();
        set.Add(Active("a", 512 * 1024, 1024 * 1024, speed: 1024));
        set.Add(Active("b", 512 * 1024, 1024 * 1024));
        var (text, right) = DownloadText.AggregateStatus(set, now);
        Assert.Equal("Downloading 2 files — 50%", text);
        Assert.Contains("1 KB/s", right);
    }

    [Fact]
    public void AggregateStatus_UnknownTotals()
    {
        var now = DateTimeOffset.UtcNow;
        var set = new ActiveDownloadSet();
        set.Add(Active("a", 2048, null));
        set.Add(Active("b", 2048, null));
        var (text, _) = DownloadText.AggregateStatus(set, now);
        Assert.Equal("Downloading 2 files…", text);
    }

    [Fact]
    public void AggregateStatus_AllPaused()
    {
        var now = DateTimeOffset.UtcNow;
        var set = new ActiveDownloadSet();
        set.Add(Active("a", 5, 10, DownloadActivityState.Paused));
        var (text, _) = DownloadText.AggregateStatus(set, now);
        Assert.Equal("Download paused", text);
    }

    [Fact]
    public void ActiveDetail_InProgressShowsPercentBytesAndSpeed()
    {
        var now = DateTimeOffset.UtcNow;
        var d = Active("a", 512 * 1024, 2048 * 1024, speed: 1536);
        var detail = DownloadText.ActiveDetail(d, now);
        Assert.Contains("25%", detail);
        Assert.Contains("1.5 KB/s", detail);
    }

    [Fact]
    public void ActiveDetail_PausedWording()
    {
        var now = DateTimeOffset.UtcNow;
        var d = Active("a", 50, 100, DownloadActivityState.Paused);
        Assert.StartsWith("Paused — 50%", DownloadText.ActiveDetail(d, now));
        var unknown = Active("b", 2048, null, DownloadActivityState.Paused);
        Assert.StartsWith("Paused —", DownloadText.ActiveDetail(unknown, now));
    }

    [Fact]
    public void ActiveDownload_PercentClampsAndNullWhenUnknown()
    {
        Assert.Equal(50, Active("a", 50, 100).Percent);
        Assert.Equal(100, Active("a", 500, 100).Percent);
        Assert.Null(Active("a", 5, null).Percent);
        Assert.Null(Active("a", 5, 0).Percent);
    }

    // ── #16 history: cap parameter, failure records, query, removal ──

    [Fact]
    public void History_DefaultCapStillFifty()
    {
        var path = TempPath();
        try
        {
            var many = Enumerable.Range(0, 60).Select(i => Rec($"f{i}.zip", i)).ToList();
            DownloadHistoryStore.Save(path, many);
            Assert.Equal(50, DownloadHistoryStore.Load(path).Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void History_ConfiguredCapIsHonored()
    {
        var path = TempPath();
        try
        {
            var many = Enumerable.Range(0, 120).Select(i => Rec($"f{i}.zip", i)).ToList();
            DownloadHistoryStore.Save(path, many, cap: 200);
            Assert.Equal(120, DownloadHistoryStore.Load(path, cap: 200).Count);
            Assert.Equal(50, DownloadHistoryStore.Load(path).Count); // old callers unchanged
            DownloadHistoryStore.Add(path, Rec("new.zip", 0), cap: 200);
            Assert.Equal(121, DownloadHistoryStore.Load(path, cap: 200).Count);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(200, 200)]
    [InlineData(1000, 1000)]
    [InlineData(0, 50)]
    [InlineData(75, 50)]
    [InlineData(-1, 50)]
    public void History_SanitizeCap(int input, int expected) =>
        Assert.Equal(expected, DownloadHistoryStore.SanitizeCap(input));

    [Fact]
    public void History_FailedRecordRoundTripsWithReason()
    {
        var path = TempPath();
        try
        {
            var failed = Rec("bad.zip", 0, 123, DownloadRecordState.Failed);
            failed.FailureReason = "NetworkFailed";
            DownloadHistoryStore.Add(path, failed);
            var loaded = Assert.Single(DownloadHistoryStore.Load(path));
            Assert.Equal(DownloadRecordState.Failed, loaded.State);
            Assert.Equal("NetworkFailed", loaded.FailureReason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void History_OldFileWithoutState_LoadsAsCompleted()
    {
        var path = TempPath();
        try
        {
            // A v0.4.0-era file: no "state" field at all.
            File.WriteAllText(path,
                "[{\"fileName\":\"old.zip\",\"fullPath\":\"C:\\\\Downloads\\\\old.zip\",\"sizeBytes\":5,\"completedUtc\":\"2026-01-01T00:00:00+00:00\"}]");
            var loaded = Assert.Single(DownloadHistoryStore.Load(path));
            Assert.Equal(DownloadRecordState.Completed, loaded.State);
            Assert.Null(loaded.FailureReason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HistoryQuery_SearchIsCaseInsensitiveOnFileName()
    {
        var records = new[] { Rec("Report.pdf", 1), Rec("photo.png", 2), Rec("report-2.pdf", 3) };
        var found = DownloadHistoryQuery.Apply(records, "REPORT", DownloadStateFilter.All, DownloadHistorySort.NewestFirst);
        Assert.Equal(2, found.Count);
        Assert.All(found, r => Assert.Contains("report", r.FileName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HistoryQuery_FiltersByState()
    {
        var records = new[]
        {
            Rec("ok.zip", 1),
            Rec("bad.zip", 2, state: DownloadRecordState.Failed),
            Rec("ok2.zip", 3),
        };
        Assert.Equal(2, DownloadHistoryQuery.Apply(records, null, DownloadStateFilter.Completed, DownloadHistorySort.NewestFirst).Count);
        var failed = DownloadHistoryQuery.Apply(records, null, DownloadStateFilter.Failed, DownloadHistorySort.NewestFirst);
        Assert.Equal("bad.zip", Assert.Single(failed).FileName);
        Assert.Equal(3, DownloadHistoryQuery.Apply(records, "  ", DownloadStateFilter.All, DownloadHistorySort.NewestFirst).Count);
    }

    [Fact]
    public void HistoryQuery_Sorts()
    {
        var records = new[]
        {
            Rec("b.zip", 10, size: 300),
            Rec("a.zip", 30, size: 100),
            Rec("c.zip", 20, size: 200),
        };
        Assert.Equal(new[] { "b.zip", "c.zip", "a.zip" },
            DownloadHistoryQuery.Apply(records, null, DownloadStateFilter.All, DownloadHistorySort.NewestFirst).Select(r => r.FileName));
        Assert.Equal(new[] { "b.zip", "c.zip", "a.zip" },
            DownloadHistoryQuery.Apply(records, null, DownloadStateFilter.All, DownloadHistorySort.LargestFirst).Select(r => r.FileName));
        Assert.Equal(new[] { "a.zip", "b.zip", "c.zip" },
            DownloadHistoryQuery.Apply(records, null, DownloadStateFilter.All, DownloadHistorySort.NameAscending).Select(r => r.FileName));
    }

    [Fact]
    public void History_RemoveDeletesOnlyTheMatchingEntry()
    {
        var path = TempPath();
        try
        {
            var keep = Rec("keep.zip", 5);
            var drop = Rec("drop.zip", 1);
            DownloadHistoryStore.Add(path, keep);
            DownloadHistoryStore.Add(path, drop);
            // Reload so CompletedUtc equality is the persisted value.
            var persisted = DownloadHistoryStore.Load(path).Single(r => r.FileName == "drop.zip");
            Assert.True(DownloadHistoryStore.Remove(path, persisted));
            var remaining = DownloadHistoryStore.Load(path);
            Assert.Equal("keep.zip", Assert.Single(remaining).FileName);
            Assert.False(DownloadHistoryStore.Remove(path, persisted));
        }
        finally { File.Delete(path); }
    }

    // ── #21 unique file names ──

    [Fact]
    public void UniqueFileName_NoCollisionReturnsOriginal()
    {
        var path = @"C:\Downloads\report.pdf";
        Assert.Equal(path, UniqueFileName.ForPath(path, _ => false));
    }

    [Fact]
    public void UniqueFileName_SingleCollisionAppendsOne()
    {
        var existing = new HashSet<string> { @"C:\Downloads\report.pdf" };
        Assert.Equal(@"C:\Downloads\report (1).pdf",
            UniqueFileName.ForPath(@"C:\Downloads\report.pdf", existing.Contains));
    }

    [Fact]
    public void UniqueFileName_SkipsTakenNumbers()
    {
        var existing = new HashSet<string>
        {
            @"C:\Downloads\report.pdf", @"C:\Downloads\report (1).pdf", @"C:\Downloads\report (2).pdf",
        };
        Assert.Equal(@"C:\Downloads\report (3).pdf",
            UniqueFileName.ForPath(@"C:\Downloads\report.pdf", existing.Contains));
    }

    [Fact]
    public void UniqueFileName_NoExtension()
    {
        var existing = new HashSet<string> { @"C:\Downloads\notes" };
        Assert.Equal(@"C:\Downloads\notes (1)",
            UniqueFileName.ForPath(@"C:\Downloads\notes", existing.Contains));
    }

    // ── #20 per-profile downloads folder ──

    [Fact]
    public void FolderResolver_ProfileBeatsSharedBeatsNull()
    {
        Assert.Equal(@"D:\Work", DownloadFolderResolver.Resolve(@"  D:\Work  ", @"C:\Shared"));
        Assert.Equal(@"C:\Shared", DownloadFolderResolver.Resolve(null, @" C:\Shared "));
        Assert.Equal(@"C:\Shared", DownloadFolderResolver.Resolve("   ", @"C:\Shared"));
        Assert.Null(DownloadFolderResolver.Resolve(null, null));
        Assert.Null(DownloadFolderResolver.Resolve("", "  "));
    }

    [Fact]
    public void EffectiveFolder_UsesActiveProfileOverride()
    {
        var settings = new AppSettings { DownloadsFolder = @"C:\Shared" };
        settings.Profiles.Add(new AccountProfile { Id = "work", Name = "Work", DownloadsFolder = @"D:\WorkDl" });
        settings.Sanitized();
        Assert.Equal(@"C:\Shared", settings.EffectiveDownloadsFolder()); // default profile: shared
        settings.ActiveProfileId = "work";
        Assert.Equal(@"D:\WorkDl", settings.EffectiveDownloadsFolder());
    }

    [Fact]
    public void ProfileDownloadsFolder_SanitizedTrimAndNull()
    {
        var settings = new AppSettings();
        settings.Profiles[0].DownloadsFolder = "  D:\\Dl  ";
        settings.Sanitized();
        Assert.Equal(@"D:\Dl", settings.Profiles[0].DownloadsFolder);
        settings.Profiles[0].DownloadsFolder = "   ";
        settings.Sanitized();
        Assert.Null(settings.Profiles[0].DownloadsFolder);
    }

    [Fact]
    public void ProfileDownloadsFolder_RoundTripsExportImport()
    {
        var settings = new AppSettings();
        settings.Profiles[0].DownloadsFolder = @"E:\ProfileDl";
        var json = SettingsStore.Export(settings.Sanitized());
        Assert.True(SettingsStore.TryImport(json, out var imported, out _));
        Assert.Equal(@"E:\ProfileDl", imported!.Profiles[0].DownloadsFolder);
    }

    // ── #16/#18/#21 new settings ──

    [Fact]
    public void NewDownloadSettings_Defaults()
    {
        var s = new AppSettings();
        Assert.False(s.NotifyWhenDownloadCompletes);
        Assert.Equal(50, s.DownloadHistoryCap);
        Assert.Equal(DownloadCollisionPolicy.AutoRename, s.CollisionPolicy);
    }

    [Theory]
    [InlineData(200, 200)]
    [InlineData(1000, 1000)]
    [InlineData(51, 50)]
    [InlineData(-3, 50)]
    public void Settings_Sanitized_ValidatesHistoryCap(int input, int expected) =>
        Assert.Equal(expected, new AppSettings { DownloadHistoryCap = input }.Sanitized().DownloadHistoryCap);

    [Fact]
    public void Settings_Sanitized_InvalidCollisionPolicyFallsBackToAutoRename()
    {
        var s = new AppSettings { CollisionPolicy = (DownloadCollisionPolicy)99 };
        Assert.Equal(DownloadCollisionPolicy.AutoRename, s.Sanitized().CollisionPolicy);
    }

    [Fact]
    public void NewDownloadSettings_RoundTripExportImportAndClone()
    {
        var settings = new AppSettings
        {
            NotifyWhenDownloadCompletes = true,
            DownloadHistoryCap = 200,
            CollisionPolicy = DownloadCollisionPolicy.AskWhereToSave,
        };
        var json = SettingsStore.Export(settings.Sanitized());
        Assert.True(SettingsStore.TryImport(json, out var imported, out _));
        Assert.True(imported!.NotifyWhenDownloadCompletes);
        Assert.Equal(200, imported.DownloadHistoryCap);
        Assert.Equal(DownloadCollisionPolicy.AskWhereToSave, imported.CollisionPolicy);

        var clone = settings.Clone();
        Assert.True(clone.NotifyWhenDownloadCompletes);
        Assert.Equal(200, clone.DownloadHistoryCap);
        Assert.Equal(DownloadCollisionPolicy.AskWhereToSave, clone.CollisionPolicy);
    }
}
