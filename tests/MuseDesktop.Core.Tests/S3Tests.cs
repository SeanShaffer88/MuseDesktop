using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>v0.7.0 Slice 3 (items 23–30) Core additions: the command-line
/// parser + launch-profile resolution (#23), the profile color palette
/// (#24), menu labels (#24/#25), per-profile zoom (#26), profile data
/// facts (#27), deferred folder deletion (#28), settings copy (#29),
/// and the raised profile cap (#30).</summary>
public class S3Tests
{
    private static AppSettings SettingsWith(params AccountProfile[] profiles) => new()
    {
        Profiles = profiles.ToList(),
        ActiveProfileId = profiles.Length > 0 ? profiles[0].Id : Profiles.DefaultId,
    };

    // ── #23 command line ──

    [Fact]
    public void CommandLine_ProfileTakesNextArgument()
    {
        var o = CommandLine.Parse(new[] { "--profile", "work" });
        Assert.Equal("work", o.ProfileId);
        Assert.False(o.Background);
        Assert.Empty(o.Unknown);
    }

    [Fact]
    public void CommandLine_ProfileEqualsForm()
    {
        var o = CommandLine.Parse(new[] { "--profile=work" });
        Assert.Equal("work", o.ProfileId);
    }

    [Fact]
    public void CommandLine_FlagsAreCaseInsensitiveAndBackgroundKnown()
    {
        var o = CommandLine.Parse(new[] { "--BACKGROUND", "--Profile", "work" });
        Assert.True(o.Background);
        Assert.Equal("work", o.ProfileId);
    }

    [Fact]
    public void CommandLine_ProfileWithoutValueIsNull()
    {
        Assert.Null(CommandLine.Parse(new[] { "--profile" }).ProfileId);
        Assert.Null(CommandLine.Parse(new[] { "--profile=" }).ProfileId);
    }

    [Fact]
    public void CommandLine_ProfileDoesNotSwallowAnotherFlag()
    {
        var o = CommandLine.Parse(new[] { "--profile", "--background" });
        Assert.Null(o.ProfileId);
        Assert.True(o.Background);
    }

    [Fact]
    public void CommandLine_UnknownArgumentsTolerated()
    {
        var o = CommandLine.Parse(new[] { "--future-flag", "stray", "--background" });
        Assert.True(o.Background);
        Assert.Equal(new[] { "--future-flag", "stray" }, o.Unknown);
        Assert.Null(o.ProfileId);
    }

    [Fact]
    public void CommandLine_EmptyArgs()
    {
        var o = CommandLine.Parse(Array.Empty<string>());
        Assert.False(o.Background);
        Assert.False(o.SmokeTest);
        Assert.Null(o.ProfileId);
        Assert.Empty(o.Unknown);
    }

    // ── #23 launch-profile resolution ──

    [Fact]
    public void LaunchProfile_ValidRequestedWins()
    {
        var s = SettingsWith(
            new AccountProfile { Id = "default", Name = "Default" },
            new AccountProfile { Id = "work", Name = "Work" }).Sanitized();
        var (id, fellBack) = Profiles.ResolveLaunchProfile(s, "work");
        Assert.Equal("work", id);
        Assert.False(fellBack);
    }

    [Fact]
    public void LaunchProfile_UnknownFallsBackToSavedActive()
    {
        var s = SettingsWith(
            new AccountProfile { Id = "default", Name = "Default" },
            new AccountProfile { Id = "work", Name = "Work" }).Sanitized();
        s.ActiveProfileId = "work";
        var (id, fellBack) = Profiles.ResolveLaunchProfile(s, "ghost");
        Assert.Equal("work", id);
        Assert.True(fellBack);
    }

    [Fact]
    public void LaunchProfile_InvalidShapeAndNullRequest()
    {
        var s = SettingsWith(new AccountProfile()).Sanitized();
        var (a, fellA) = Profiles.ResolveLaunchProfile(s, "BAD ID!");
        Assert.Equal(Profiles.DefaultId, a);
        Assert.True(fellA);
        var (b, fellB) = Profiles.ResolveLaunchProfile(s, null);
        Assert.Equal(Profiles.DefaultId, b);
        Assert.False(fellB);
    }

    // ── #24 palette + sanitize ──

    [Fact]
    public void Palette_KnownKeysHaveHexAndCanonicalizeCase()
    {
        Assert.Equal("Blue", ProfileColors.Sanitize("blue"));
        Assert.Equal("Blue", ProfileColors.Sanitize(" Blue "));
        Assert.Equal("#3B82F6", ProfileColors.HexFor("BLUE"));
        Assert.Equal("#E50914", ProfileColors.HexFor("Red"));
        Assert.Equal(6, ProfileColors.Palette.Count);
    }

    [Fact]
    public void Palette_UnknownAndEmptyAreNone()
    {
        Assert.Null(ProfileColors.Sanitize("chartreuse"));
        Assert.Null(ProfileColors.Sanitize(""));
        Assert.Null(ProfileColors.Sanitize(null));
        Assert.Null(ProfileColors.HexFor("chartreuse"));
        Assert.Null(ProfileColors.HexFor(null));
    }

