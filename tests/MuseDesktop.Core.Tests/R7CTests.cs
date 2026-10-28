using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── R7C/R7C2: the approval beep's policy ─────────────────────

/// <summary>
/// The approval-beep policy (R7C; outcomes + reminders R7C2): an
/// appearance is judged exactly once, at its first report — the
/// setting must be on, the window must not be active, and the
/// global cooldown must have elapsed. A card that beeped enters
/// the alerting state and reminds every 20 s while it waits and
/// the user stays away; the window gaining focus (or the setting
/// going off) ends that card's cycle, and a disappearance re-arms.
/// Every judgment is returned as an outcome so the shell can log
/// beeps AND suppressions with their reasons.
/// </summary>
public class ApprovalAttentionPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static ApprovalBeepOutcome Appear(
        ApprovalAttentionPolicy policy, bool settingOn = true, bool windowActive = false,
        DateTimeOffset? at = null, ApprovalDetectionSignal signal = ApprovalDetectionSignal.AllowPrefix) =>
        policy.ObserveAppeared(signal, settingOn, windowActive, at ?? T0);

    [Fact]
    public void NewCard_BeepsWhenOnAndWindowNotActive()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.Equal(ApprovalBeepOutcome.Beep, Appear(policy));
        Assert.True(policy.CardPresent);
        Assert.True(policy.IsAlerting);
    }

    [Fact]
    public void NewCard_NoBeepWhenSettingOff()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.Equal(ApprovalBeepOutcome.SuppressedSettingOff, Appear(policy, settingOn: false));
        Assert.True(policy.CardPresent);
        Assert.False(policy.IsAlerting);
    }

    [Fact]
    public void NewCard_NoBeepWhenWindowActive()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.Equal(ApprovalBeepOutcome.SuppressedWindowActive, Appear(policy, windowActive: true));
        Assert.False(policy.IsAlerting);
    }

    [Fact]
    public void Appearance_ReportsTheSignalThatFired()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.Equal(ApprovalDetectionSignal.Unknown, policy.CurrentSignal);
        Appear(policy, signal: ApprovalDetectionSignal.ButtonPair);
        Assert.Equal(ApprovalDetectionSignal.ButtonPair, policy.CurrentSignal);
        policy.ObserveGone();
        Assert.Equal(ApprovalDetectionSignal.Unknown, policy.CurrentSignal);
    }

    [Fact]
    public void SameCard_AppearanceIsJudgedOnlyOnce()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.Equal(ApprovalBeepOutcome.Beep, Appear(policy));
        // A duplicate appearance report is not a new judgment (the
        // card's REMINDERS come from ObserveStillPresent, below).
        Assert.Equal(ApprovalBeepOutcome.AlreadyPresent, Appear(policy, at: T0.AddMinutes(1)));
        Assert.Equal(ApprovalBeepOutcome.AlreadyPresent, Appear(policy, at: T0.AddMinutes(5)));
    }

    [Fact]
    public void CardSeenWhileActive_DoesNotBeepLaterWhenUserTabsAway()
    {
        // The appearance was judged while the user was looking at
        // the window; the same card still being up later is not a
        // new appearance and never enters the reminder cycle.
        var policy = new ApprovalAttentionPolicy();
        Assert.Equal(ApprovalBeepOutcome.SuppressedWindowActive, Appear(policy, windowActive: true));
        Assert.Equal(ApprovalBeepOutcome.AlreadyPresent, Appear(policy, at: T0.AddMinutes(1)));
        Assert.Equal(ApprovalBeepOutcome.NotAlerting,
            policy.ObserveStillPresent(true, false, T0.AddMinutes(2)));
    }

    [Fact]
    public void SettingOffAtAppearance_DoesNotBeepWhenToggledOnMidCard()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.Equal(ApprovalBeepOutcome.SuppressedSettingOff, Appear(policy, settingOn: false));
        Assert.Equal(ApprovalBeepOutcome.AlreadyPresent, Appear(policy, at: T0.AddSeconds(30)));
        Assert.Equal(ApprovalBeepOutcome.NotAlerting,
            policy.ObserveStillPresent(true, false, T0.AddMinutes(1)));
    }

    [Fact]
    public void Gone_RearmsSoTheNextCardBeeps()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.Equal(ApprovalBeepOutcome.Beep, Appear(policy));
        policy.ObserveGone();
        Assert.False(policy.CardPresent);
        Assert.Equal(ApprovalBeepOutcome.Beep,
            Appear(policy, at: T0 + ApprovalAttentionPolicy.Cooldown + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Cooldown_TwoCardsInQuickSuccessionBeepOnce()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.Equal(ApprovalBeepOutcome.Beep, Appear(policy));
        policy.ObserveGone();
        // A second card 2s later is inside the cooldown: judged,
        // suppressed, and it does NOT queue a late beep or reminders.
        Assert.Equal(ApprovalBeepOutcome.SuppressedCooldown, Appear(policy, at: T0.AddSeconds(2)));
        Assert.Equal(ApprovalBeepOutcome.NotAlerting,
            policy.ObserveStillPresent(true, false, T0.AddMinutes(1)));
        policy.ObserveGone();
        // A third card after the cooldown has elapsed beeps again.
        Assert.Equal(ApprovalBeepOutcome.Beep,
            Appear(policy, at: T0 + ApprovalAttentionPolicy.Cooldown + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void GoneWithoutACard_IsHarmless()
    {
        var policy = new ApprovalAttentionPolicy();
        policy.ObserveGone();
        Assert.False(policy.CardPresent);
        Assert.Equal(ApprovalBeepOutcome.Beep, Appear(policy));
    }

    // ── R7C2: the reminder cycle ──

    [Fact]
    public void WaitingCard_RemindsEveryTwentySecondsWhileUserIsAway()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.Equal(ApprovalBeepOutcome.Beep, Appear(policy));
        var interval = ApprovalAttentionPolicy.RepeatInterval;
        // Not yet due, then due — and the clock restarts per beep.
        Assert.Equal(ApprovalBeepOutcome.RepeatNotDue,
            policy.ObserveStillPresent(true, false, T0 + interval - TimeSpan.FromSeconds(1)));
        Assert.Equal(ApprovalBeepOutcome.Beep,
            policy.ObserveStillPresent(true, false, T0 + interval));
        Assert.Equal(ApprovalBeepOutcome.RepeatNotDue,
            policy.ObserveStillPresent(true, false, T0 + interval + interval - TimeSpan.FromSeconds(1)));
        Assert.Equal(ApprovalBeepOutcome.Beep,
            policy.ObserveStillPresent(true, false, T0 + interval + interval));
        Assert.True(policy.IsAlerting);
    }

    [Fact]
    public void ReminderCycle_EndsWhenTheWindowGainsFocus()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.Equal(ApprovalBeepOutcome.Beep, Appear(policy));
        // The user comes back: the cycle ends at that tick…
        Assert.Equal(ApprovalBeepOutcome.SuppressedWindowActive,
            policy.ObserveStillPresent(true, windowActive: true, T0.AddSeconds(10)));
        Assert.False(policy.IsAlerting);
        // …and tabbing away again does not resurrect it.
        Assert.Equal(ApprovalBeepOutcome.NotAlerting,
            policy.ObserveStillPresent(true, false, T0.AddMinutes(5)));
    }

    [Fact]
    public void ReminderCycle_EndsWhenTheSettingIsTurnedOff()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.Equal(ApprovalBeepOutcome.Beep, Appear(policy));
        Assert.Equal(ApprovalBeepOutcome.SuppressedSettingOff,
            policy.ObserveStillPresent(false, false, T0.AddSeconds(10)));
        Assert.False(policy.IsAlerting);
        Assert.Equal(ApprovalBeepOutcome.NotAlerting,
            policy.ObserveStillPresent(true, false, T0.AddMinutes(5)));
    }

    [Fact]
    public void Gone_EndsTheCycle_AndTheNextCardStartsFresh()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.Equal(ApprovalBeepOutcome.Beep, Appear(policy));
        policy.ObserveGone();
        Assert.Equal(ApprovalBeepOutcome.NoCard,
            policy.ObserveStillPresent(true, false, T0.AddSeconds(30)));
        var t1 = T0 + ApprovalAttentionPolicy.Cooldown + TimeSpan.FromSeconds(1);
        Assert.Equal(ApprovalBeepOutcome.Beep, Appear(policy, at: t1));
        Assert.Equal(ApprovalBeepOutcome.Beep,
            policy.ObserveStillPresent(true, false, t1 + ApprovalAttentionPolicy.RepeatInterval));
    }

    [Fact]
    public void TickWithNoCard_IsNoCard()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.Equal(ApprovalBeepOutcome.NoCard, policy.ObserveStillPresent(true, false, T0));
    }
}

