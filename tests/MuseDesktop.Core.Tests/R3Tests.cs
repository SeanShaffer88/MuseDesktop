using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>
/// R3 (v1.3.0, items 121–130): profiles round 2 — avatars, per-profile
/// PINs, scheduled switching, clone, the counts-only health report,
/// profile bundles, groups, templates, usage tallies, and archiving.
/// The invariants that matter most are the negative ones: sign-in data
/// is never copied by anything, a PIN record never travels, an archived
/// profile vanishes from switchers but not from disk or explicit
/// launches, and a manual choice always beats the schedule. The shell
/// pieces stay thin over these.
/// </summary>
public class R3Tests
{
    private static AppSettings TwoProfiles()
    {
        var s = new AppSettings();
        s.Profiles.Add(new AccountProfile { Id = "work", Name = "Work" });
        return s.Sanitized();
    }

    private static AccountProfile Profile(AppSettings s, string id) =>
        s.Profiles.Single(p => p.Id == id);

    // 2026-10-26 is a Monday; all schedule times are local wall clock.
    private static DateTimeOffset On(int dayOffset, int hour, int minute) =>
        new DateTimeOffset(2026, 10, 26, hour, minute, 0, TimeSpan.FromHours(-5)).AddDays(dayOffset);

    private static ProfileSwitchRule Rule(string profileId, int mask, int start, int end) =>
        new() { ProfileId = profileId, DaysMask = mask, StartMinutes = start, EndMinutes = end };

    // ──────────────────────── #121 avatars ────────────────────────

    [Fact]
    public void DisplayLabel_UsesGlyphOverColorDot()
    {
        var p = new AccountProfile { Name = "Work", DisplayColor = "Blue", AvatarGlyph = "Star" };
        Assert.Equal("★ Work", Profiles.DisplayLabel(p));
        // MenuLabel is untouched by avatars (S3 tests pin its shape).
        Assert.Equal("● Work", Profiles.MenuLabel(p));
    }

    [Fact]
    public void DisplayLabel_FallsBackToMenuLabelWithoutGlyph()
    {
        Assert.Equal("● Work", Profiles.DisplayLabel(
            new AccountProfile { Name = "Work", DisplayColor = "Blue" }));
        Assert.Equal("Work", Profiles.DisplayLabel(new AccountProfile { Name = "Work" }));
    }

    [Fact]
    public void AvatarGlyph_IsSanitizedToThePalette()
    {
        var s = TwoProfiles();
        Profile(s, "work").AvatarGlyph = "star";
        s.Sanitized();
        Assert.Equal("Star", Profile(s, "work").AvatarGlyph);
        Profile(s, "work").AvatarGlyph = "bogus-glyph";
        s.Sanitized();
        Assert.Null(Profile(s, "work").AvatarGlyph);
    }

    [Fact]
    public void IconSpec_GlyphColorAndInitialFallbacks()
    {
        var (hex, mark) = ProfileAvatar.IconSpec(
            new AccountProfile { Name = "Work", DisplayColor = "Blue", AvatarGlyph = "Flag" });
        Assert.Equal("#3B82F6", hex);
        Assert.Equal("⚑", mark);

        (hex, mark) = ProfileAvatar.IconSpec(
            new AccountProfile { Name = "Work", DisplayColor = "Green" });
        Assert.Equal("#22A06B", hex);
        Assert.Equal("W", mark);

        (hex, mark) = ProfileAvatar.IconSpec(new AccountProfile { Name = "reading" });
        Assert.Equal(ProfileAvatar.DefaultBackgroundHex, hex);
        Assert.Equal("R", mark);
    }

    // ──────────────────── #122 per-profile lock ────────────────────

    [Fact]
    public void ProfileLock_SetVerifyRemove()
    {
        var p = new AccountProfile { Id = "work", Name = "Work" };
        Assert.False(ProfileLock.IsLockActive(p));
        ProfileLock.SetCredential(p, "2468");
        Assert.True(ProfileLock.IsLockActive(p));
        Assert.True(ProfileLock.Verify("2468", p));
        Assert.False(ProfileLock.Verify("0000", p));
        Assert.False(ProfileLock.Verify(null, p));
        ProfileLock.RemoveCredential(p);
        Assert.False(ProfileLock.IsLockActive(p));
        Assert.False(ProfileLock.Verify("2468", p));
        Assert.Null(p.LockSalt);
        Assert.Null(p.LockHash);
    }

