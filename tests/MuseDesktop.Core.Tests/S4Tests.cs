using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>v0.8.0 Slice 4 (items 31–40) Core additions: the title
/// attention parser (#31), the tray icon plan (#31/#37), the suspend
/// timeout + policy (#34/#35), quiet hours + the notification gate
/// (#38), launch visibility (#40), and the settings plumbing for all
/// of it (sanitize/clone/export-import).</summary>
public class S4Tests
{
    // ── #31 attention title parsing ──

    [Theory]
    [InlineData("(3) Muse", 3)]
    [InlineData("(12) New chat — Muse", 12)]
    [InlineData("(1) Muse", 1)]
    [InlineData("  (2) Muse", 2)] // leading whitespace tolerated
    [InlineData("(999) Muse", 999)]
    public void Attention_ParsesLeadingCountPrefix(string title, int expected)
    {
        var signal = AttentionSignal.Parse(title);
        Assert.Equal(AttentionKind.Count, signal.Kind);
        Assert.Equal(expected, signal.Count);
        Assert.True(signal.HasAttention);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Muse")]
    [InlineData("New conversation")]
    [InlineData("(0) Muse")] // zero waiting is no attention
    [InlineData("(Official) Muse")] // prose in parens is not a count
    [InlineData("Muse (3)")] // not leading — could be anything
    [InlineData("(3)Muse")] // no separator — a name, not a badge
    [InlineData("((3)) Muse")]
    [InlineData("( 3) Muse")]
    [InlineData("(12345) Muse")] // implausible width; treated as prose
    public void Attention_RejectsEverythingElse(string? title)
    {
        var signal = AttentionSignal.Parse(title);
        Assert.Equal(AttentionKind.None, signal.Kind);
        Assert.False(signal.HasAttention);
    }

    // ── #31/#37 tray icon plan ──

    [Fact]
    public void TrayPlan_CombinesDotAndBadge()
    {
        var plan = TrayIconPlanner.Plan(true, AttentionSignal.Parse("(3) Muse"));
        Assert.True(plan.ShowProfileDot);
        Assert.True(plan.ShowBadge);
        Assert.Equal("3", plan.BadgeText);
    }

    [Fact]
    public void TrayPlan_BadgeTextCapsAtNinePlus()
    {
        Assert.Equal("9+", TrayIconPlanner.BadgeTextFor(10));
        Assert.Equal("9+", TrayIconPlanner.BadgeTextFor(250));
        Assert.Equal("9", TrayIconPlanner.BadgeTextFor(9));
    }

    [Fact]
    public void TrayPlan_QuietTitleMeansPlainIcon()
    {
        var plan = TrayIconPlanner.Plan(false, AttentionSignal.Parse("Muse"));
        Assert.False(plan.ShowProfileDot);
        Assert.False(plan.ShowBadge);
        Assert.Null(plan.BadgeText);
    }

    [Fact]
    public void TrayPlan_UncountedDotHasBadgeWithoutText()
    {
        var plan = TrayIconPlanner.Plan(false, new AttentionSignal(AttentionKind.Dot, 0));
        Assert.True(plan.ShowBadge);
        Assert.Null(plan.BadgeText);
    }

    [Fact]
    public void TrayPlan_TooltipSuffixNeverInventsACount()
    {
        Assert.Equal(" · 4 unread", TrayIconPlanner.TooltipSuffix(AttentionSignal.Parse("(4) Muse")));
        Assert.Equal(" · attention", TrayIconPlanner.TooltipSuffix(new AttentionSignal(AttentionKind.Dot, 0)));
        Assert.Equal("", TrayIconPlanner.TooltipSuffix(AttentionSignal.None));
    }

    // ── #34 suspend timeout ──

    [Fact]
    public void SuspendTimeout_Delays()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), SuspendTimeouts.Delay(SuspendTimeout.OneMinute));
        Assert.Equal(TimeSpan.FromMinutes(5), SuspendTimeouts.Delay(SuspendTimeout.FiveMinutes));
        Assert.Equal(TimeSpan.FromMinutes(15), SuspendTimeouts.Delay(SuspendTimeout.FifteenMinutes));
        Assert.Null(SuspendTimeouts.Delay(SuspendTimeout.Never));
    }

    [Fact]
    public void SuspendTimeout_BadEnumSanitizesToFiveMinutes()
    {
        var settings = new AppSettings { TraySuspendTimeout = (SuspendTimeout)999 }.Sanitized();
        Assert.Equal(SuspendTimeout.FiveMinutes, settings.TraySuspendTimeout);
    }

    // ── #34/#35 suspend policy matrix ──

    [Fact]
    public void SuspendPolicy_HappyPathSuspends()
    {
        Assert.Equal(SuspendDecision.SuspendNow,
            SuspendPolicy.Decide(true, SuspendTimeout.FiveMinutes, downloadsActive: false, microphoneGrantedThisSession: false));
    }

    [Fact]
    public void SuspendPolicy_MasterSwitchBeatsEverything()
    {
        Assert.Equal(SuspendDecision.Disabled,
            SuspendPolicy.Decide(false, SuspendTimeout.FiveMinutes, downloadsActive: true, microphoneGrantedThisSession: true));
        Assert.Equal(SuspendDecision.Disabled,
            SuspendPolicy.Decide(false, SuspendTimeout.Never, downloadsActive: false, microphoneGrantedThisSession: false));
    }

    [Fact]
    public void SuspendPolicy_NeverBeatsTransientBlocks()
    {
        Assert.Equal(SuspendDecision.NeverSuspend,
            SuspendPolicy.Decide(true, SuspendTimeout.Never, downloadsActive: true, microphoneGrantedThisSession: true));
    }

    [Fact]
    public void SuspendPolicy_DownloadBeatsMicrophone()
    {
        Assert.Equal(SuspendDecision.BlockedByDownload,
            SuspendPolicy.Decide(true, SuspendTimeout.OneMinute, downloadsActive: true, microphoneGrantedThisSession: true));
    }

    [Fact]
    public void SuspendPolicy_MicrophoneGrantBlocks()
    {
        Assert.Equal(SuspendDecision.BlockedByMicrophone,
            SuspendPolicy.Decide(true, SuspendTimeout.FifteenMinutes, downloadsActive: false, microphoneGrantedThisSession: true));
    }

    // ── #38 quiet hours ──

    [Fact]
    public void QuietHours_SameDayWindow()
    {
        // 09:00–17:00
        Assert.True(QuietHours.IsActive(540, 1020, 720)); // noon
        Assert.True(QuietHours.IsActive(540, 1020, 540)); // start inclusive
        Assert.False(QuietHours.IsActive(540, 1020, 1020)); // end exclusive
        Assert.False(QuietHours.IsActive(540, 1020, 480));
        Assert.False(QuietHours.IsActive(540, 1020, 1080));
    }

    [Fact]
    public void QuietHours_WrapsMidnight()
    {
        // 22:00–07:00
        Assert.True(QuietHours.IsActive(1320, 420, 1380)); // 23:00
        Assert.True(QuietHours.IsActive(1320, 420, 0)); // midnight
        Assert.True(QuietHours.IsActive(1320, 420, 180)); // 03:00
        Assert.False(QuietHours.IsActive(1320, 420, 720)); // noon
        Assert.False(QuietHours.IsActive(1320, 420, 420)); // end exclusive
        Assert.True(QuietHours.IsActive(1320, 420, 1320)); // start inclusive
    }

    [Fact]
    public void QuietHours_EqualStartAndEndIsNeverActive()
    {
        Assert.False(QuietHours.IsActive(600, 600, 600));
        Assert.False(QuietHours.IsActive(600, 600, 0));
    }

    [Fact]
    public void QuietHours_InvalidMinutesAreNeverActive()
    {
        Assert.False(QuietHours.IsActive(-1, 420, 100));
        Assert.False(QuietHours.IsActive(1320, 1440, 100));
        Assert.False(QuietHours.IsActive(1320, 420, 2000));
        Assert.False(QuietHours.IsValidMinutes(1440));
        Assert.True(QuietHours.IsValidMinutes(1439));
    }

    [Fact]
    public void QuietHours_MinutesOfDay()
    {
        Assert.Equal(1320, QuietHours.MinutesOfDay(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero)));
        Assert.Equal(0, QuietHours.MinutesOfDay(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero)));
    }

    // ── #38 notification policy ──

    private static DateTimeOffset At(int hour) => new(2026, 10, 24, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NotificationPolicy_WindowsDndWinsFirst()
    {
        // Even with quiet hours also active, the reason reported is Windows'.
        Assert.Equal(NotificationSuppression.WindowsDoNotDisturb,
            NotificationPolicy.Evaluate(true, true, 1320, 420, At(23)));
        Assert.Equal(NotificationSuppression.WindowsDoNotDisturb,
            NotificationPolicy.Evaluate(true, false, 1320, 420, At(12)));
    }

    [Fact]
    public void NotificationPolicy_QuietHoursSuppress()
    {
        Assert.Equal(NotificationSuppression.QuietHours,
            NotificationPolicy.Evaluate(false, true, 1320, 420, At(23)));
        Assert.Equal(NotificationSuppression.QuietHours,
            NotificationPolicy.Evaluate(false, true, 1320, 420, At(3)));
    }

    [Fact]
    public void NotificationPolicy_OutsideTheWindowShows()
    {
        Assert.Equal(NotificationSuppression.None,
            NotificationPolicy.Evaluate(false, true, 1320, 420, At(12)));
        Assert.Equal(NotificationSuppression.None,
            NotificationPolicy.Evaluate(false, false, 1320, 420, At(23)));
        Assert.Equal(NotificationSuppression.None,
            NotificationPolicy.Evaluate(false, false, 1320, 420, At(12)));
    }

    // ── #40 launch visibility ──

    [Theory]
    [InlineData(true, true, true)] // autostart + wants tray = hidden
    [InlineData(true, false, false)] // autostart, wants visible = shown
    [InlineData(false, true, false)] // manual launch always shows
    [InlineData(false, false, false)]
    public void LaunchVisibility_OnlyBackgroundLaunchesHide(bool background, bool minimized, bool expected)
    {
        Assert.Equal(expected, LaunchVisibility.ShouldStartHidden(background, minimized));
    }

    // ── #33 command line ──

    [Fact]
    public void CommandLine_SettingsFlag()
    {
        var o = CommandLine.Parse(new[] { "--settings" });
        Assert.True(o.OpenSettings);

        var combined = CommandLine.Parse(new[] { "--profile", "work", "--SETTINGS" });
        Assert.True(combined.OpenSettings);
        Assert.Equal("work", combined.ProfileId);

        Assert.False(CommandLine.Parse(Array.Empty<string>()).OpenSettings);
    }

    // ── tray status line (#32) ──

    [Fact]
    public void TrayStatusLine_CoversTheStates()
    {
        Assert.Equal("Web view: suspended (memory saved)",
            TrayStatusText.SuspendLine(hidden: true, suspended: true, suspendEnabled: true, SuspendTimeout.FiveMinutes));
        Assert.Equal("Web view: suspend off",
            TrayStatusText.SuspendLine(hidden: true, suspended: false, suspendEnabled: false, SuspendTimeout.FiveMinutes));
        Assert.Equal("Web view: stays awake in the tray",
            TrayStatusText.SuspendLine(hidden: true, suspended: false, suspendEnabled: true, SuspendTimeout.Never));
        Assert.Equal("Web view: suspends after 5 min hidden",
            TrayStatusText.SuspendLine(hidden: true, suspended: false, suspendEnabled: true, SuspendTimeout.FiveMinutes));
        Assert.Equal("Web view: awake",
            TrayStatusText.SuspendLine(hidden: false, suspended: false, suspendEnabled: true, SuspendTimeout.FiveMinutes));
    }

    // ── settings plumbing: sanitize / clone / export-import ──

    [Fact]
    public void Settings_S4Defaults()
    {
        var s = new AppSettings();
        Assert.True(s.StartMinimizedToTray); // preserves the long-standing autostart behavior
        Assert.Equal(SuspendTimeout.FiveMinutes, s.TraySuspendTimeout); // the old fixed delay
        Assert.False(s.TraySingleClickRestore);
        Assert.False(s.FlashTaskbarOnAttention);
        Assert.False(s.QuietHoursEnabled);
        Assert.Equal(22 * 60, s.QuietHoursStartMinutes);
        Assert.Equal(7 * 60, s.QuietHoursEndMinutes);
    }

    [Fact]
    public void Settings_BadQuietHoursPairResetsBoth()
    {
        var s = new AppSettings { QuietHoursStartMinutes = -5, QuietHoursEndMinutes = 300 }.Sanitized();
        Assert.Equal(22 * 60, s.QuietHoursStartMinutes);
        Assert.Equal(7 * 60, s.QuietHoursEndMinutes);

        var s2 = new AppSettings { QuietHoursStartMinutes = 600, QuietHoursEndMinutes = 1440 }.Sanitized();
        Assert.Equal(22 * 60, s2.QuietHoursStartMinutes);
        Assert.Equal(7 * 60, s2.QuietHoursEndMinutes);
    }

    [Fact]
    public void Settings_S4FieldsRoundTripThroughCloneAndExportImport()
    {
        var original = new AppSettings
        {
            StartMinimizedToTray = false,
            TraySuspendTimeout = SuspendTimeout.FifteenMinutes,
            TraySingleClickRestore = true,
            FlashTaskbarOnAttention = true,
            QuietHoursEnabled = true,
            QuietHoursStartMinutes = 23 * 60,
            QuietHoursEndMinutes = 6 * 60,
        };

        var clone = original.Clone();
        AssertS4FieldsEqual(original, clone);

        var json = SettingsStore.Export(original);
        Assert.True(SettingsStore.TryImport(json, out var imported, out var error), error);
        Assert.NotNull(imported);
        AssertS4FieldsEqual(original, imported);
    }

    private static void AssertS4FieldsEqual(AppSettings expected, AppSettings actual)
    {
        Assert.Equal(expected.StartMinimizedToTray, actual.StartMinimizedToTray);
        Assert.Equal(expected.TraySuspendTimeout, actual.TraySuspendTimeout);
        Assert.Equal(expected.TraySingleClickRestore, actual.TraySingleClickRestore);
        Assert.Equal(expected.FlashTaskbarOnAttention, actual.FlashTaskbarOnAttention);
        Assert.Equal(expected.QuietHoursEnabled, actual.QuietHoursEnabled);
        Assert.Equal(expected.QuietHoursStartMinutes, actual.QuietHoursStartMinutes);
        Assert.Equal(expected.QuietHoursEndMinutes, actual.QuietHoursEndMinutes);
    }
}