// ───────────────────── R7C/R7C2: the bridge messages ─────────────────────

/// <summary>
/// The two R7C bridge messages parse to their own kinds (and
/// carry no text — the watcher reports presence, never content).
/// Since R7C2 the appearance also carries which watcher rule
/// matched, mapped through a fixed vocabulary: anything the page
/// invents becomes Unknown, never an echoed string.
/// </summary>
public class ApprovalBridgeParseTests
{
    [Fact]
    public void ApprovalTransitions_Parse()
    {
        var appeared = PageBridge.TryParse("{\"type\":\"md-approval-appeared\"}");
        Assert.NotNull(appeared);
        Assert.Equal(PageBridgeKind.ApprovalAppeared, appeared!.Kind);
        Assert.Equal("", appeared.Text);
        Assert.Equal(ApprovalDetectionSignal.Unknown, appeared.Signal);

        var gone = PageBridge.TryParse("{\"type\":\"md-approval-gone\"}");
        Assert.NotNull(gone);
        Assert.Equal(PageBridgeKind.ApprovalGone, gone!.Kind);
        Assert.Equal("", gone.Text);
    }

    [Fact]
    public void ApprovalAppearance_CarriesTheSignalName()
    {
        var allow = PageBridge.TryParse("{\"type\":\"md-approval-appeared\",\"signal\":\"allow-prefix\"}");
        Assert.Equal(ApprovalDetectionSignal.AllowPrefix, allow!.Signal);

        var pair = PageBridge.TryParse("{\"type\":\"md-approval-appeared\",\"signal\":\"pair\"}");
        Assert.Equal(ApprovalDetectionSignal.ButtonPair, pair!.Signal);
    }