    [Fact]
    public void ProfileLock_RecordWithoutTheSwitchIsNotActive()
    {
        var p = new AccountProfile { Id = "work", Name = "Work" };
        ProfileLock.SetCredential(p, "2468");
        p.LockEnabled = false;
        Assert.True(ProfileLock.HasValidRecord(p));
        Assert.False(ProfileLock.IsLockActive(p));
    }

    [Fact]
    public void ProfileLock_CorruptRecordFailsOpenAtLoad()
    {
        var s = TwoProfiles();
        var p = Profile(s, "work");
        p.LockEnabled = true;
        p.LockSalt = "not-base64!!";
        p.LockHash = "also-not-base64!!";
        p.LockIterations = 210_000;
        s.Sanitized();
        Assert.False(p.LockEnabled);
        Assert.Null(p.LockSalt);
        Assert.Null(p.LockHash);
    }

    // ─────────────────────── #130 archiving ───────────────────────

    [Fact]
    public void Visible_ExcludesArchived_KeepsOrder()
    {
        var s = TwoProfiles();
        s.Profiles.Add(new AccountProfile { Id = "old", Name = "Old", Archived = true });
        Assert.Equal(
            new[] { Profiles.DefaultId, "work" },
            Profiles.VisibleProfiles(s).Select(p => p.Id).ToArray());
        Assert.Equal(3, s.Profiles.Count); // archiving deletes nothing
    }

    [Fact]
    public void ActiveProfile_CannotStayArchived()
    {
        var s = TwoProfiles();
        s.ActiveProfileId = "work";
        Profile(s, "work").Archived = true;
        s.Sanitized();
        Assert.False(Profile(s, "work").Archived);
    }

    [Fact]
    public void ArchivedProfile_StillLaunchesWhenNamedExplicitly()
    {
        var s = TwoProfiles();
        Profile(s, "work").Archived = true;
        var (id, fellBack) = Profiles.ResolveLaunchProfile(s, "work");
        Assert.Equal("work", id);
        Assert.False(fellBack);
    }

    // ─────────────────────── #127 groups ───────────────────────

    [Fact]
    public void Groups_AddNamesAndCap()
    {
        var s = TwoProfiles();
        var g1 = ProfileGroups.Add(s, null);
        var g2 = ProfileGroups.Add(s, null);
        Assert.Equal("Group", g1!.Name);
        Assert.Equal("Group 2", g2!.Name);
        var named = ProfileGroups.Add(s, "  Clients  ");
        Assert.Equal("Clients", named!.Name);
        for (var i = s.ProfileGroups.Count; i < ProfileGroups.MaxGroups; i++)
            Assert.NotNull(ProfileGroups.Add(s, null));
        Assert.Null(ProfileGroups.Add(s, null));
    }

    [Fact]
    public void Groups_MembersAndSwitchTarget()
    {
        var s = TwoProfiles();
        var g = ProfileGroups.Add(s, "Clients")!;
        var work = Profile(s, "work");
        work.GroupId = g.Id;
        work.LastUsedUtc = On(0, 9, 0);
        var personal = new AccountProfile
        {
            Id = "personal", Name = "Personal", GroupId = g.Id,
            LastUsedUtc = On(0, 10, 0),
        };
        s.Profiles.Add(personal);
        // Most recently used member wins.
        Assert.Equal("personal", ProfileGroups.SwitchTarget(s, g.Id)!.Id);
        // An archived member is skipped even when it is the freshest.
        personal.Archived = true;
        personal.LastUsedUtc = On(0, 11, 0);
        Assert.Equal("work", ProfileGroups.SwitchTarget(s, g.Id)!.Id);
        Assert.Equal(2, ProfileGroups.Members(s, g.Id).Count);
        Assert.Single(ProfileGroups.VisibleMembers(s, g.Id));
        // Archiving the last visible member leaves no target.
        work.Archived = true;
        Assert.Null(ProfileGroups.SwitchTarget(s, g.Id));
    }

    [Fact]
    public void Groups_RemoveUngroupsMembers()
    {
        var s = TwoProfiles();
        var g = ProfileGroups.Add(s, "Clients")!;
        Profile(s, "work").GroupId = g.Id;
        Assert.True(ProfileGroups.Remove(s, g.Id));
        Assert.Null(Profile(s, "work").GroupId);
        Assert.Empty(s.ProfileGroups);
        Assert.False(ProfileGroups.Remove(s, g.Id));
    }

