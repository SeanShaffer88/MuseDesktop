using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>v0.9.0 Slice 5 (items 41–50) Core additions: Settings search
/// (#41), reset-with-preserved-identity (#42), shell palettes + accent
/// contrast (#43/#44), the toolbar layout model (#45), shell text scale
/// (#46), the per-profile start page (#47), Settings-window memory
/// sanitize (#48), portable location resolution (#49), and the settings
/// plumbing for all of it (sanitize/clone/export-import).</summary>
public class S5Tests
{
    // ── #43 shell palettes + theme resolution ──

    [Fact]
    public void ThemeResolution_Modes()
    {
        Assert.False(ThemeResolution.IsLight(ThemeMode.Dark, systemIsLight: true));
        Assert.False(ThemeResolution.IsLight(ThemeMode.Dark, systemIsLight: false));
        Assert.True(ThemeResolution.IsLight(ThemeMode.Light, systemIsLight: false));
        Assert.True(ThemeResolution.IsLight(ThemeMode.FollowSystem, systemIsLight: true));
        Assert.False(ThemeResolution.IsLight(ThemeMode.FollowSystem, systemIsLight: false));
    }

    [Fact]
    public void ShellPalettes_TextAndMutedMeetContrastOnEverySurface()
    {
        // Text carries real content and Muted carries notes/status:
        // both must reach WCAG AA (4.5:1) on every surface they sit on.
        // Faint is de-emphasis only and must still clear 3:1.
        foreach (var palette in new[] { ShellPalettes.Dark, ShellPalettes.Light })
        {
            foreach (var surface in new[] { palette.Bg, palette.Surface, palette.Raised })
            {
                Assert.True(ColorContrast.Ratio(palette.Text, surface) >= 4.5,
                    $"Text {palette.Text} on {surface}: {ColorContrast.Ratio(palette.Text, surface):0.00}");
                Assert.True(ColorContrast.Ratio(palette.Muted, surface) >= 4.5,
                    $"Muted {palette.Muted} on {surface}: {ColorContrast.Ratio(palette.Muted, surface):0.00}");
                Assert.True(ColorContrast.Ratio(palette.Faint, surface) >= 3.0,
                    $"Faint {palette.Faint} on {surface}: {ColorContrast.Ratio(palette.Faint, surface):0.00}");
            }
        }
    }

    [Fact]
    public void ColorContrast_KnownValues()
    {
        Assert.Equal(21.0, ColorContrast.Ratio("#000000", "#FFFFFF"), precision: 1);
        Assert.Equal(1.0, ColorContrast.Ratio("#123456", "#123456"), precision: 2);
    }

    // ── #44 accent palette ──

    [Fact]
    public void AccentPalette_EveryAccentPassesItsContrastContract()
    {
        // The contract from the AccentPalette doc: the dark-shell
        // variant is readable text on the dark surfaces; the light-shell
        // variant is readable text on the light surfaces AND carries
        // white button text at AA.
        foreach (var (key, darkHex, lightHex) in AccentPalette.Palette)
        {
            Assert.True(ColorContrast.Ratio(darkHex, ShellPalettes.Dark.Bg) >= 4.5, $"{key} dark on dark Bg");
            Assert.True(ColorContrast.Ratio(darkHex, ShellPalettes.Dark.Surface) >= 4.5, $"{key} dark on dark Surface");
            Assert.True(ColorContrast.Ratio(lightHex, ShellPalettes.Light.Bg) >= 4.5, $"{key} light on light Bg");
            Assert.True(ColorContrast.Ratio(lightHex, ShellPalettes.Light.Surface) >= 4.5, $"{key} light on light Surface");
            Assert.True(ColorContrast.Ratio("#FFFFFF", lightHex) >= 4.5, $"{key} white text on fill");
        }
    }