    [Fact]
    public void ApprovalAppearance_InventedSignalBecomesUnknown()
    {
        var odd = PageBridge.TryParse("{\"type\":\"md-approval-appeared\",\"signal\":\"Allow the user\"}");
        Assert.Equal(ApprovalDetectionSignal.Unknown, odd!.Signal);
        var numeric = PageBridge.TryParse("{\"type\":\"md-approval-appeared\",\"signal\":42}");
        Assert.Equal(ApprovalDetectionSignal.Unknown, numeric!.Signal);
    }

    [Fact]
    public void ApprovalLookalikes_AreIgnored()
    {
        Assert.Null(PageBridge.TryParse("{\"type\":\"md-approval\"}"));
        Assert.Null(PageBridge.TryParse("{\"type\":\"md-approval-appeared \"}"));
        Assert.Null(PageBridge.TryParse("not json"));
        Assert.Null(PageBridge.TryParse(null));
    }
}

// ───────────────────── R7C: the setting ─────────────────────

/// <summary>
/// The approval-beep setting: on by default (SEAN asked for the
/// feature), survives the JSON clone the Settings window edits,
/// and a settings reset returns it to on.
/// </summary>
public class ApprovalBeepSettingsTests
{
    [Fact]
    public void Default_IsOn()
    {
        Assert.True(new AppSettings().ApprovalBeepEnabled);
    }

    [Fact]
    public void Clone_RoundTripsBothWays()
    {
        var off = new AppSettings { ApprovalBeepEnabled = false };
        Assert.False(off.Clone().ApprovalBeepEnabled);
        var on = new AppSettings { ApprovalBeepEnabled = true };
        Assert.True(on.Clone().ApprovalBeepEnabled);
    }

    [Fact]
    public void Reset_ReturnsTheBeepToOn()
    {
        var settings = new AppSettings { ApprovalBeepEnabled = false };
        SettingsReset.ApplyDefaults(settings);
        Assert.True(settings.ApprovalBeepEnabled);
    }

    [Fact]
    public void Sanitized_KeepsTheUsersChoice()
    {
        Assert.False(new AppSettings { ApprovalBeepEnabled = false }.Sanitized().ApprovalBeepEnabled);
        Assert.True(new AppSettings { ApprovalBeepEnabled = true }.Sanitized().ApprovalBeepEnabled);
    }
}

// ───────────────────── R7C/R7C2: the wiring (shell source guards) ─────────────────────