    [Fact]
    public void Groups_DanglingMembershipIsRepaired()
    {
        var s = TwoProfiles();
        Profile(s, "work").GroupId = "g-gone";
        s.Sanitized();
        Assert.Null(Profile(s, "work").GroupId);
    }

    [Fact]
    public void Groups_PaletteOffersSwitchToGroup()
    {
        var s = TwoProfiles();
        var g = ProfileGroups.Add(s, "Clients")!;
        Profile(s, "work").GroupId = g.Id;
        var palette = CommandRegistry.CreateDefault(
            Profiles.VisibleProfiles(s), s.ActiveProfileId,
            Array.Empty<DownloadRecord>(), Array.Empty<WindowDescriptor>(),
            groupSettings: s);
        var command = palette.All().Single(c => c.Id == $"profile-group.switch.{g.Id}");
        Assert.Equal("Switch to group: Clients", command.Title);
        Assert.Equal("Opens Work", command.Detail);
        Assert.Equal(g.Id, command.Payload);

        // Without the settings argument there are no group commands.
        var bare = CommandRegistry.CreateDefault(
            Profiles.VisibleProfiles(s), s.ActiveProfileId,
            Array.Empty<DownloadRecord>(), Array.Empty<WindowDescriptor>());
        Assert.DoesNotContain(bare.All(), c => c.Id.StartsWith("profile-group."));
    }

    // ─────────────────────── #128 templates ───────────────────────

    [Fact]
    public void Templates_TheThreeStarters()
    {
        Assert.Equal(new[] { "work", "personal", "reading" },
            ProfileTemplates.All.Select(t => t.Key).ToArray());
        Assert.NotNull(ProfileTemplates.Find("WORK"));
        Assert.Null(ProfileTemplates.Find("nope"));
    }

    [Fact]
    public void Templates_CreateAppliesLookAndStartPage()
    {
        var s = TwoProfiles();
        var work = ProfileTemplates.Create(s, "work")!;
        Assert.Equal("Work 2", work.Name); // "Work" is taken in this fixture
        Assert.Equal("Blue", work.DisplayColor);
        Assert.Equal("Flag", work.AvatarGlyph);
        Assert.Equal(StartPageMode.Home, work.StartPage);
        Assert.True(work.StartAtHome);

        var reading = ProfileTemplates.Create(s, "reading", "Book club")!;
        Assert.Equal("Book club", reading.Name);
        Assert.Equal(1.25, reading.ZoomFactor);
        Assert.Equal(StartPageMode.LastPage, reading.StartPage);
        Assert.False(reading.StartAtHome);
    }

    [Fact]
    public void Templates_CreateDedupesAndRespectsCap()
    {
        var s = new AppSettings().Sanitized();
        Assert.Equal("Work", ProfileTemplates.Create(s, "work")!.Name);
        Assert.Equal("Work 2", ProfileTemplates.Create(s, "work")!.Name);
        while (s.Profiles.Count < Profiles.MaxProfiles)
            s.Profiles.Add(new AccountProfile { Id = Profiles.NewId(), Name = "Filler" });
        Assert.Null(ProfileTemplates.Create(s, "personal"));
        Assert.Null(ProfileTemplates.Create(s, "unknown-key"));
    }

    // ──────────────────────── #124 clone ────────────────────────