    [Fact]
    public void ProfileColor_SanitizedOnLoad()
    {
        var s = SettingsWith(new AccountProfile { Id = "default", DisplayColor = "purple" },
                             new AccountProfile { Id = "work", Name = "Work", DisplayColor = "neon" }).Sanitized();
        Assert.Equal("Purple", s.Profiles[0].DisplayColor);
        Assert.Null(s.Profiles[1].DisplayColor);
    }

    [Fact]
    public void MenuLabel_DotOnlyWithColor()
    {
        Assert.Equal("Work", Profiles.MenuLabel(new AccountProfile { Name = "Work" }));
        Assert.Equal("● Work", Profiles.MenuLabel(new AccountProfile { Name = "Work", DisplayColor = "Blue" }));
    }

    // ── #26 per-profile zoom ──

    [Fact]
    public void EffectiveZoom_OverrideWinsAndSharedIsFallback()
    {
        var withOverride = new AccountProfile { ZoomFactor = 1.5 };
        var without = new AccountProfile();
        Assert.Equal(1.5, Profiles.EffectiveZoom(withOverride, 1.0));
        Assert.Equal(1.25, Profiles.EffectiveZoom(without, 1.25));
        Assert.Equal(1.25, Profiles.EffectiveZoom(null, 1.25));
    }

    [Fact]
    public void ProfileZoomOverride_SanitizedClampAndDrop()
    {
        var s = SettingsWith(
            new AccountProfile { Id = "default", ZoomFactor = 99 },
            new AccountProfile { Id = "work", Name = "Work", ZoomFactor = double.NaN },
            new AccountProfile { Id = "play", Name = "Play", ZoomFactor = -1 }).Sanitized();
        Assert.Equal(2.0, s.Profiles[0].ZoomFactor);
        Assert.Null(s.Profiles[1].ZoomFactor);
        Assert.Null(s.Profiles[2].ZoomFactor);
    }

    // ── #27 profile data facts ──