    [Fact]
    public void AccentPalette_SanitizeAndDefault()
    {
        Assert.Equal("Blue", AccentPalette.Sanitize("blue"));
        Assert.Equal("Teal", AccentPalette.Sanitize(" Teal "));
        Assert.Equal("Red", AccentPalette.Sanitize("chartreuse"));
        Assert.Equal("Red", AccentPalette.Sanitize(null));
        Assert.Equal("Red", AccentPalette.Sanitize(""));
        var (dark, light) = AccentPalette.For("nope");
        Assert.Equal(AccentPalette.Palette[0].DarkShellHex, dark);
        Assert.Equal(AccentPalette.Palette[0].LightShellHex, light);
        // The default accent IS the family red family: the light-shell
        // variant is a red, and the profile palette is untouched.
        Assert.Equal("Red", AccentPalette.DefaultKey);
        Assert.NotNull(ProfileColors.HexFor("Red"));
    }

    // ── #45 toolbar layout ──

    [Fact]
    public void ToolbarLayout_DefaultsShowEverythingInCanonicalOrder()
    {
        var (order, hidden) = ToolbarLayout.Sanitize(null, null);
        Assert.Equal(ToolbarLayout.DefaultOrder(), order);
        Assert.Empty(hidden);
        Assert.Equal(new[] { "back", "forward", "reload", "home" },
            ToolbarLayout.VisibleIds(order, hidden, ToolbarGroup.Left));
        Assert.Equal(new[] { "zoomout", "zoomlabel", "zoomin", "pin", "browser" },
            ToolbarLayout.VisibleIds(order, hidden, ToolbarGroup.Right));
    }

    [Fact]
    public void ToolbarLayout_SanitizeDropsUnknownAndDuplicates()
    {
        var (order, hidden) = ToolbarLayout.Sanitize(
            new[] { "home", "bogus", "back", "home", "reload" },
            new[] { "pin", "bogus", "pin" });
        Assert.Equal(ToolbarLayout.All.Count, order.Count);
        Assert.Equal(order.Distinct().Count(), order.Count);
        Assert.DoesNotContain("bogus", order);
        Assert.Equal(new[] { "pin" }, hidden);
        // Stored relative order survives for the ids that were stored.
        Assert.True(order.IndexOf("home") < order.IndexOf("back"));
        Assert.True(order.IndexOf("back") < order.IndexOf("reload"));
    }

    [Fact]
    public void ToolbarLayout_MissingIdsAreAppendedNotHidden()
    {
        // A layout saved before an item existed must SHOW the new item
        // (at its canonical place), not hide it.
        var (order, hidden) = ToolbarLayout.Sanitize(new[] { "back", "reload" }, Array.Empty<string>());
        Assert.Equal(ToolbarLayout.All.Count, order.Count);
        Assert.Empty(hidden);
        Assert.Equal(ToolbarLayout.All.Count,
            ToolbarLayout.VisibleIds(order, hidden, ToolbarGroup.Left).Count +
            ToolbarLayout.VisibleIds(order, hidden, ToolbarGroup.Right).Count);
    }

    [Fact]
    public void ToolbarLayout_BackCanNeverBeHidden()
    {
        var (_, hidden) = ToolbarLayout.Sanitize(null, new[] { "back", "home" });
        Assert.DoesNotContain("back", hidden);
        Assert.Contains("home", hidden);
        var (order, _) = ToolbarLayout.Sanitize(null, null);
        Assert.Contains("back", ToolbarLayout.VisibleIds(order, hidden, ToolbarGroup.Left));
        Assert.DoesNotContain("home", ToolbarLayout.VisibleIds(order, hidden, ToolbarGroup.Left));
    }

    [Fact]
    public void ToolbarLayout_ReorderWithinGroupIsHonored()
    {
        var custom = new[] { "reload", "home", "back", "forward", "zoomin", "zoomlabel", "zoomout", "browser", "pin" };
        var (order, hidden) = ToolbarLayout.Sanitize(custom, Array.Empty<string>());
        Assert.Equal(new[] { "reload", "home", "back", "forward" },
            ToolbarLayout.VisibleIds(order, hidden, ToolbarGroup.Left));
        Assert.Equal(new[] { "zoomin", "zoomlabel", "zoomout", "browser", "pin" },
            ToolbarLayout.VisibleIds(order, hidden, ToolbarGroup.Right));
    }

