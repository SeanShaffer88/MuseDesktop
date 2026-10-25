using System.Text.Json;
using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>
/// v0.13.0 Slice 9 (items 81–88): release-list parsing + channel
/// selection, the update schedule/snooze matrix, SHA256SUMS
/// verification inputs, installer asset naming + the rollback
/// keep/rotation rules, the update download status line, What's New
/// decisions + section extraction (incl. the CHANGELOG ↔ WhatsNew.md
/// drift guard), and the settings plumbing for every new field.
/// </summary>
public class S9Tests
{
    private static GitHubRelease Release(string tag, bool prerelease = false, params ReleaseAsset[] assets) =>
        new(tag, prerelease, $"https://github.com/SeanShaffer88/MuseDesktop/releases/tag/{tag}", assets);

    private static ReleaseAsset Installer(string version, string arch, long size = 1000) =>
        new(UpdateAssets.InstallerAssetName(version, arch),
            $"https://example.com/{UpdateAssets.InstallerAssetName(version, arch)}", size);

    // ──────────────────────── release parsing (#88) ────────────────────────

    [Fact]
    public void ParseReleases_ReadsTagsFlagsAndAssets()
    {
        const string json = """
            [
              { "tag_name": "v0.14.0-beta.1", "prerelease": true,
                "html_url": "https://github.com/x/releases/v0.14.0-beta.1",
                "assets": [ { "name": "MuseDesktop-Setup-v0.14.0-x64.exe",
                              "browser_download_url": "https://example.com/a.exe", "size": 42 } ] },
              { "tag_name": "v0.13.0", "prerelease": false,
                "html_url": "https://github.com/x/releases/v0.13.0", "assets": [] },
              { "prerelease": false }
            ]
            """;
        var releases = ReleaseSelection.ParseReleasesJson(json);
        Assert.Equal(2, releases.Count);
        Assert.True(releases[0].IsPrerelease);
        var asset = Assert.Single(releases[0].Assets);
        Assert.Equal(42, asset.Size);
        Assert.False(releases[1].IsPrerelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void ParseReleases_GarbageReadsAsNoReleases(string json) =>
        Assert.Empty(ReleaseSelection.ParseReleasesJson(json));

    // ──────────────────────── channel selection (#88) ────────────────────────

    [Fact]
    public void Select_StableChannelNeverOffersPrereleases()
    {
        var releases = new[]
        {
            Release("v0.14.0-beta.1", prerelease: true),
            Release("v0.12.0"),
        };
        Assert.Null(ReleaseSelection.SelectNewest(releases, new Version(0, 12, 0), UpdateChannel.Stable));
    }

    [Fact]
    public void Select_StableChannelIgnoresSuffixOnlyPrereleases()
    {
        // The prerelease flag and the tag suffix must agree to be
        // offered on Stable; a suffixed tag with the flag missing is
        // still a pre-release for selection purposes.
        var releases = new[] { Release("v0.14.0-rc.1", prerelease: false) };
        Assert.Null(ReleaseSelection.SelectNewest(releases, new Version(0, 13, 0), UpdateChannel.Stable));
        Assert.NotNull(ReleaseSelection.SelectNewest(releases, new Version(0, 13, 0), UpdateChannel.IncludePrereleases));
    }

    [Fact]
    public void Select_StableChannelPicksNewestStable()
    {
        var releases = new[] { Release("v0.13.0"), Release("v0.14.0"), Release("v0.12.0") };
        var picked = ReleaseSelection.SelectNewest(releases, new Version(0, 12, 0), UpdateChannel.Stable);
        Assert.Equal("v0.14.0", picked?.Tag);
    }

    [Fact]
    public void Select_PreChannelPrefersStableAtSameBaseVersion()
    {
        var releases = new[] { Release("v1.0.0-beta.2", prerelease: true), Release("v1.0.0") };
        var picked = ReleaseSelection.SelectNewest(releases, new Version(0, 13, 0), UpdateChannel.IncludePrereleases);
        Assert.Equal("v1.0.0", picked?.Tag);
    }

    [Fact]
    public void Select_NoDowngradeAndNoSidewaysOffer()
    {
        var releases = new[] { Release("v0.12.0"), Release("v0.13.0") };
        Assert.Null(ReleaseSelection.SelectNewest(releases, new Version(0, 13, 0), UpdateChannel.IncludePrereleases));
        Assert.Null(ReleaseSelection.SelectNewest(releases, new Version(1, 0, 0), UpdateChannel.IncludePrereleases));
    }

    [Fact]
    public void Select_PreUserIsOfferedSameBaseStable_OnlyWhenKnownPre()
    {
        var releases = new[] { Release("v1.0.0") };
        var current = new Version(1, 0, 0);
        // A user ON 1.0.0-beta switching back to Stable gets the
        // stable 1.0.0 offered (not a downgrade)…
        Assert.NotNull(ReleaseSelection.SelectNewest(
            releases, current, UpdateChannel.Stable, currentIsPrerelease: true));
        // …but a user already ON stable 1.0.0 is not re-offered it.
        Assert.Null(ReleaseSelection.SelectNewest(
            releases, current, UpdateChannel.Stable, currentIsPrerelease: false));
    }

    [Fact]
    public void Select_SkipsUnparseableTags()
    {
        var releases = new[] { Release("nightly"), Release("v0.14.0") };
        var picked = ReleaseSelection.SelectNewest(releases, new Version(0, 13, 0), UpdateChannel.Stable);
        Assert.Equal("v0.14.0", picked?.Tag);
    }

    // ──────────────────────── schedule + snooze (#83) ────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Schedule_MasterSwitchOff_NeverAutoChecks() =>
        Assert.False(UpdateSchedule.ShouldAutoCheck(
            Now, updatesEnabled: false, UpdateCheckFrequency.EveryLaunch, null, null));

    [Fact]
    public void Schedule_ActiveSnoozeBlocks_EveryFrequency()
    {
        foreach (var frequency in Enum.GetValues<UpdateCheckFrequency>())
            Assert.False(UpdateSchedule.ShouldAutoCheck(
                Now, true, frequency, null, Now.AddHours(3)));
    }

    [Fact]
    public void Schedule_ExpiredSnoozeDoesNotBlock() =>
        Assert.True(UpdateSchedule.ShouldAutoCheck(
            Now, true, UpdateCheckFrequency.EveryLaunch, null, Now.AddMinutes(-1)));

    [Theory]
    [InlineData(UpdateCheckFrequency.EveryLaunch, null, true)]
    [InlineData(UpdateCheckFrequency.Daily, null, true)]
    [InlineData(UpdateCheckFrequency.Daily, -23, false)]   // hours ago
    [InlineData(UpdateCheckFrequency.Daily, -25, true)]
    [InlineData(UpdateCheckFrequency.Weekly, -167, false)] // just under 7 days, in hours
    [InlineData(UpdateCheckFrequency.Weekly, -169, true)]
    [InlineData(UpdateCheckFrequency.ManualOnly, null, false)]
    [InlineData(UpdateCheckFrequency.ManualOnly, -1000, false)]
    public void Schedule_FrequencyMatrix(UpdateCheckFrequency frequency, int? lastCheckHoursAgo, bool expected)
    {
        DateTimeOffset? last = lastCheckHoursAgo is { } h ? Now.AddHours(h) : null;
        Assert.Equal(expected, UpdateSchedule.ShouldAutoCheck(Now, true, frequency, last, null));
    }

    [Fact]
    public void Snooze_Durations()
    {
        Assert.Equal(Now.AddDays(1), UpdateSchedule.SnoozeUntil(Now, UpdateSnooze.Tomorrow));
        Assert.Equal(Now.AddDays(7), UpdateSchedule.SnoozeUntil(Now, UpdateSnooze.NextWeek));
        Assert.Contains("snoozed until", UpdateSchedule.DescribeSnooze(Now.AddDays(1)));
    }

    // ──────────────────────── SHA256SUMS (#81) ────────────────────────

    private const string SampleSums =
        "03004823e8718e2abb76450338b49655aa1e6ab526e0b9f4372dfb8311859a5c  MuseDesktop-v0.5.0-windows-x64.zip\r\n" +
        "e15ccbdccfb82aa95e2166298dddf9eb5ae6945c2272b317dbf46be1d92be869 *MuseDesktop-Setup-v0.5.0-x64.exe\n" +
        "not a hash line\n" +
        "abc  too-short.exe\n";

    [Fact]
    public void Sums_ParsesSha256sumFormat()
    {
        var map = SumsFile.Parse(SampleSums);
        Assert.Equal(2, map.Count);
        Assert.Equal("e15ccbdccfb82aa95e2166298dddf9eb5ae6945c2272b317dbf46be1d92be869",
            SumsFile.ExpectedHash(SampleSums, "MuseDesktop-Setup-v0.5.0-x64.exe"));
        Assert.Null(SumsFile.ExpectedHash(SampleSums, "MuseDesktop-Setup-v0.5.0-arm64.exe"));
    }

    [Fact]
    public void Sums_HashesMatch_IsExactButCaseTolerant()
    {
        const string hash = "E15CCBDCCFB82AA95E2166298DDDF9EB5AE6945C2272B317DBF46BE1D92BE869";
        Assert.True(SumsFile.HashesMatch(hash, hash.ToLowerInvariant()));
        Assert.False(SumsFile.HashesMatch(hash, "0" + hash[1..]));
        Assert.False(SumsFile.HashesMatch(hash, null));
        Assert.False(SumsFile.HashesMatch("xyz", "xyz"));
    }

    // ──────────────────────── installer assets + rollback (#81/#84) ────────────────────────

    [Fact]
    public void Assets_NamingAndPicking()
    {
        Assert.Equal("MuseDesktop-Setup-v0.13.0-x64.exe", UpdateAssets.InstallerAssetName("0.13.0", "x64"));
        Assert.Equal("MuseDesktop-Setup-v0.13.0-arm64.exe", UpdateAssets.InstallerAssetName("v0.13.0", "arm64"));
        Assert.Equal("arm64", UpdateAssets.ArchToken("Arm64"));
        var release = Release("v0.13.0", false, Installer("0.13.0", "x64"), Installer("0.13.0", "arm64"));
        Assert.Equal("MuseDesktop-Setup-v0.13.0-arm64.exe", UpdateAssets.PickInstaller(release, "arm64")?.Name);
        Assert.Null(UpdateAssets.PickInstaller(Release("v0.13.0"), "x64"));
        Assert.True(UpdateAssets.IsInstallerForArch("MuseDesktop-Setup-v0.12.0-x64.exe", "x64"));
        Assert.False(UpdateAssets.IsInstallerForArch("MuseDesktop-Setup-v0.12.0-x64.exe", "arm64"));
        Assert.False(UpdateAssets.IsInstallerForArch("MuseDesktop-v0.12.0-windows-x64.zip", "x64"));
    }

    [Fact]
    public void Rotation_FirstDownloadRotatesNothing()
    {
        var (rotate, previous) = InstallerKeep.PlanRotation(null, null, "0.13.0", Now);
        Assert.False(rotate);
        Assert.Null(previous);
    }

    [Fact]
    public void Rotation_NextUpdateMakesKeptTheRollback()
    {
        var (rotate, previous) = InstallerKeep.PlanRotation(
            "0.12.0", "MuseDesktop-Setup-v0.12.0-x64.exe", "0.13.0", Now);
        Assert.True(rotate);
        Assert.Equal("0.12.0", previous?.Version);
        Assert.Equal("MuseDesktop-Setup-v0.12.0-x64.exe", previous?.FileName);
        Assert.Equal(Now, previous?.KeptUtc);
    }

    [Fact]
    public void Rotation_SameVersionOrBadRecordRotatesNothing()
    {
        Assert.False(InstallerKeep.PlanRotation(
            "0.13.0", "MuseDesktop-Setup-v0.13.0-x64.exe", "0.13.0", Now).RotateCurrentToPrevious);
        Assert.False(InstallerKeep.PlanRotation(
            "garbage", "MuseDesktop-Setup-v0.12.0-x64.exe", "0.13.0", Now).RotateCurrentToPrevious);
    }

    [Fact]
    public void Rollback_UsableOnlyWhenKeptOnDiskAndSameArch()
    {
        var record = new PreviousInstallerInfo
        {
            Version = "0.12.0",
            FileName = "MuseDesktop-Setup-v0.12.0-x64.exe",
            KeptUtc = Now,
        };
        const string dir = "/data/updates";
        var expectedPath = Path.Combine(dir, InstallerKeep.PreviousSlot, record.FileName);
        Assert.Equal(expectedPath, RollbackOffer.UsableInstallerPath(record, dir, "x64", _ => true));
        Assert.Null(RollbackOffer.UsableInstallerPath(record, dir, "x64", _ => false));      // file gone
        Assert.Null(RollbackOffer.UsableInstallerPath(record, dir, "arm64", _ => true));    // wrong arch
        Assert.Null(RollbackOffer.UsableInstallerPath(null, dir, "x64", _ => true));        // no record
        Assert.Null(RollbackOffer.UsableInstallerPath(
            new PreviousInstallerInfo { Version = "0.12.0", FileName = "../escape.exe" }, dir, "x64", _ => true));
        Assert.Equal("Reinstall previous version (0.12.0)", RollbackOffer.Describe(record));
    }

    [Fact]
    public void DownloadStatus_KnownTotalShowsPercentSpeedEta()
    {
        var line = UpdateDownloadStatus.Format(50_000_000, 100_000_000, 10_000_000);
        Assert.Contains("50%", line);
        Assert.Contains("MB/s", line);
        Assert.Contains("left", line);
    }

    [Fact]
    public void DownloadStatus_UnknownTotalIsIndeterminate()
    {
        var line = UpdateDownloadStatus.Format(1234, null, 0);
        Assert.DoesNotContain("%", line);
        Assert.Contains("received", line);
    }

    // ──────────────────────── What's New (#82) ────────────────────────

    [Theory]
    [InlineData(null, "0.13.0", true, WhatsNewDecision.RecordOnly)]  // fresh install
    [InlineData("0.12.0", "0.13.0", true, WhatsNewDecision.Show)]     // first launch of a new version
    [InlineData("0.13.0", "0.13.0", true, WhatsNewDecision.Skip)]     // already seen
    [InlineData("0.12.0", "0.13.0", false, WhatsNewDecision.Skip)]    // opted out
    [InlineData(null, "0.13.0", false, WhatsNewDecision.Skip)]        // opted out, fresh
    public void WhatsNew_DecisionMatrix(string? lastSeen, string current, bool enabled, WhatsNewDecision expected) =>
        Assert.Equal(expected, WhatsNew.Decide(lastSeen, current, enabled));

    [Fact]
    public void WhatsNew_ExtractsOnlyTheMatchingSection()
    {
        const string md = """
            # Changelog

            ## [0.13.0] — Unreleased

            New stuff here.

            ## [0.12.0] — Unreleased

            Old stuff here.
            """;
        var body = WhatsNew.ExtractSection(md, "0.13.0");
        Assert.Contains("New stuff here.", body);
        Assert.DoesNotContain("Old stuff", body);
        Assert.Contains("Old stuff here.", WhatsNew.ExtractSection(md, "v0.12.0"));
        Assert.Null(WhatsNew.ExtractSection(md, "9.9.9"));
    }

    [Fact]
    public void WhatsNew_ShippedFileMatchesChangelogForTheCsprojVersion()
    {
        // The drift guard (#82): the WhatsNew.md beside the exe is a
        // release-time copy of CHANGELOG.md's current-version section —
        // if they disagree, the release process skipped a step.
        var root = RepoRoot();
        var csproj = File.ReadAllText(Path.Combine(root, "src", "MuseDesktop", "MuseDesktop.csproj"));
        var versionMatch = System.Text.RegularExpressions.Regex.Match(csproj, "<Version>([^<]+)</Version>");
        Assert.True(versionMatch.Success, "csproj has no <Version>");
        var version = versionMatch.Groups[1].Value;

        var changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        var whatsNew = File.ReadAllText(Path.Combine(root, "WhatsNew.md"));
        var fromChangelog = WhatsNew.ExtractSection(changelog, version);
        var fromShipped = WhatsNew.ExtractSection(whatsNew, version);
        Assert.NotNull(fromChangelog);
        Assert.Equal(fromChangelog, fromShipped);
    }

    // ──────────────────────── settings plumbing (all S9 fields) ────────────────────────

    [Fact]
    public void Settings_S9Fields_RoundTripThroughCloneAndExportImport()
    {
        var settings = new AppSettings
        {
            UpdateChannel = UpdateChannel.IncludePrereleases,
            UpdateCheckFrequency = UpdateCheckFrequency.Weekly,
            LastUpdateCheckUtc = Now,
            UpdateSnoozedUntilUtc = Now.AddDays(1),
            LastSeenVersion = "0.12.0",
            ShowWhatsNew = false,
            KeptInstaller = new PreviousInstallerInfo
            {
                Version = "0.12.0", FileName = "MuseDesktop-Setup-v0.12.0-x64.exe", KeptUtc = Now,
            },
            PreviousInstaller = new PreviousInstallerInfo
            {
                Version = "0.11.0", FileName = "MuseDesktop-Setup-v0.11.0-x64.exe", KeptUtc = Now,
            },
        };
        var clone = settings.Clone();
        Assert.Equal(UpdateChannel.IncludePrereleases, clone.UpdateChannel);
        Assert.Equal("0.12.0", clone.KeptInstaller?.Version);
        Assert.Equal("0.11.0", clone.PreviousInstaller?.Version);

        var json = SettingsStore.Export(settings);
        Assert.True(SettingsStore.TryImport(json, out var imported, out _));
        Assert.Equal(UpdateCheckFrequency.Weekly, imported!.UpdateCheckFrequency);
        Assert.Equal(Now.AddDays(1), imported.UpdateSnoozedUntilUtc);
        Assert.False(imported.ShowWhatsNew);
        Assert.Equal("0.12.0", imported.LastSeenVersion);
    }

    [Fact]
    public void Settings_S9Sanitize_RepairsBadValues()
    {
        var settings = new AppSettings
        {
            UpdateChannel = (UpdateChannel)99,
            UpdateCheckFrequency = (UpdateCheckFrequency)99,
            LastSeenVersion = "  ",
            PreviousInstaller = new PreviousInstallerInfo { Version = "0.12.0", FileName = "" },
            KeptInstaller = new PreviousInstallerInfo { Version = "junk", FileName = "x.exe" },
        }.Sanitized();
        Assert.Equal(UpdateChannel.Stable, settings.UpdateChannel);
        Assert.Equal(UpdateCheckFrequency.EveryLaunch, settings.UpdateCheckFrequency);
        Assert.Null(settings.LastSeenVersion);
        Assert.Null(settings.PreviousInstaller);
        Assert.Null(settings.KeptInstaller);
    }

    [Fact]
    public void SettingsReset_S9PreferencesReset_StatePreserved()
    {
        var settings = new AppSettings
        {
            UpdateChannel = UpdateChannel.IncludePrereleases,
            UpdateCheckFrequency = UpdateCheckFrequency.ManualOnly,
            UpdateSnoozedUntilUtc = Now.AddDays(1),
            ShowWhatsNew = false,
            LastSeenVersion = "0.12.0",
            LastUpdateCheckUtc = Now,
            PreviousInstaller = new PreviousInstallerInfo
            {
                Version = "0.12.0", FileName = "MuseDesktop-Setup-v0.12.0-x64.exe", KeptUtc = Now,
            },
        };
        SettingsReset.ApplyDefaults(settings);
        Assert.Equal(UpdateChannel.Stable, settings.UpdateChannel);
        Assert.Equal(UpdateCheckFrequency.EveryLaunch, settings.UpdateCheckFrequency);
        Assert.Null(settings.UpdateSnoozedUntilUtc);
        Assert.True(settings.ShowWhatsNew);
        // State, not preferences — preserved by design.
        Assert.Equal("0.12.0", settings.LastSeenVersion);
        Assert.Equal(Now, settings.LastUpdateCheckUtc);
        Assert.Equal("0.12.0", settings.PreviousInstaller?.Version);
    }

    [Fact]
    public void SettingsSearch_FindsBehaviorForUpdateWords()
    {
        Assert.Contains(SettingsSearch.Sections,
            s => s.Id == SettingsSearch.BehaviorId);
        var hits = SettingsSearch.Search("prerelease");
        Assert.Contains(hits, s => s.Id == SettingsSearch.BehaviorId);
        Assert.Contains(SettingsSearch.Search("snooze"), s => s.Id == SettingsSearch.BehaviorId);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(typeof(S9Tests).Assembly.Location)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MuseDesktop.sln"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "MuseDesktop")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }
}
