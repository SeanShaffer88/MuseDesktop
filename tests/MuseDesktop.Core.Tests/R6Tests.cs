using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>
/// R6 (v1.7.0, items 151–160): appearance round 2. The invariants
/// that matter: accessibility outranks taste everywhere (High
/// Contrast and reduced transparency always win over backdrops,
/// tints, and see-through windows), a custom accent passes the SAME
/// measured-contrast guard as the presets or it silently falls back,
/// the theme schedule reuses quiet-hours window math (wraps, equal
/// times = never light), the tray composition generalizes the old
/// plan without changing its default face, and every new field
/// round-trips through settings export/import (theme bundles).
/// </summary>
public class R6Tests
{
    private static readonly BackdropCapabilities Capable = new(true, true);

    // ─────────────────────── backdrop materials (#151) ───────────────────────

    [Fact]
    public void Backdrop_AutoResolvesToMicaOnACapablePc()
    {
        Assert.Equal(BackdropMaterial.Mica,
            BackdropPolicy.Resolve(BackdropPreference.Auto, Capable, highContrast: false));
    }

    [Fact]
    public void Backdrop_ExplicitChoicesAreHonored()
    {
        Assert.Equal(BackdropMaterial.Acrylic,
            BackdropPolicy.Resolve(BackdropPreference.Acrylic, Capable, highContrast: false));
        Assert.Equal(BackdropMaterial.Mica,
            BackdropPolicy.Resolve(BackdropPreference.Mica, Capable, highContrast: false));
        Assert.Equal(BackdropMaterial.None,
            BackdropPolicy.Resolve(BackdropPreference.None, Capable, highContrast: false));
    }

    [Fact]
    public void Backdrop_HighContrastAlwaysWins()
    {
        Assert.Equal(BackdropMaterial.None,
            BackdropPolicy.Resolve(BackdropPreference.Acrylic, Capable, highContrast: true));
        Assert.Equal(BackdropMaterial.None,
            BackdropPolicy.Resolve(BackdropPreference.Auto, Capable, highContrast: true));
    }

    [Fact]
    public void Backdrop_ReducedTransparencyAlwaysWins()
    {
        var reduced = new BackdropCapabilities(true, false);
        Assert.Equal(BackdropMaterial.None,
            BackdropPolicy.Resolve(BackdropPreference.Mica, reduced, highContrast: false));
    }

    [Fact]
    public void Backdrop_UnsupportedPcFallsBackGracefully()
    {
        var old = new BackdropCapabilities(false, true);
        Assert.Equal(BackdropMaterial.None,
            BackdropPolicy.Resolve(BackdropPreference.Auto, old, highContrast: false));
        Assert.Equal(BackdropMaterial.None,
            BackdropPolicy.Resolve(BackdropPreference.Acrylic, old, highContrast: false));
    }

    [Fact]
    public void Backdrop_UndefinedPreferenceMeansNone()
    {
        Assert.Equal(BackdropMaterial.None,
            BackdropPolicy.Resolve((BackdropPreference)99, Capable, highContrast: false));
    }

    // ─────────────────────── title bar tint (#152) ───────────────────────

    [Fact]
    public void TitleBarTint_UsesTheProfileColor()
    {
        Assert.Equal(ProfileColors.HexFor("Blue"),
            TitleBarTint.Resolve(enabled: true, "Blue", highContrast: false));
    }

    [Fact]
    public void TitleBarTint_OffHighContrastOrColorlessMeansDefault()
    {
        Assert.Null(TitleBarTint.Resolve(enabled: false, "Blue", highContrast: false));
        Assert.Null(TitleBarTint.Resolve(enabled: true, "Blue", highContrast: true));
        Assert.Null(TitleBarTint.Resolve(enabled: true, null, highContrast: false));
        Assert.Null(TitleBarTint.Resolve(enabled: true, "NotAColor", highContrast: false));
    }

    // ─────────────────────── density (#153) ───────────────────────

    [Fact]
    public void Density_CompactIsDenserThanComfortableEverywhere()
    {
        var compact = DensityMetrics.For(ShellDensity.Compact);
        var comfortable = DensityMetrics.For(ShellDensity.Comfortable);
        Assert.True(compact.ToolbarHeight < comfortable.ToolbarHeight);
        Assert.True(compact.StatusBarHeight < comfortable.StatusBarHeight);
        Assert.True(compact.ToolbarInset < comfortable.ToolbarInset);
        Assert.True(compact.StatusInset < comfortable.StatusInset);
    }