    [Fact]
    public void Clone_CopiesSettingsAndAppearanceOnly()
    {
        var s = TwoProfiles();
        var work = Profile(s, "work");
        work.DisplayColor = "Purple";
        work.AvatarGlyph = "Moon";
        work.StartPage = StartPageMode.Custom;
        work.CustomStartUrl = "https://muse.ai/projects";
        work.DownloadsFolder = @"C:\Work Downloads";
        work.ZoomFactor = 1.5;
        work.DataCapBytes = ProfileDataCaps.Gb1;
        work.LastUsedUtc = On(0, 9, 0);
        var g = ProfileGroups.Add(s, "Clients")!;
        work.GroupId = g.Id;
        ProfileLock.SetCredential(work, "1357");
        work.Archived = false;

        var clone = Profiles.Clone(s, "work")!;
        Assert.NotEqual("work", clone.Id);
        Assert.Equal("Work copy", clone.Name);
        Assert.Equal("Purple", clone.DisplayColor);
        Assert.Equal("Moon", clone.AvatarGlyph);
        Assert.Equal(StartPageMode.Custom, clone.StartPage);
        Assert.Equal("https://muse.ai/projects", clone.CustomStartUrl);
        Assert.Equal(@"C:\Work Downloads", clone.DownloadsFolder);
        Assert.Equal(1.5, clone.ZoomFactor);
        Assert.Equal(ProfileDataCaps.Gb1, clone.DataCapBytes);
        Assert.Equal(g.Id, clone.GroupId);
        // Never copied: history, credentials, archive state.
        Assert.Null(clone.LastUsedUtc);
        Assert.False(ProfileLock.IsLockActive(clone));
        Assert.Null(clone.LockSalt);
        Assert.False(clone.Archived);
        // The source is untouched and the clone sits right after it.
        Assert.True(ProfileLock.IsLockActive(work));
        Assert.Equal(clone.Id, s.Profiles[s.Profiles.IndexOf(work) + 1].Id);

        var second = Profiles.Clone(s, "work")!;
        Assert.Equal("Work copy 2", second.Name);
    }

    [Fact]
    public void Clone_UnknownOrAtCap_ReturnsNull()
    {
        var s = TwoProfiles();
        Assert.Null(Profiles.Clone(s, "nope"));
        while (s.Profiles.Count < Profiles.MaxProfiles)
            s.Profiles.Add(new AccountProfile { Id = Profiles.NewId(), Name = "Filler" });
        Assert.Null(Profiles.Clone(s, "work"));
    }

    [Fact]
    public void Clone_OfArchivedSource_IsNotArchived()
    {
        var s = TwoProfiles();
        Profile(s, "work").Archived = true;
        var clone = Profiles.Clone(s, "work")!;
        Assert.False(clone.Archived);
    }

    // ─────────────────── #123 scheduled switching ───────────────────

    [Fact]
    public void Schedule_ContainsSameDayWindow()
    {
        var rule = Rule("work", ProfileSchedule.WeekdaysMask, 9 * 60, 17 * 60);
        Assert.True(ProfileSchedule.Contains(rule, On(0, 10, 0)));   // Mon 10:00
        Assert.True(ProfileSchedule.Contains(rule, On(0, 9, 0)));    // start is inclusive
        Assert.False(ProfileSchedule.Contains(rule, On(0, 8, 59)));
        Assert.False(ProfileSchedule.Contains(rule, On(0, 17, 0))); // end is exclusive
        Assert.False(ProfileSchedule.Contains(rule, On(5, 10, 0))); // Saturday
    }

    [Fact]
    public void Schedule_ContainsWrappingWindow()
    {
        // Friday 22:00 – Saturday 06:00, masked on the START day (Fri).
        var rule = Rule("work", ProfileSchedule.MaskFor(DayOfWeek.Friday), 22 * 60, 6 * 60);
        Assert.True(ProfileSchedule.Contains(rule, On(4, 23, 0)));  // Fri 23:00
        Assert.True(ProfileSchedule.Contains(rule, On(5, 2, 0)));   // Sat 02:00
        Assert.False(ProfileSchedule.Contains(rule, On(5, 6, 0)));  // Sat 06:00 (end)
        Assert.False(ProfileSchedule.Contains(rule, On(5, 23, 0))); // Sat night isn't Friday's window
        Assert.False(ProfileSchedule.Contains(rule, On(3, 23, 0))); // Thu 23:00
        Assert.False(ProfileSchedule.Contains(rule, On(4, 21, 59)));
    }

    [Fact]
    public void Schedule_WindowEnd()
    {
        var day = Rule("work", ProfileSchedule.WeekdaysMask, 9 * 60, 17 * 60);
        Assert.Equal(On(0, 17, 0), ProfileSchedule.WindowEnd(day, On(0, 10, 0)));
        var night = Rule("work", ProfileSchedule.MaskFor(DayOfWeek.Friday), 22 * 60, 6 * 60);
        Assert.Equal(On(5, 6, 0), ProfileSchedule.WindowEnd(night, On(4, 23, 0))); // ends next morning
        Assert.Equal(On(5, 6, 0), ProfileSchedule.WindowEnd(night, On(5, 2, 0)));  // small-hours part ends today
    }

