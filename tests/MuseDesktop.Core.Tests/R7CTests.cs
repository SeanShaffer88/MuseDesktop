using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

// ───────────────────── R7C: the approval beep's policy ─────────────────────

/// <summary>
/// The approval-beep policy (R7C): an appearance is judged exactly
/// once, at its first report — the setting must be on, the window
/// must not be active, and the global cooldown must have elapsed.
/// A disappearance re-arms; nothing else does.
/// </summary>
public class ApprovalAttentionPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewCard_BeepsWhenOnAndWindowNotActive()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.True(policy.ObserveAppeared(settingOn: true, windowActive: false, T0));
        Assert.True(policy.CardPresent);
    }

    [Fact]
    public void NewCard_NoBeepWhenSettingOff()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.False(policy.ObserveAppeared(settingOn: false, windowActive: false, T0));
    }

    [Fact]
    public void NewCard_NoBeepWhenWindowActive()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.False(policy.ObserveAppeared(settingOn: true, windowActive: true, T0));
    }

    [Fact]
    public void SameCard_NeverBeepsTwice()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.True(policy.ObserveAppeared(true, false, T0));
        Assert.False(policy.ObserveAppeared(true, false, T0.AddMinutes(1)));
        Assert.False(policy.ObserveAppeared(true, false, T0.AddMinutes(5)));
    }

    [Fact]
    public void CardSeenWhileActive_DoesNotBeepLaterWhenUserTabsAway()
    {
        // The appearance was judged while the user was looking at
        // the window; the same card still being up later is not a
        // new appearance.
        var policy = new ApprovalAttentionPolicy();
        Assert.False(policy.ObserveAppeared(true, windowActive: true, T0));
        Assert.False(policy.ObserveAppeared(true, windowActive: false, T0.AddMinutes(1)));
    }

    [Fact]
    public void SettingOffAtAppearance_DoesNotBeepWhenToggledOnMidCard()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.False(policy.ObserveAppeared(settingOn: false, windowActive: false, T0));
        Assert.False(policy.ObserveAppeared(settingOn: true, windowActive: false, T0.AddSeconds(30)));
    }

    [Fact]
    public void Gone_RearmsSoTheNextCardBeeps()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.True(policy.ObserveAppeared(true, false, T0));
        policy.ObserveGone();
        Assert.False(policy.CardPresent);
        Assert.True(policy.ObserveAppeared(true, false,
            T0 + ApprovalAttentionPolicy.Cooldown + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Cooldown_TwoCardsInQuickSuccessionBeepOnce()
    {
        var policy = new ApprovalAttentionPolicy();
        Assert.True(policy.ObserveAppeared(true, false, T0));
        policy.ObserveGone();
        // A second card 2s later is inside the cooldown: judged,
        // suppressed, and it does NOT queue a late beep.
        Assert.False(policy.ObserveAppeared(true, false, T0.AddSeconds(2)));
        policy.ObserveGone();
        // A third card after the cooldown has elapsed beeps again.
        Assert.True(policy.ObserveAppeared(true, false,
            T0 + ApprovalAttentionPolicy.Cooldown + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void GoneWithoutACard_IsHarmless()
    {
        var policy = new ApprovalAttentionPolicy();
        policy.ObserveGone();
        Assert.False(policy.CardPresent);
        Assert.True(policy.ObserveAppeared(true, false, T0));
    }
}

// ───────────────────── R7C: the bridge messages ─────────────────────

/// <summary>
/// The two R7C bridge messages parse to their own kinds (and
/// carry no text — the watcher reports presence, never content).
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

        var gone = PageBridge.TryParse("{\"type\":\"md-approval-gone\"}");
        Assert.NotNull(gone);
        Assert.Equal(PageBridgeKind.ApprovalGone, gone!.Kind);
        Assert.Equal("", gone.Text);
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

// ───────────────────── R7C: the wiring (shell source guards) ─────────────────────

/// <summary>
/// End-to-end wiring pins, in the R7 guard style: the watcher
/// script speaks both transitions, the shell handles them and
/// consults the setting, and the Settings checkbox exists and is
/// loaded/harvested. If any link is cut, the feature silently
/// dies — these tests make it fail loudly instead.
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
    public void Shell_HandlesBothTransitions_AndConsultsTheSetting()
    {
        var main = ShellFile("MainWindow.xaml.cs");
        Assert.Contains("PageBridgeKind.ApprovalAppeared", main);
        Assert.Contains("PageBridgeKind.ApprovalGone", main);
        Assert.Contains("_settings.ApprovalBeepEnabled", main);
        Assert.Contains("SystemSounds.Exclamation", main);
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