    [Fact]
    public void ProfileData_MissingFolderIsNoData()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"musedesktop-s3-{Guid.NewGuid():N}");
        Assert.Null(ProfileDataInfo.MeasureFolderBytes(missing));
        Assert.Equal("No data on this PC yet", ProfileDataInfo.Describe(missing, new AccountProfile()));
    }

    [Fact]
    public void ProfileData_MeasuresRecursivelyAndDescribes()
    {
        var dir = Directory.CreateTempSubdirectory("musedesktop-s3");
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "a.bin"), new byte[2048]);
            var sub = Directory.CreateDirectory(Path.Combine(dir.FullName, "sub"));
            File.WriteAllBytes(Path.Combine(sub.FullName, "b.bin"), new byte[1024]);
            Assert.Equal(3072, ProfileDataInfo.MeasureFolderBytes(dir.FullName));

            var used = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
            var text = ProfileDataInfo.Describe(dir.FullName, new AccountProfile { LastUsedUtc = used });
            Assert.StartsWith("Data: 3 KB", text);
            Assert.Contains("Last used:", text);
            // No stamp → no last-used segment.
            Assert.Equal("Data: 3 KB", ProfileDataInfo.Describe(dir.FullName, new AccountProfile()));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void LastUsed_SurvivesSettingsRoundtrip()
    {
        var stamp = new DateTimeOffset(2026, 10, 24, 8, 30, 0, TimeSpan.FromHours(-5));
        var json = SettingsStore.Export(SettingsWith(
            new AccountProfile { Id = "default", LastUsedUtc = stamp }).Sanitized());
        Assert.True(SettingsStore.TryImport(json, out var imported, out _));
        Assert.Equal(stamp, imported!.Profiles[0].LastUsedUtc);
    }

    // ── #28 pending folder deletions ──

    [Fact]
    public void PendingDeletions_SanitizeDedupesAndDropsLiveInvalidActive()
    {
        var s = SettingsWith(
            new AccountProfile { Id = "default" },
            new AccountProfile { Id = "work", Name = "Work" });
        s.PendingProfileFolderDeletions = new List<string>
            { "gone1", "gone1", "work", "default", "BAD ID!", "gone2" };
        s.Sanitized();
        Assert.Equal(new[] { "gone1", "gone2" }, s.PendingProfileFolderDeletions);
    }

    [Fact]
    public void PendingDeletions_QueueRules()
    {
        var s = SettingsWith(
            new AccountProfile { Id = "default" },
            new AccountProfile { Id = "work", Name = "Work" }).Sanitized();
        Profiles.QueuePendingDeletion(s, "gone");
        Profiles.QueuePendingDeletion(s, "gone"); // dedupe
        Profiles.QueuePendingDeletion(s, "work");  // live — refused
        Profiles.QueuePendingDeletion(s, "nope!"); // invalid — refused
        Assert.Equal(new[] { "gone" }, s.PendingProfileFolderDeletions);
    }

    [Fact]
    public void PendingDeletions_CappedAtConstant()
    {
        var s = SettingsWith(new AccountProfile { Id = "default" });
        s.PendingProfileFolderDeletions = Enumerable.Range(0, 40).Select(i => $"gone{i}").ToList();
        s.Sanitized();
        Assert.Equal(Profiles.MaxPendingDeletions, s.PendingProfileFolderDeletions.Count);
        Assert.Equal("gone0", s.PendingProfileFolderDeletions[0]);
    }

    [Fact]
    public void PendingDeletions_RoundtripThroughExport()
    {
        var s = SettingsWith(new AccountProfile { Id = "default" }).Sanitized();
        Profiles.QueuePendingDeletion(s, "oldprofile");
        var json = SettingsStore.Export(s);
        Assert.True(SettingsStore.TryImport(json, out var imported, out _));
        Assert.Equal(new[] { "oldprofile" }, imported!.PendingProfileFolderDeletions);
    }

    // ── #29 copy settings ──

    [Fact]
    public void CopySettings_CopiesSettingsNotSessionState()
    {
        var source = new AccountProfile
        {
            Id = "work",
            Name = "Work",
            StartAtHome = true,
            DownloadsFolder = @"D:\WorkDl",
            DisplayColor = "Green",
            ZoomFactor = 1.25,
            LastUsedUtc = DateTimeOffset.UtcNow,
        };
        var copy = Profiles.CopySettingsFrom(source, "newid", "Work copy");
        Assert.Equal("newid", copy.Id);
        Assert.Equal("Work copy", copy.Name);
        Assert.True(copy.StartAtHome);
        Assert.Equal(@"D:\WorkDl", copy.DownloadsFolder);
        Assert.Equal("Green", copy.DisplayColor);
        Assert.Equal(1.25, copy.ZoomFactor);
        // Session-adjacent state is NOT copied.
        Assert.Null(copy.LastUsedUtc);
    }

    // ── #30 profile cap ──

    [Fact]
    public void ProfileCap_IsThirtyTwo()
    {
        Assert.Equal(32, Profiles.MaxProfiles);
        Assert.Equal(8, Profiles.SoftProfileWarningThreshold);
    }

    [Fact]
    public void ProfileCap_TruncatesDeterministicallyKeepingFirst()
    {
        var many = Enumerable.Range(0, 40)
            .Select(i => new AccountProfile { Id = $"p{i}", Name = $"P{i}" })
            .ToList();
        var s = new AppSettings { Profiles = many }.Sanitized();
        Assert.Equal(32, s.Profiles.Count);
        // Default is reinserted at the front by EnsureValid, then the
        // tail is truncated — the surviving generated ids are stable.
        Assert.Equal(Profiles.DefaultId, s.Profiles[0].Id);
        Assert.Equal("p0", s.Profiles[1].Id);
        Assert.Equal("p30", s.Profiles[^1].Id);
    }

    [Fact]
    public void ProfileCap_NineProfilesNowSurvive()
    {
        var nine = Enumerable.Range(0, 9)
            .Select(i => new AccountProfile { Id = $"p{i}", Name = $"P{i}" })
            .ToList();
        var s = new AppSettings { Profiles = nine }.Sanitized();
        // 9 generated + the reinserted default = 10, all under the cap.
        Assert.Equal(10, s.Profiles.Count);
    }

    // ── #23/#24 export-import round-trip of the new profile fields ──

    [Fact]
    public void ExportImport_RoundTripsS3ProfileFields()
    {
        var s = SettingsWith(
            new AccountProfile
            {
                Id = "default",
                DisplayColor = "Orange",
                ZoomFactor = 0.8,
                LastUsedUtc = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero),
            },
            new AccountProfile { Id = "work", Name = "Work", DisplayColor = "Gray" }).Sanitized();
        Profiles.QueuePendingDeletion(s, "retired");
        var json = SettingsStore.Export(s);
        Assert.True(SettingsStore.TryImport(json, out var imported, out var error), error);
        var d = imported!.Profiles[0];
        Assert.Equal("Orange", d.DisplayColor);
        Assert.Equal(0.8, d.ZoomFactor);
        Assert.NotNull(d.LastUsedUtc);
        Assert.Equal("Gray", imported.Profiles[1].DisplayColor);
        Assert.Equal(new[] { "retired" }, imported.PendingProfileFolderDeletions);
    }

    // ── #23 shortcut file name ──

    [Fact]
    public void ShortcutFileName_SanitizesHostileNames()
    {
        Assert.Equal("MuseDesktop - Work.lnk",
            Profiles.ShortcutFileName(new AccountProfile { Name = "Work" }));
        var hostile = Profiles.ShortcutFileName(new AccountProfile { Name = "a/b:c*?" });
        Assert.DoesNotContain("/", hostile);
        Assert.DoesNotContain(":", hostile);
        Assert.DoesNotContain("*", hostile);
        Assert.EndsWith(".lnk", hostile);
        Assert.Equal("MuseDesktop - Profile.lnk",
            Profiles.ShortcutFileName(new AccountProfile { Name = "///" }));
    }
}