    [Fact]
    public void Schedule_NextWindowStart()
    {
        var rule = Rule("work", ProfileSchedule.WeekdaysMask, 9 * 60, 17 * 60);
        Assert.Equal(On(0, 9, 0), ProfileSchedule.NextWindowStart(rule, On(0, 8, 0)));
        Assert.Equal(On(1, 9, 0), ProfileSchedule.NextWindowStart(rule, On(0, 10, 0))); // inside → tomorrow
        Assert.Equal(On(7, 9, 0), ProfileSchedule.NextWindowStart(rule, On(4, 18, 0))); // Fri evening → Monday (+7d from Mon = next Mon)
    }

    [Fact]
    public void Schedule_EvaluateSuggestsTheWindowsProfile()
    {
        var s = TwoProfiles();
        ProfileSchedule.SetRule(s, Rule("work", ProfileSchedule.WeekdaysMask, 9 * 60, 17 * 60));
        s.Sanitized();
        var decision = ProfileSchedule.Evaluate(s, On(0, 10, 0), Profiles.DefaultId);
        Assert.True(decision.ShouldSwitch);
        Assert.Equal("work", decision.ProfileId);
        // Already there: nothing to suggest.
        Assert.False(ProfileSchedule.Evaluate(s, On(0, 10, 0), "work").ShouldSwitch);
        // Outside the window: nothing to suggest.
        Assert.False(ProfileSchedule.Evaluate(s, On(0, 20, 0), Profiles.DefaultId).ShouldSwitch);
    }

    [Fact]
    public void Schedule_ManualHoldWinsUntilItExpires()
    {
        var s = TwoProfiles();
        ProfileSchedule.SetRule(s, Rule("work", ProfileSchedule.WeekdaysMask, 9 * 60, 17 * 60));
        s.Sanitized();
        // The user picks Default by hand at 10:00, mid-window: the
        // hold covers the rest of the window…
        ProfileSchedule.RecordManualSwitch(s, Profiles.DefaultId, On(0, 10, 0));
        Assert.Equal(Profiles.DefaultId, s.ScheduleHoldProfileId);
        Assert.Equal(On(0, 17, 0), s.ScheduleHoldUntilUtc);
        Assert.False(ProfileSchedule.Evaluate(s, On(0, 11, 0), Profiles.DefaultId).ShouldSwitch);
        // …and the next day's window suggests again.
        Assert.True(ProfileSchedule.Evaluate(s, On(1, 10, 0), Profiles.DefaultId).ShouldSwitch);
    }

    [Fact]
    public void Schedule_HoldOutsideAWindowLastsUntilTheNextWindowStarts()
    {
        var s = TwoProfiles();
        ProfileSchedule.SetRule(s, Rule("work", ProfileSchedule.WeekdaysMask, 9 * 60, 17 * 60));
        s.Sanitized();
        ProfileSchedule.RecordManualSwitch(s, Profiles.DefaultId, On(0, 7, 30));
        Assert.Equal(On(0, 9, 0), s.ScheduleHoldUntilUtc);
    }

    [Fact]
    public void Schedule_SanitizeDropsDeadRules()
    {
        var s = TwoProfiles();
        Profile(s, "work").Archived = true;
        var ghost = new AccountProfile { Id = "ghost", Name = "Ghost", Archived = true };
        s.Profiles.Add(ghost);
        s.ProfileSwitchRules.Add(Rule("work", ProfileSchedule.WeekdaysMask, 9 * 60, 17 * 60)); // archived → drop
        s.ProfileSwitchRules.Add(Rule("missing", ProfileSchedule.WeekdaysMask, 9 * 60, 17 * 60)); // no profile → drop
        s.ProfileSwitchRules.Add(Rule(Profiles.DefaultId, 0, 9 * 60, 17 * 60)); // invalid mask → drop
        s.ProfileSwitchRules.Add(Rule(Profiles.DefaultId, ProfileSchedule.WeekdaysMask, 9 * 60, 9 * 60)); // empty window → drop
        s.ProfileSwitchRules.Add(Rule(Profiles.DefaultId, ProfileSchedule.WeekdaysMask, 9 * 60, 17 * 60)); // the keeper
        s.ProfileSwitchRules.Add(Rule(Profiles.DefaultId, ProfileSchedule.WeekdaysMask, 10 * 60, 18 * 60)); // duplicate → drop
        s.Sanitized();
        var rule = Assert.Single(s.ProfileSwitchRules);
        Assert.Equal(9 * 60, rule.StartMinutes);
    }