    // ── #46 shell text scale ──

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.25, 1.25)]
    [InlineData(1.5, 1.5)]
    [InlineData(1.1, 1.0)]
    [InlineData(1.13, 1.25)]
    [InlineData(2.0, 1.5)]
    [InlineData(0.5, 1.0)]
    [InlineData(double.NaN, 1.0)]
    public void ShellTextScale_SnapsToOfferedFactors(double input, double expected) =>
        Assert.Equal(expected, ShellTextScale.Sanitize(input));

    // ── #41 settings search ──

    [Theory]
    [InlineData("theme", SettingsSearch.AppearanceId)]
    [InlineData("dark", SettingsSearch.AppearanceId)]
    [InlineData("toolbar", SettingsSearch.AppearanceId)]
    [InlineData("reset", SettingsSearch.DataId)]
    [InlineData("portable", SettingsSearch.DataId)]
    [InlineData("export", SettingsSearch.DataId)]
    [InlineData("quiet", SettingsSearch.TrayId)]
    [InlineData("single click", SettingsSearch.TrayId)]
    [InlineData("rename", SettingsSearch.ProfilesId)]
    [InlineData("custom url", SettingsSearch.ProfilesId)]
    [InlineData("autostart", SettingsSearch.BehaviorId)]
    [InlineData("wizard", SettingsSearch.BehaviorId)]
    [InlineData("collision", SettingsSearch.DownloadsId)]
    [InlineData("history", SettingsSearch.DownloadsId)]
    public void SettingsSearch_FindsTheRightSection(string query, string expectedSection)
    {
        var matches = SettingsSearch.Search(query);
        Assert.Contains(matches, m => m.Id == expectedSection);
    }

    [Fact]
    public void SettingsSearch_BlankOrUnknownQuery()
    {
        Assert.Empty(SettingsSearch.Search(null));
        Assert.Empty(SettingsSearch.Search("   "));
        Assert.Empty(SettingsSearch.Search("zzzznothing"));
    }

    [Fact]
    public void SettingsSearch_EveryTermMustMatch()
    {
        // "profile zoom" — profiles has both words; the Zoom section
        // lacks "profile", downloads lacks both.
        var matches = SettingsSearch.Search("profile zoom");
        Assert.Equal(new[] { SettingsSearch.ProfilesId }, matches.Select(m => m.Id).ToArray());
    }

    // ── #42 reset ──

    [Fact]
    public void SettingsReset_BackupNameIsTimestamped()
    {
        var name = SettingsReset.BackupFileName(new DateTimeOffset(2026, 10, 24, 15, 4, 9, TimeSpan.FromHours(-5)));
        Assert.Equal("settings-backup-20261024-150409.json", name);
    }

    [Fact]
    public void SettingsReset_ResetsPreferencesPreservesIdentity()
    {
        var settings = new AppSettings
        {
            CloseToTray = false,
            StartWithWindows = true,
            ZoomFactor = 1.5,
            Theme = ThemeMode.Light,
            AccentColor = "Blue",
            ShellFontScale = 1.5,
            CheckForUpdates = false,
            FirstRunCompleted = true,
            QuietHoursEnabled = true,
            ActiveProfileId = "work",
        };
        settings.Profiles.Add(new AccountProfile { Id = "work", Name = "Work" });
        settings.ActiveProfileId = "work";
        settings.LastUrlByProfile["work"] = "https://muse.ai/chat/abc";
        settings.PendingProfileFolderDeletions.Add("gone1");
        settings.SettingsLastSection = SettingsSearch.DataId;
        settings.ToolbarHidden.Add("home");
        settings.SchemaVersion = 2;
        settings.Sanitized();

        SettingsReset.ApplyDefaults(settings);

        // SchemaVersion is format state, not a preference: preserved,
        // so App can still tell upgrades from first runs afterwards.
        Assert.Equal(2, settings.SchemaVersion);

        // Preferences are back at factory defaults…
        var fresh = new AppSettings();
        Assert.Equal(fresh.CloseToTray, settings.CloseToTray);
        Assert.Equal(fresh.StartWithWindows, settings.StartWithWindows);
        Assert.Equal(fresh.ZoomFactor, settings.ZoomFactor);
        Assert.Equal(fresh.Theme, settings.Theme);
        Assert.Equal(fresh.AccentColor, settings.AccentColor);
        Assert.Equal(fresh.ShellFontScale, settings.ShellFontScale);
        Assert.Equal(fresh.CheckForUpdates, settings.CheckForUpdates);
        Assert.Equal(fresh.QuietHoursEnabled, settings.QuietHoursEnabled);
        Assert.Empty(settings.ToolbarHidden);
        Assert.Equal(ToolbarLayout.DefaultOrder(), settings.ToolbarOrder);
        // …including the first-run flag (the wizard offers itself again)…
        Assert.False(settings.FirstRunCompleted);
        // …but identity and session state survive untouched.
        Assert.Equal("work", settings.ActiveProfileId);
        Assert.Contains(settings.Profiles, p => p.Id == "work");
        Assert.Equal("https://muse.ai/chat/abc", settings.LastUrlByProfile["work"]);
        Assert.Contains("gone1", settings.PendingProfileFolderDeletions);
        Assert.Equal(SettingsSearch.DataId, settings.SettingsLastSection);
    }

    // ── #47 start page ──

    [Theory]
    [InlineData("https://muse.ai/chat/abc", true)]
    [InlineData("https://muse.ai/", true)]
    [InlineData("https://accounts.google.com/signin", true)] // in-app sign-in host
    [InlineData("http://muse.ai/chat/abc", false)]          // not HTTPS
    [InlineData("https://example.com/", false)]              // not an in-app host
    [InlineData("https://muse.ai.evil.com/", false)]        // look-alike
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void StartPagePolicy_ValidatesCustomUrls(string? url, bool expected) =>
        Assert.Equal(expected, StartPagePolicy.IsValidCustomUrl(url));

    [Fact]
    public void StartPagePolicy_ResolveModes()
    {
        var last = "https://muse.ai/chat/last";
        var home = UrlPolicy.HomeUrl;

        var p = new AccountProfile { StartPage = StartPageMode.LastPage };
        Assert.Equal((last, false), StartPagePolicy.Resolve(p, last));
        Assert.Equal((home, false), StartPagePolicy.Resolve(p, null));

        p.StartPage = StartPageMode.Home;
        Assert.Equal((home, false), StartPagePolicy.Resolve(p, last));

        p.StartPage = StartPageMode.Custom;
        p.CustomStartUrl = "https://muse.ai/chat/pinned";
        Assert.Equal(("https://muse.ai/chat/pinned", false), StartPagePolicy.Resolve(p, last));

        // An invalid custom URL falls back to HOME and says so.
        p.CustomStartUrl = "https://example.com/nope";
        Assert.Equal((home, true), StartPagePolicy.Resolve(p, last));
        p.CustomStartUrl = null;
        Assert.Equal((home, true), StartPagePolicy.Resolve(p, last));

        Assert.Equal((last, false), StartPagePolicy.Resolve(null, last));
    }

    [Fact]
    public void StartPage_LegacyStartAtHomeMigratesIntoTheMode()
    {
        // A pre-v0.9.0 file: StartAtHome true, no mode stored.
        var settings = new AppSettings();
        settings.Profiles[0].StartAtHome = true;
        settings.Sanitized();
        Assert.Equal(StartPageMode.Home, settings.Profiles[0].StartPage);
        Assert.True(settings.Profiles[0].StartAtHome); // bool kept in sync
        Assert.Equal(UrlPolicy.HomeUrl, settings.StartupUrl);

        // And the reverse direction: setting the mode rewrites the bool.
        settings.Profiles[0].StartPage = StartPageMode.Custom;
        settings.Profiles[0].CustomStartUrl = "https://muse.ai/chat/pinned";
        settings.Sanitized();
        Assert.False(settings.Profiles[0].StartAtHome);
        Assert.Equal("https://muse.ai/chat/pinned", settings.StartupUrl);
    }

    [Fact]
    public void StartPage_CustomUrlSanitizeTrimsAndCaps()
    {
        var settings = new AppSettings();
        settings.Profiles[0].CustomStartUrl = "  https://muse.ai/chat/x  ";
        settings.Sanitized();
        Assert.Equal("https://muse.ai/chat/x", settings.Profiles[0].CustomStartUrl);

        settings.Profiles[0].CustomStartUrl = "   ";
        settings.Sanitized();
        Assert.Null(settings.Profiles[0].CustomStartUrl);

        settings.Profiles[0].StartPage = (StartPageMode)99; // hand-edited garbage
        settings.Sanitized();
        Assert.Equal(StartPageMode.LastPage, settings.Profiles[0].StartPage);
    }

    [Fact]
    public void StartPage_CopySettingsCarriesModeAndCustomUrl()
    {
        var source = new AccountProfile
        {
            StartPage = StartPageMode.Custom,
            CustomStartUrl = "https://muse.ai/chat/pinned",
        };
        var copy = Profiles.CopySettingsFrom(source, "newid", "Copy");
        Assert.Equal(StartPageMode.Custom, copy.StartPage);
        Assert.Equal("https://muse.ai/chat/pinned", copy.CustomStartUrl);
    }

    [Fact]
    public void StartupUrl_CustomFallbackIsReported()
    {
        var settings = new AppSettings();
        settings.Profiles[0].StartPage = StartPageMode.Custom;
        settings.Profiles[0].CustomStartUrl = "https://example.com/nope";
        settings.Sanitized();
        var (url, fellBack) = settings.ResolveStartupUrl();
        Assert.Equal(UrlPolicy.HomeUrl, url);
        Assert.True(fellBack);
        Assert.Equal(url, settings.StartupUrl);
    }

    // ── #48 settings-window memory sanitize ──

    [Fact]
    public void SettingsMemory_Sanitized()
    {
        var settings = new AppSettings
        {
            SettingsLastSection = "not-a-section",
            SettingsWindowWidth = 50,
            SettingsWindowHeight = 99999,
        };
        settings.Sanitized();
        Assert.Null(settings.SettingsLastSection);
        Assert.Equal(480, settings.SettingsWindowWidth);
        Assert.Equal(0, settings.SettingsWindowHeight);

        settings.SettingsLastSection = SettingsSearch.DownloadsId;
        settings.SettingsWindowWidth = 640;
        settings.SettingsWindowHeight = 700;
        settings.Sanitized();
        Assert.Equal(SettingsSearch.DownloadsId, settings.SettingsLastSection);
        Assert.Equal(640, settings.SettingsWindowWidth);
        Assert.Equal(700, settings.SettingsWindowHeight);
    }

    // ── #49 portable location ──

    [Fact]
    public void SettingsLocation_PortablePutsDataBesideTheExe()
    {
        var exeDir = Path.Combine("Apps", "MuseDesktop");
        var installed = Path.Combine("Users", "x", "AppData", "Roaming", "MuseDesktop");
        var loc = SettingsLocation.Resolve(exeDir, installed, markerExists: true);
        Assert.True(loc.IsPortable);
        Assert.Equal(Path.Combine(exeDir, "data"), loc.RootDir);
        Assert.Equal(Path.Combine(exeDir, "data", "settings.json"), loc.SettingsPath);
        Assert.Equal(Path.Combine(exeDir, "data", "downloads.json"), loc.DownloadsHistoryPath);
        Assert.Equal(Path.Combine(exeDir, "data", "logs"), loc.LogDir);
    }

    [Fact]
    public void SettingsLocation_InstalledIsTheDefault()
    {
        var installed = Path.Combine("Users", "x", "AppData", "Roaming", "MuseDesktop");
        var loc = SettingsLocation.Resolve(Path.Combine("Apps", "MuseDesktop"), installed, markerExists: false);
        Assert.False(loc.IsPortable);
        Assert.Equal(installed, loc.RootDir);
        Assert.Equal(Path.Combine(installed, "settings.json"), loc.SettingsPath);
        Assert.Equal("portable.txt", SettingsLocation.MarkerFileName);
    }

    // ── settings plumbing: every S5 field round-trips ──

    private static void AssertS5FieldsEqual(AppSettings expected, AppSettings actual)
    {
        Assert.Equal(expected.Theme, actual.Theme);
        Assert.Equal(expected.AccentColor, actual.AccentColor);
        Assert.Equal(expected.ShellFontScale, actual.ShellFontScale);
        Assert.Equal(expected.ToolbarOrder, actual.ToolbarOrder);
        Assert.Equal(expected.ToolbarHidden, actual.ToolbarHidden);
        Assert.Equal(expected.SettingsLastSection, actual.SettingsLastSection);
        Assert.Equal(expected.SettingsWindowWidth, actual.SettingsWindowWidth);
        Assert.Equal(expected.SettingsWindowHeight, actual.SettingsWindowHeight);
        Assert.Equal(expected.FirstRunCompleted, actual.FirstRunCompleted);
        var ep = expected.Profiles[0];
        var ap = actual.Profiles[0];
        Assert.Equal(ep.StartPage, ap.StartPage);
        Assert.Equal(ep.CustomStartUrl, ap.CustomStartUrl);
        Assert.Equal(ep.StartAtHome, ap.StartAtHome);
    }

    [Fact]
    public void Settings_S5FieldsRoundTripThroughCloneAndExportImport()
    {
        var original = new AppSettings
        {
            Theme = ThemeMode.FollowSystem,
            AccentColor = "Purple",
            ShellFontScale = 1.25,
            SettingsLastSection = SettingsSearch.AppearanceId,
            SettingsWindowWidth = 620,
            SettingsWindowHeight = 700,
            FirstRunCompleted = true,
        };
        original.ToolbarOrder = new List<string> { "home", "back", "forward", "reload", "zoomin", "zoomlabel", "zoomout", "pin", "browser" };
        original.ToolbarHidden = new List<string> { "pin", "home" };
        original.Profiles[0].StartPage = StartPageMode.Custom;
        original.Profiles[0].CustomStartUrl = "https://muse.ai/chat/pinned";
        original.Sanitized();

        var clone = original.Clone();
        AssertS5FieldsEqual(original, clone);

        var json = SettingsStore.Export(original);
        Assert.True(SettingsStore.TryImport(json, out var imported, out var error), error);
        Assert.NotNull(imported);
        AssertS5FieldsEqual(original, imported!);
    }

    [Fact]
    public void Settings_S5FieldsSanitizeFromGarbage()
    {
        var settings = new AppSettings
        {
            Theme = (ThemeMode)42,
            AccentColor = "chartreuse",
            ShellFontScale = 3.7,
        };
        settings.ToolbarOrder = new List<string> { "bogus", "back", "back" };
        settings.ToolbarHidden = new List<string> { "back", "bogus" };
        settings.Sanitized();
        Assert.Equal(ThemeMode.Dark, settings.Theme);
        Assert.Equal("Red", settings.AccentColor);
        Assert.Equal(1.5, settings.ShellFontScale);
        Assert.Equal(ToolbarLayout.All.Count, settings.ToolbarOrder.Count);
        Assert.Empty(settings.ToolbarHidden); // back unhidable, bogus unknown
    }
}