/// <summary>
/// End-to-end wiring pins, in the R7 guard style: the watcher
/// script speaks both transitions, the shell handles them and
/// consults the setting, and the Settings checkbox exists and is
/// loaded/harvested. If any link is cut, the feature silently
/// dies — these tests make it fail loudly instead. The R7C2
/// additions pin the detection rebuild itself: the primary
/// allow-prefix rule with its label cap, the pair rule kept as
/// back-up, the dropped viewport-share filter, the short settle
/// window, and the shell's reminder tick + outcome logging.
/// </summary>
public class ApprovalBeepWiringTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(typeof(ApprovalBeepWiringTests).Assembly.Location)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MuseDesktop.sln"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "MuseDesktop")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }

    private static string ShellFile(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "MuseDesktop", name));

    [Fact]
    public void Watcher_ReportsBothTransitions_AndNeverClicks()
    {
        var scripts = ShellFile("PageScripts.cs");
        Assert.Contains("md-approval-appeared", scripts);
        Assert.Contains("md-approval-gone", scripts);
        Assert.Contains("APPROVE_WORDS", scripts);
        Assert.Contains("DENY_WORDS", scripts);
        // The whole bridge's one interaction-shaped call is the
        // bookmark button's own listener; the watcher must not add
        // a programmatic click anywhere in the document script.
        Assert.DoesNotContain(".click()", scripts);
    }

    [Fact]
    public void Watcher_LeadsWithTheAllowPrefixRule()
    {
        var scripts = ShellFile("PageScripts.cs");
        // The field-proven primary rule (R7C2): a visible button
        // whose whole label starts with "allow" / "always allow",
        // with the 60-character label cap pinned here.
        Assert.Contains("allowPrefixHit", scripts);
        Assert.Contains("ALLOW_LABEL_MAX = 60", scripts);
        Assert.Contains("/^allow\\b/", scripts);
        Assert.Contains("/^always allow\\b/", scripts);
        Assert.Contains("return 'allow-prefix'", scripts);
        // The appearance report names the signal that fired.
        Assert.Contains("signal: signal", scripts);
    }

    [Fact]
    public void Watcher_KeepsThePairRuleAsTheBackupSignal()
    {
        var scripts = ShellFile("PageScripts.cs");
        Assert.Contains("detectApprovalSignal", scripts);
        Assert.Contains("return 'pair'", scripts);
    }

    [Fact]
    public void Watcher_NoLongerFiltersByViewportShare()
    {
        var scripts = ShellFile("PageScripts.cs");
        // R7C required the card container to be under 60% of the
        // viewport — one more way for a real card to be filtered
        // out. R7C2 removed it; nothing in the script may measure
        // the viewport again.
        Assert.DoesNotContain("innerWidth", scripts);
        Assert.DoesNotContain("innerHeight", scripts);
    }

    [Fact]
    public void Watcher_SettlesInAboutAThirdOfASecond()
    {
        var scripts = ShellFile("PageScripts.cs");
        Assert.Contains("APPROVAL_SETTLE_MS = 300", scripts);
    }

    [Fact]
    public void Shell_HandlesBothTransitions_AndConsultsTheSetting()
    {
        var main = ShellFile("MainWindow.xaml.cs");
        Assert.Contains("PageBridgeKind.ApprovalAppeared", main);
        Assert.Contains("PageBridgeKind.ApprovalGone", main);
        Assert.Contains("_settings.ApprovalBeepEnabled", main);
        Assert.Contains("SystemSounds.Exclamation", main);
    }

    [Fact]
    public void Shell_PassesTheSignal_AsksForReminders_AndLogsOutcomes()
    {
        var main = ShellFile("MainWindow.xaml.cs");
        // The watcher's signal reaches the policy…
        Assert.Contains("OnApprovalCardAppeared(message.Signal)", main);
        // …the reminder tick asks Core whether a repeat is due…
        Assert.Contains("ObserveStillPresent", main);
        // …and beeps/suppressions leave log lines (presence and
        // reasons only — the R7C2 diagnosability requirement).
        Assert.Contains("Approval card detected (signal:", main);
        Assert.Contains("Approval card gone.", main);
        Assert.Contains("Approval reminders stopped", main);
    }

    [Fact]
    public void Settings_CheckboxExistsAndIsLoadedAndHarvested()
    {
        var xaml = ShellFile("SettingsWindow.xaml");
        Assert.Contains("ApprovalBeepCheck", xaml);
        Assert.Contains("approval.beep_label", xaml);
        var code = ShellFile("SettingsWindow.xaml.cs");
        Assert.Contains("ApprovalBeepCheck.IsChecked = _working.ApprovalBeepEnabled", code);
        Assert.Contains("_working.ApprovalBeepEnabled = ApprovalBeepCheck.IsChecked == true", code);
        Assert.Contains("target.ApprovalBeepEnabled = source.ApprovalBeepEnabled", code);
    }
}