    [Fact]
    public void Schedule_HoldRepairAndText()
    {
        var s = TwoProfiles();
        s.ScheduleHoldProfileId = "missing";
        s.ScheduleHoldUntilUtc = On(0, 17, 0);
        s.Sanitized();
        Assert.Null(s.ScheduleHoldProfileId);
        Assert.Null(s.ScheduleHoldUntilUtc);

        Assert.Equal(545, ProfileSchedule.ParseMinutes("9:05"));
        Assert.Equal(0, ProfileSchedule.ParseMinutes("0:00"));
        Assert.Null(ProfileSchedule.ParseMinutes("24:00"));
        Assert.Null(ProfileSchedule.ParseMinutes("9"));
        Assert.Null(ProfileSchedule.ParseMinutes("nine"));
        Assert.Equal("09:05", ProfileSchedule.FormatMinutes(545));
        Assert.Equal("09:00–17:00 · Weekdays",
            ProfileSchedule.Describe(Rule("work", ProfileSchedule.WeekdaysMask, 9 * 60, 17 * 60)));
        Assert.Contains("past midnight",
            ProfileSchedule.Describe(Rule("work", ProfileSchedule.MaskFor(DayOfWeek.Friday), 22 * 60, 6 * 60)));
    }

    // ───────────────────── #125 health report ─────────────────────

    [Fact]
    public void Health_FullFactsRenderCountsOnly()
    {
        var p = new AccountProfile
        {
            Id = "work", Name = "Work",
            LastUsedUtc = On(0, 9, 30),
            DataCapBytes = ProfileDataCaps.Gb1,
        };
        var report = ProfileHealth.Build(p, new ProfileHealthFacts(
            DataBytes: 1536, CacheBytes: 1024, CookieCount: 1234, LastBackupUtc: On(0, 8, 0)));
        var byLabel = report.Lines.ToDictionary(l => l.Label, l => l.Value);
        Assert.Equal("1.5 KB", byLabel["Data on this PC"]);
        Assert.Equal("1 KB", byLabel["Disposable cache"]);
        Assert.Equal("1,234", byLabel["Cookies"]);
        // The report renders local time; compute the expectation the
        // same way so the test holds in any machine timezone.
        Assert.Equal(On(0, 9, 30).ToLocalTime().ToString("yyyy-MM-dd HH:mm"), byLabel["Last used"]);
        Assert.Equal(On(0, 8, 0).ToLocalTime().ToString("yyyy-MM-dd"), byLabel["Last settings backup"]);
        Assert.Equal("1 GB", byLabel["Data cap"]);
        Assert.Contains("1,234 cookies", report.Summary);
    }

    [Fact]
    public void Health_UnknownFactsSaySoNeverZero()
    {
        var p = new AccountProfile { Id = "work", Name = "Work" };
        var report = ProfileHealth.Build(p, new ProfileHealthFacts(null, null, null, null));
        var byLabel = report.Lines.ToDictionary(l => l.Label, l => l.Value);
        Assert.Equal("No data on this PC yet", byLabel["Data on this PC"]);
        Assert.Equal("Couldn't be measured", byLabel["Disposable cache"]);
        Assert.Equal("Couldn't be counted right now", byLabel["Cookies"]);
        Assert.Equal("Never", byLabel["Last used"]);
        Assert.Equal("No backup yet", byLabel["Last settings backup"]);
        Assert.Equal("Off", byLabel["Data cap"]);
    }

    // ─────────────────── #126 profile bundles ───────────────────

    [Fact]
    public void Bundle_RoundTripsSettingsAndAppearance()
    {
        var source = new AccountProfile
        {
            Id = "work", Name = "Work",
            DisplayColor = "Purple", AvatarGlyph = "Moon",
            StartPage = StartPageMode.Custom, CustomStartUrl = "https://muse.ai/projects",
            DownloadsFolder = @"C:\Work Downloads", ZoomFactor = 1.5,
            DataCapBytes = ProfileDataCaps.Gb1,
        };
        var text = ProfileBundle.Export(source, null);
        Assert.False(ProfileBundle.NeedsPassword(text));
        Assert.True(ProfileBundle.TryImport(text, null, out var imported, out var error,
            idFactory: () => "pimported1"));
        Assert.Null(error);
        Assert.Equal("pimported1", imported!.Id); // fresh id, never the source's
        Assert.Equal("Work", imported.Name);
        Assert.Equal("Purple", imported.DisplayColor);
        Assert.Equal("Moon", imported.AvatarGlyph);
        Assert.Equal(StartPageMode.Custom, imported.StartPage);
        Assert.Equal("https://muse.ai/projects", imported.CustomStartUrl);
        Assert.Equal(@"C:\Work Downloads", imported.DownloadsFolder);
        Assert.Equal(1.5, imported.ZoomFactor);
        Assert.Equal(ProfileDataCaps.Gb1, imported.DataCapBytes);
    }