    [Fact]
    public void Density_ComfortableKeepsTheLongStandingMeasurements()
    {
        var m = DensityMetrics.For(ShellDensity.Comfortable);
        Assert.Equal(40, m.ToolbarHeight);
        Assert.Equal(24, m.StatusBarHeight);
        Assert.Equal(6, m.ToolbarInset);
        Assert.Equal(8, m.StatusInset);
    }

    // ─────────────────────── custom accent (#158) ───────────────────────

    [Theory]
    [InlineData("#e50914", "#E50914")]
    [InlineData("e50914", "#E50914")]
    [InlineData("  #0aBcDe  ", "#0ABCDE")]
    public void CustomAccent_Normalizes(string input, string expected) =>
        Assert.Equal(expected, CustomAccent.TryNormalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#GGGGGG")]
    public void CustomAccent_RejectsNonColors(string? input) =>
        Assert.Null(CustomAccent.TryNormalize(input));

    [Theory]
    // The awkward cases: extremes, near-background colors, and a
    // saturated mid-tone — the repair loop must make ALL of them
    // meet the presets' 4.5:1 contract on both shells.
    [InlineData("#FFFFFF")]
    [InlineData("#000000")]
    [InlineData("#808080")]
    [InlineData("#FF0000")]
    [InlineData("#FFFF00")]
    [InlineData("#0B0B0D")] // the dark shell's own background
    [InlineData("#F5F5F6")] // the light shell's own background
    [InlineData("#E50914")]
    public void CustomAccent_VariantsPassThePresetContrastContract(string picked)
    {
        var (dark, light) = CustomAccent.Variants(picked);
        foreach (var surface in new[] { ShellPalettes.Dark.Bg, ShellPalettes.Dark.Surface, ShellPalettes.Dark.Raised })
            Assert.True(ColorContrast.Ratio(dark, surface) >= 4.5,
                $"{dark} on {surface} = {ColorContrast.Ratio(dark, surface):0.00}");
        foreach (var surface in new[] { ShellPalettes.Light.Bg, ShellPalettes.Light.Surface, ShellPalettes.Light.Raised })
            Assert.True(ColorContrast.Ratio(light, surface) >= 4.5,
                $"{light} on {surface} = {ColorContrast.Ratio(light, surface):0.00}");
        Assert.True(ColorContrast.Ratio("#FFFFFF", light) >= 4.5,
            $"white on {light} = {ColorContrast.Ratio("#FFFFFF", light):0.00}");
    }

    [Fact]
    public void CustomAccent_VariantsAreDeterministic()
    {
        Assert.Equal(CustomAccent.Variants("#3A7BD5"), CustomAccent.Variants("#3A7BD5"));
    }

    [Fact]
    public void AccentResolver_CustomWinsAndInvalidFallsBackToPreset()
    {
        var custom = AccentResolver.Resolve("Blue", "#E50914");
        Assert.Equal(CustomAccent.Variants("#E50914"), custom);
        Assert.Equal(AccentPalette.For("Blue"), AccentResolver.Resolve("Blue", "not-a-color"));
        Assert.Equal(AccentPalette.For("Blue"), AccentResolver.Resolve("Blue", null));
    }

    // ─────────────────────── theme scheduling (#159) ───────────────────────

    [Fact]
    public void ThemeSchedule_LightInsideTheWindowDarkOutside()
    {
        // 07:00 → 19:00 light.
        Assert.False(ThemeScheduleMath.IsLightAt(420, 1140, 419));
        Assert.True(ThemeScheduleMath.IsLightAt(420, 1140, 420));
        Assert.True(ThemeScheduleMath.IsLightAt(420, 1140, 1139));
        Assert.False(ThemeScheduleMath.IsLightAt(420, 1140, 1140));
        Assert.False(ThemeScheduleMath.IsLightAt(420, 1140, 0));
    }

    [Fact]
    public void ThemeSchedule_NightOwlWindowWrapsMidnight()
    {
        // Light 20:00 → 06:00 (a dark-room schedule): light at 23:00
        // and 05:00, dark at noon.
        Assert.True(ThemeScheduleMath.IsLightAt(1200, 360, 1380));
        Assert.True(ThemeScheduleMath.IsLightAt(1200, 360, 300));
        Assert.False(ThemeScheduleMath.IsLightAt(1200, 360, 720));
    }

    [Fact]
    public void ThemeSchedule_EqualTimesMeanNeverLight()
    {
        Assert.False(ThemeScheduleMath.IsLightAt(600, 600, 600));
        Assert.False(ThemeScheduleMath.IsLightAt(600, 600, 0));
    }

    [Fact]
    public void ThemeResolution_FollowScheduleUsesTheScheduleInputOnly()
    {
        Assert.True(ThemeResolution.IsLight(ThemeMode.FollowSchedule, systemIsLight: false, scheduleIsLight: true));
        Assert.False(ThemeResolution.IsLight(ThemeMode.FollowSchedule, systemIsLight: true, scheduleIsLight: false));
        // The older modes ignore the schedule entirely.
        Assert.True(ThemeResolution.IsLight(ThemeMode.FollowSystem, systemIsLight: true, scheduleIsLight: false));
        Assert.False(ThemeResolution.IsLight(ThemeMode.FollowSystem, systemIsLight: false, scheduleIsLight: true));
        Assert.True(ThemeResolution.IsLight(ThemeMode.Light, systemIsLight: false, scheduleIsLight: false));
        Assert.False(ThemeResolution.IsLight(ThemeMode.Dark, systemIsLight: true, scheduleIsLight: true));
        // The 2-arg overload never guesses light for a schedule.
        Assert.False(ThemeResolution.IsLight(ThemeMode.FollowSchedule, systemIsLight: true));
    }

    [Theory]
    [InlineData("7:00", 420)]
    [InlineData("07:00", 420)]
    [InlineData("19:30", 1170)]
    [InlineData("0:00", 0)]
    [InlineData("23:59", 1439)]
    [InlineData("7:00 PM", 1140)]
    [InlineData("12:00 AM", 0)]
    [InlineData("12:00 PM", 720)]
    public void ThemeSchedule_ParsesTimes(string text, int expected)
    {
        Assert.True(ThemeScheduleMath.TryParseTime(text, out var minutes));
        Assert.Equal(expected, minutes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("24:00")]
    [InlineData("7:60")]
    [InlineData("7")]
    [InlineData("7:5x")]
    [InlineData("13:00 PM")]
    public void ThemeSchedule_RejectsNonTimes(string? text) =>
        Assert.False(ThemeScheduleMath.TryParseTime(text, out _));

    [Fact]
    public void ThemeSchedule_FormatsAndRoundTrips()
    {
        Assert.Equal("07:00", ThemeScheduleMath.FormatMinutes(420));
        Assert.Equal("19:30", ThemeScheduleMath.FormatMinutes(1170));
        Assert.True(ThemeScheduleMath.TryParseTime(ThemeScheduleMath.FormatMinutes(1170), out var back));
        Assert.Equal(1170, back);
    }

    [Fact]
    public void ThemeSchedule_IsLightNowReadsSettings()
    {
        var settings = new AppSettings { ThemeLightStartMinutes = 420, ThemeDarkStartMinutes = 1140 };
        var morning = new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.FromHours(-5));
        var night = new DateTimeOffset(2026, 10, 26, 23, 0, 0, TimeSpan.FromHours(-5));
        Assert.True(ThemeScheduleMath.IsLightNow(settings, morning));
        Assert.False(ThemeScheduleMath.IsLightNow(settings, night));
    }

    // ─────────────────────── window opacity (#156) ───────────────────────

    [Fact]
    public void Opacity_ClampsIntoSixtyToOneHundred()
    {
        Assert.Equal(1.0, WindowOpacityPolicy.Clamp(1.4));
        Assert.Equal(0.60, WindowOpacityPolicy.Clamp(0.2));
        Assert.Equal(0.85, WindowOpacityPolicy.Clamp(0.85));
        Assert.Equal(1.0, WindowOpacityPolicy.Clamp(double.NaN));
        Assert.Equal(1.0, WindowOpacityPolicy.Clamp(double.PositiveInfinity));
    }

    [Fact]
    public void Opacity_HighContrastAndLockForceFullyOpaque()
    {
        Assert.Equal(1.0, WindowOpacityPolicy.Effective(0.7, highContrast: true, locked: false));
        Assert.Equal(1.0, WindowOpacityPolicy.Effective(0.7, highContrast: false, locked: true));
        Assert.Equal(0.7, WindowOpacityPolicy.Effective(0.7, highContrast: false, locked: false));
    }

    // ─────────────────────── tray icon styles (#155) ───────────────────────

    private static readonly AttentionSignal ThreeUnread = new(AttentionKind.Count, 3);
    private static readonly AttentionSignal DotOnly = new(AttentionKind.Dot, 0);

    [Fact]
    public void TrayCompose_DefaultFaceMatchesTheLegacyPlan()
    {
        var plan = TrayIconPlanner.Compose(
            TrayIconStyle.Color, TrayOverlayStyle.NumberBadge, "#E50914", ThreeUnread);
        var legacy = TrayIconPlanner.Plan(true, ThreeUnread);
        Assert.Equal(TrayBaseTransform.None, plan.Base);
        Assert.Equal("#E50914", plan.ProfileDotHex);
        Assert.Equal(legacy.ShowBadge, plan.ShowBadge);
        Assert.Equal(legacy.BadgeText, plan.BadgeText);
    }

    [Fact]
    public void TrayCompose_MonochromeGrayscalesIconAndDot()
    {
        var plan = TrayIconPlanner.Compose(
            TrayIconStyle.Monochrome, TrayOverlayStyle.NumberBadge, "#E50914", ThreeUnread);
        Assert.Equal(TrayBaseTransform.Grayscale, plan.Base);
        // #E50914 → 0.299·229 + 0.587·9 + 0.114·20 = 76 → #4C4C4C.
        Assert.Equal("#4C4C4C", plan.ProfileDotHex);
        Assert.True(plan.ShowBadge);
        Assert.Equal("3", plan.BadgeText);
    }

    [Fact]
    public void TrayCompose_ProfileColoredTintsTheIconAndDropsTheDot()
    {
        var plan = TrayIconPlanner.Compose(
            TrayIconStyle.ProfileColored, TrayOverlayStyle.NumberBadge, "#E50914", ThreeUnread);
        Assert.Equal(TrayBaseTransform.ProfileTint, plan.Base);
        Assert.Null(plan.ProfileDotHex);
        // No profile color → no tint, and nothing to dot with either.
        var plain = TrayIconPlanner.Compose(
            TrayIconStyle.ProfileColored, TrayOverlayStyle.NumberBadge, null, ThreeUnread);
        Assert.Equal(TrayBaseTransform.None, plain.Base);
        Assert.Null(plain.ProfileDotHex);
    }

    [Fact]
    public void TrayCompose_OverlayChoices()
    {
        var none = TrayIconPlanner.Compose(TrayIconStyle.Color, TrayOverlayStyle.None, "#E50914", ThreeUnread);
        Assert.False(none.ShowBadge);
        Assert.Null(none.BadgeText);

        var dot = TrayIconPlanner.Compose(TrayIconStyle.Color, TrayOverlayStyle.Dot, "#E50914", ThreeUnread);
        Assert.True(dot.ShowBadge);
        Assert.Null(dot.BadgeText);

        var quietDot = TrayIconPlanner.Compose(TrayIconStyle.Color, TrayOverlayStyle.Dot, "#E50914", AttentionSignal.None);
        Assert.False(quietDot.ShowBadge);

        var dotSignal = TrayIconPlanner.Compose(TrayIconStyle.Color, TrayOverlayStyle.NumberBadge, "#E50914", DotOnly);
        Assert.True(dotSignal.ShowBadge);
        Assert.Null(dotSignal.BadgeText);
    }

    [Fact]
    public void TrayCompose_PlainWhenNothingToShow()
    {
        var plan = TrayIconPlanner.Compose(TrayIconStyle.Color, TrayOverlayStyle.NumberBadge, null, AttentionSignal.None);
        Assert.True(plan.IsPlainIcon);
    }

    // ─────────────────────── web view background (#157) ───────────────────────

    [Fact]
    public void WebViewBackground_IsTheThemeBackgroundUnlessHighContrast()
    {
        Assert.Equal(ShellPalettes.Dark.Bg, WebViewBackgroundPolicy.ResolveHex(ShellPalettes.Dark, highContrast: false));
        Assert.Equal(ShellPalettes.Light.Bg, WebViewBackgroundPolicy.ResolveHex(ShellPalettes.Light, highContrast: false));
        Assert.Null(WebViewBackgroundPolicy.ResolveHex(ShellPalettes.Dark, highContrast: true));
    }

    // ─────────────────────── mini mode downloads (#154) ───────────────────────

    private static ActiveDownload Dl(DownloadActivityState state) => new() { State = state };

    [Fact]
    public void MiniSummary_CountsOnlyLiveStates()
    {
        var summary = MiniDownloadSummary.FromDownloads(new[]
        {
            Dl(DownloadActivityState.InProgress), Dl(DownloadActivityState.InProgress),
            Dl(DownloadActivityState.Paused),
            Dl(DownloadActivityState.Completed), Dl(DownloadActivityState.Failed),
        });
        Assert.Equal(2, summary.Transferring);
        Assert.Equal(1, summary.Paused);
        Assert.Equal(3, summary.Active);
        Assert.True(summary.CanPause);
        Assert.True(summary.CanResume);
        Assert.Equal("2 downloading · 1 paused", summary.Label);
    }

    [Fact]
    public void MiniSummary_EmptyAndSingularLabels()
    {
        var empty = MiniDownloadSummary.FromDownloads(Array.Empty<ActiveDownload>());
        Assert.Equal("No active downloads", empty.Label);
        Assert.False(empty.CanPause);
        Assert.False(empty.CanResume);
        var one = MiniDownloadSummary.FromDownloads(new[] { Dl(DownloadActivityState.Paused) });
        Assert.Equal("1 paused", one.Label);
        Assert.False(one.CanPause);
        Assert.True(one.CanResume);
    }

    // ─────────────────────── settings plumbing & bundles (#160) ───────────────────────

    [Fact]
    public void Settings_R6FieldsRoundTripThroughExportImport()
    {
        var original = new AppSettings
        {
            Theme = ThemeMode.FollowSchedule,
            AccentColor = "Teal",
            CustomAccent = "#3A7BD5",
            Backdrop = BackdropPreference.Acrylic,
            TitleBarProfileTint = false,
            Density = ShellDensity.Compact,
            TrayIconStyle = TrayIconStyle.Monochrome,
            TrayCountOverlay = TrayOverlayStyle.Dot,
            ThemeLightStartMinutes = 390,
            ThemeDarkStartMinutes = 1230,
        };
        var json = SettingsStore.Export(original);
        Assert.True(SettingsStore.TryImport(json, out var imported, out var error), error);
        Assert.NotNull(imported);
        Assert.Equal(ThemeMode.FollowSchedule, imported!.Theme);
        Assert.Equal("Teal", imported.AccentColor);
        Assert.Equal("#3A7BD5", imported.CustomAccent);
        Assert.Equal(BackdropPreference.Acrylic, imported.Backdrop);
        Assert.False(imported.TitleBarProfileTint);
        Assert.Equal(ShellDensity.Compact, imported.Density);
        Assert.Equal(TrayIconStyle.Monochrome, imported.TrayIconStyle);
        Assert.Equal(TrayOverlayStyle.Dot, imported.TrayCountOverlay);
        Assert.Equal(390, imported.ThemeLightStartMinutes);
        Assert.Equal(1230, imported.ThemeDarkStartMinutes);
    }

    [Fact]
    public void Settings_SanitizedRepairsR6Fields()
    {
        var broken = new AppSettings
        {
            Backdrop = (BackdropPreference)77,
            Density = (ShellDensity)77,
            TrayIconStyle = (TrayIconStyle)77,
            TrayCountOverlay = (TrayOverlayStyle)77,
            CustomAccent = "banana",
            ThemeLightStartMinutes = -5,
            ThemeDarkStartMinutes = 9999,
        }.Sanitized();
        Assert.Equal(BackdropPreference.Auto, broken.Backdrop);
        Assert.Equal(ShellDensity.Comfortable, broken.Density);
        Assert.Equal(TrayIconStyle.Color, broken.TrayIconStyle);
        Assert.Equal(TrayOverlayStyle.NumberBadge, broken.TrayCountOverlay);
        Assert.Null(broken.CustomAccent);
        Assert.Equal(ThemeScheduleMath.DefaultLightStartMinutes, broken.ThemeLightStartMinutes);
        Assert.Equal(ThemeScheduleMath.DefaultDarkStartMinutes, broken.ThemeDarkStartMinutes);
        // Per-field repair: one good time survives a bad sibling.
        var half = new AppSettings { ThemeLightStartMinutes = 300, ThemeDarkStartMinutes = -1 }.Sanitized();
        Assert.Equal(300, half.ThemeLightStartMinutes);
        Assert.Equal(ThemeScheduleMath.DefaultDarkStartMinutes, half.ThemeDarkStartMinutes);
        // A well-formed custom accent is normalized, not dropped.
        var ok = new AppSettings { CustomAccent = "e50914" }.Sanitized();
        Assert.Equal("#E50914", ok.CustomAccent);
    }

    [Fact]
    public void SettingsReset_RestoresR6Defaults()
    {
        var settings = new AppSettings
        {
            Backdrop = BackdropPreference.Acrylic,
            TitleBarProfileTint = false,
            Density = ShellDensity.Compact,
            TrayIconStyle = TrayIconStyle.ProfileColored,
            TrayCountOverlay = TrayOverlayStyle.None,
            CustomAccent = "#123456",
            ThemeLightStartMinutes = 100,
            ThemeDarkStartMinutes = 200,
        };
        SettingsReset.ApplyDefaults(settings);
        var fresh = new AppSettings();
        Assert.Equal(fresh.Backdrop, settings.Backdrop);
        Assert.Equal(fresh.TitleBarProfileTint, settings.TitleBarProfileTint);
        Assert.Equal(fresh.Density, settings.Density);
        Assert.Equal(fresh.TrayIconStyle, settings.TrayIconStyle);
        Assert.Equal(fresh.TrayCountOverlay, settings.TrayCountOverlay);
        Assert.Equal(fresh.CustomAccent, settings.CustomAccent);
        Assert.Equal(fresh.ThemeLightStartMinutes, settings.ThemeLightStartMinutes);
        Assert.Equal(fresh.ThemeDarkStartMinutes, settings.ThemeDarkStartMinutes);
    }

    [Fact]
    public void ThemeBundle_CaptureApplyRoundTripsTheFace()
    {
        var source = new AppSettings
        {
            Theme = ThemeMode.Light,
            AccentColor = "Purple",
            CustomAccent = "#0ABCDE",
            Backdrop = BackdropPreference.Mica,
            ThemeLightStartMinutes = 390,
            ThemeDarkStartMinutes = 1230,
        };
        var bundle = ThemeBundle.Capture(source);
        var target = new AppSettings();
        bundle.ApplyTo(target);
        Assert.Equal(ThemeMode.Light, target.Theme);
        Assert.Equal("Purple", target.AccentColor);
        Assert.Equal("#0ABCDE", target.CustomAccent);
        Assert.Equal(BackdropPreference.Mica, target.Backdrop);
        // The bundle carries the face, not the clock: schedule times
        // are their own settings (and ride the same export).
        Assert.Equal(ThemeScheduleMath.DefaultLightStartMinutes, target.ThemeLightStartMinutes);

        // A hand-made bundle is normalized on the way in.
        var scruffy = new ThemeBundle((ThemeMode)42, "NotAKey", "xyz", (BackdropPreference)42);
        scruffy.ApplyTo(target);
        Assert.Equal(ThemeMode.Dark, target.Theme);
        Assert.Equal(AccentPalette.DefaultKey, target.AccentColor);
        Assert.Null(target.CustomAccent);
        Assert.Equal(BackdropPreference.Auto, target.Backdrop);
    }

    [Fact]
    public void Help_HasTheAppearanceRound2Page()
    {
        var page = HelpCatalog.ById("appearance-round-2");
        Assert.NotNull(page);
        Assert.Equal(SettingsSearch.AppearanceId, page!.SettingsSectionId);
    }
}