    [Fact]
    public void Bundle_NeverCarriesLockGroupArchiveOrHistory()
    {
        var s = TwoProfiles();
        var work = Profile(s, "work");
        var g = ProfileGroups.Add(s, "Clients")!;
        work.GroupId = g.Id;
        work.LastUsedUtc = On(0, 9, 0);
        ProfileLock.SetCredential(work, "1357");

        var text = ProfileBundle.Export(work, null);
        Assert.True(ProfileBundle.TryImport(text, null, out var imported, out _));
        var imp = imported!;
        Assert.False(ProfileLock.IsLockActive(imp));
        Assert.Null(imp.LockSalt);
        Assert.Null(imp.GroupId);
        Assert.False(imp.Archived);
        Assert.Null(imp.LastUsedUtc);
    }

    [Fact]
    public void Bundle_EncryptedRoundTripAndPasswordErrors()
    {
        var source = new AccountProfile { Id = "work", Name = "Work" };
        var text = ProfileBundle.Export(source, "bundle-pw");
        Assert.True(ProfileBundle.NeedsPassword(text));
        Assert.False(ProfileBundle.TryImport(text, null, out _, out var noPwError));
        Assert.Contains("encrypted", noPwError);
        Assert.False(ProfileBundle.TryImport(text, "wrong-pw", out _, out var wrongError));
        Assert.NotNull(wrongError);
        Assert.True(ProfileBundle.TryImport(text, "bundle-pw", out var imported, out _));
        Assert.Equal("Work", imported!.Name);
    }

    [Fact]
    public void Bundle_RejectsNonBundles()
    {
        Assert.False(ProfileBundle.TryImport("just some text", null, out _, out var e1));
        Assert.Contains("isn't a MuseDesktop profile bundle", e1);
        // A plain settings export is not a bundle.
        Assert.False(ProfileBundle.TryImport("{\"schemaVersion\":3}", null, out _, out var e2));
        Assert.Contains("isn't a MuseDesktop profile bundle", e2);
        // An ENCRYPTED settings export decrypts fine but still isn't a bundle.
        var settingsEncrypted = EncryptedExport.Encrypt("{\"schemaVersion\":3}", "pw");
        Assert.False(ProfileBundle.TryImport(settingsEncrypted, "pw", out _, out var e3));
        Assert.Contains("isn't a MuseDesktop profile bundle", e3);
        // A newer bundle version is refused with the reason.
        var newer = ProfileBundle.MagicLine + "\n"
            + "{\"kind\":\"MuseDesktopProfile\",\"version\":99,\"profile\":{\"id\":\"x\",\"name\":\"X\"}}";
        Assert.False(ProfileBundle.TryImport(newer, null, out _, out var e4));
        Assert.Contains("newer version", e4);
        // Damaged JSON after the magic line.
        var damaged = ProfileBundle.MagicLine + "\n{not json";
        Assert.False(ProfileBundle.TryImport(damaged, null, out _, out var e5));
        Assert.Contains("damaged", e5);
    }

    [Fact]
    public void Bundle_ImportSanitizesHandEditedFields()
    {
        var json = ProfileBundle.MagicLine + "\n"
            + "{\"kind\":\"MuseDesktopProfile\",\"version\":1,\"profile\":{"
            + "\"id\":\"work\",\"name\":\"  Edited  \",\"displayColor\":\"Neon\","
            + "\"avatarGlyph\":\"Bogus\",\"zoomFactor\":99,\"dataCapBytes\":12345}}";
        Assert.True(ProfileBundle.TryImport(json, null, out var imported, out _,
            idFactory: () => "pimported2"));
        Assert.Equal("Edited", imported!.Name);
        Assert.Null(imported.DisplayColor);
        Assert.Null(imported.AvatarGlyph);
        Assert.Equal(ZoomMath.Clamp(99), imported.ZoomFactor);
        Assert.Equal(0, imported.DataCapBytes); // not an offered size → off
    }

    // ────────────────────── #129 usage tallies ──────────────────────

    [Fact]
    public void Usage_RecordLaunchAccumulates()
    {
        var usage = new ProfileUsage();
        usage.RecordLaunch("work", On(0, 9, 0));
        usage.RecordLaunch("work", On(0, 10, 0));
        usage.RecordLaunch(Profiles.DefaultId, On(0, 11, 0));
        Assert.Equal(2, usage.EntryFor("work")!.Launches);
        Assert.Equal(On(0, 10, 0), usage.EntryFor("work")!.LastLaunchUtc);
        Assert.Equal(3, usage.TotalLaunches);
    }

    [Fact]
    public void Usage_StoreRoundTripAndClear()
    {
        var dir = Path.Combine(Path.GetTempPath(), "musedesktop-r3-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "profile-usage.json");
        try
        {
            Assert.Equal(0, ProfileUsageStore.Load(path).TotalLaunches); // missing → empty
            var usage = new ProfileUsage();
            usage.RecordLaunch("work", On(0, 9, 0));
            ProfileUsageStore.Save(path, usage);
            Assert.Equal(1, ProfileUsageStore.Load(path).TotalLaunches);
            File.WriteAllText(path, "{ corrupt");
            Assert.Equal(0, ProfileUsageStore.Load(path).TotalLaunches); // damaged → empty
            ProfileUsageStore.Save(path, usage);
            ProfileUsageStore.Clear(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
    }

    [Fact]
    public void Usage_StoreRepairsHandEditedCounts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "musedesktop-r3-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "profile-usage.json");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path,
                "{\"byProfile\":{\"work\":{\"launches\":-3,\"lastLaunchUtc\":null},"
                + "\"bad id!\":{\"launches\":9,\"lastLaunchUtc\":null}}}");
            var usage = ProfileUsageStore.Load(path);
            Assert.Equal(0, usage.EntryFor("work")!.Launches); // negative clamps to 0
            Assert.Null(usage.EntryFor("bad id!"));            // impossible id drops
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
    }

    [Fact]
    public void Usage_ReportRowsCoverEveryProfile()
    {
        var s = TwoProfiles();
        Profile(s, "work").LastUsedUtc = On(0, 9, 0);
        var usage = new ProfileUsage();
        usage.RecordLaunch("work", On(0, 10, 0));
        usage.RecordLaunch("work", On(0, 11, 0));
        var report = ProfileUsageReport.Build(s, usage);
        Assert.Equal(2, report.Rows.Count);
        Assert.Equal(2, report.TotalLaunches);
        var workRow = report.Rows.Single(r => r.ProfileId == "work");
        Assert.Equal(2, workRow.Launches);
        Assert.Equal(On(0, 11, 0), workRow.LastUsedUtc); // the tally's fresher stamp
        var defaultRow = report.Rows.Single(r => r.ProfileId == Profiles.DefaultId);
        Assert.Equal(0, defaultRow.Launches);
        Assert.Null(defaultRow.LastUsedUtc);
    }

    // ──────────────── reset + window descriptor seams ────────────────

    [Fact]
    public void Reset_ScheduleAutoSwitchResets_GroupsAndRulesSurvive()
    {
        var s = TwoProfiles();
        var g = ProfileGroups.Add(s, "Clients")!;
        ProfileSchedule.SetRule(s, Rule("work", ProfileSchedule.WeekdaysMask, 9 * 60, 17 * 60));
        s.ProfileScheduleAutoSwitch = true;
        SettingsReset.ApplyDefaults(s);
        Assert.False(s.ProfileScheduleAutoSwitch);
        // Groups and rules are the user's own organization — a
        // preferences reset preserves them untouched.
        Assert.Single(s.ProfileGroups);
        Assert.Single(s.ProfileSwitchRules);
        Assert.Equal(g.Id, s.ProfileGroups[0].Id);
    }

    [Fact]
    public void WindowDescriptor_GlyphKeyIsOptionalAndTrailing()
    {
        var plain = new WindowDescriptor("main", "A chat", "Default", null,
            IsMain: true, SupportsFocusMode: true, IsActive: true);
        Assert.Null(plain.ProfileGlyphKey);
        var marked = plain with { ProfileGlyphKey = "Star" };
        Assert.Equal("Star", marked.ProfileGlyphKey);
    }
}
