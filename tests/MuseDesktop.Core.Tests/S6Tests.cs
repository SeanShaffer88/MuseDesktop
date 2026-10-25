using System.Xml.Linq;
using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>Slice 6 (v0.10.0, items 51–60): accessibility, keyboard &amp; focus — the Core half.</summary>
public class S6Tests
{
    // ───────────────────────── #53 gestures ─────────────────────────

    [Theory]
    [InlineData("Ctrl+OemComma", "Ctrl+OemComma", "Ctrl+,")]
    [InlineData("ctrl+shift+d", "Ctrl+Shift+D", "Ctrl+Shift+D")]
    [InlineData("Alt+Home", "Alt+Home", "Alt+Home")]
    [InlineData("F8", "F8", "F8")]
    [InlineData("Ctrl+,", "Ctrl+OemComma", "Ctrl+,")]
    [InlineData("Ctrl+D0", "Ctrl+D0", "Ctrl+0")]
    [InlineData("Control+Alt+Delete", "Ctrl+Alt+Delete", "Ctrl+Alt+Delete")]
    public void Gesture_ParseFormatDisplay(string input, string canonical, string display)
    {
        Assert.True(ShortcutGesture.TryParse(input, out var gesture), input);
        Assert.Equal(canonical, gesture.Format());
        Assert.Equal(display, gesture.DisplayText());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Banana")]
    [InlineData("Ctrl+Escape")] // Esc is never a capturable gesture key
    public void Gesture_RejectsNonGestures(string? input) =>
        Assert.False(ShortcutGesture.TryParse(input, out _));

    // ───────────────────────── #52 catalog integrity ─────────────────────────

    [Fact]
    public void Catalog_IdsAreUniqueAndEveryGestureParses()
    {
        Assert.Equal(ShortcutCatalog.All.Count, ShortcutCatalog.All.Select(a => a.Id).Distinct().Count());
        foreach (var action in ShortcutCatalog.All)
        {
            Assert.NotEmpty(action.Gestures);
            foreach (var text in action.Gestures)
            {
                // "Escape" is display-only (Esc can never be remapped
                // onto), everything else must be a real gesture.
                if (text == "Escape")
                    continue;
                Assert.True(ShortcutGesture.TryParse(text, out _), $"{action.Id}: {text}");
            }
        }
    }

    [Fact]
    public void Catalog_CoversTheShellsRealBindings()
    {
        // The bindings the main window's key handler actually dispatches
        // (MainWindow.OnPreviewKeyDown + the find bar). If the shell
        // gains or loses a binding, this list and the catalog move
        // together — that is what keeps the Shortcuts window honest.
        var expected = new[]
        {
            "settings", "downloads", "home", "find", "shortcuts", "print",
            "reload", "restore-closed", "back", "forward", "zoom-in",
            "zoom-out", "zoom-reset", "focus-mode", "fullscreen", "escape",
        };
        Assert.Equal(
            expected.OrderBy(x => x),
            ShortcutCatalog.All.Select(a => a.Id).OrderBy(x => x));
    }

    [Fact]
    public void Catalog_OnlyThreeRemappable_AndNoGlobalHotkeys()
    {
        Assert.Equal(
            new[] { ShortcutCatalog.DownloadsId, ShortcutCatalog.HomeId, ShortcutCatalog.SettingsId },
            ShortcutCatalog.Remappable.Select(a => a.Id).OrderBy(x => x));
        // Honest scope (#53): the CATALOG holds no global hotkey —
        // the S11 global show/hide key lives outside it by design
        // (its own settings + Core's GlobalHotkey planner, #96).
        Assert.All(ShortcutCatalog.All, a => Assert.Equal(ShortcutScope.InApp, a.Scope));
    }

    [Fact]
    public void Catalog_RemappableDefaultsDoNotCollideWithFixed()
    {
        var fixedGestures = ShortcutCatalog.FixedGestures();
        foreach (var action in ShortcutCatalog.Remappable)
        {
            Assert.True(ShortcutGesture.TryParse(action.Gestures[0], out var gesture));
            Assert.False(fixedGestures.ContainsKey(gesture), action.Id);
        }
    }

    // ───────────────────────── #53 bindings ─────────────────────────

    [Fact]
    public void Bindings_DefaultsWhenNoOverrides()
    {
        var effective = ShortcutBindings.Effective(null);
        Assert.Equal("Ctrl+OemComma", effective[ShortcutCatalog.SettingsId].Format());
        Assert.Equal("Ctrl+Shift+D", effective[ShortcutCatalog.DownloadsId].Format());
        Assert.Equal("Alt+Home", effective[ShortcutCatalog.HomeId].Format());
    }

    [Fact]
    public void Bindings_TrySetHappyPath_AndReset()
    {
        Assert.True(ShortcutGesture.TryParse("Ctrl+Alt+S", out var gesture));
        Assert.True(ShortcutBindings.TrySet(null, ShortcutCatalog.SettingsId, gesture,
            out var updated, out var error));
        Assert.Null(error);
        Assert.Equal("Ctrl+Alt+S", updated[ShortcutCatalog.SettingsId]);
        Assert.Equal(gesture, ShortcutBindings.EffectiveGesture(ShortcutCatalog.SettingsId, updated));

        var reset = ShortcutBindings.Without(updated, ShortcutCatalog.SettingsId);
        Assert.False(reset.ContainsKey(ShortcutCatalog.SettingsId));
        Assert.Equal("Ctrl+OemComma",
            ShortcutBindings.EffectiveGesture(ShortcutCatalog.SettingsId, reset).Format());
    }

    [Fact]
    public void Bindings_TrySetRejectsFixedGesture()
    {
        Assert.True(ShortcutGesture.TryParse("Ctrl+F", out var find));
        Assert.False(ShortcutBindings.TrySet(null, ShortcutCatalog.SettingsId, find,
            out _, out var error));
        Assert.Contains("Find in page", error);
    }

    [Fact]
    public void Bindings_TrySetRejectsAnotherActionsGesture()
    {
        // Downloads wants Settings' default.
        Assert.True(ShortcutGesture.TryParse("Ctrl+OemComma", out var settingsDefault));
        Assert.False(ShortcutBindings.TrySet(null, ShortcutCatalog.DownloadsId, settingsDefault,
            out _, out var error));
        Assert.Contains("Open Settings", error);
    }

    [Fact]
    public void Bindings_TrySetRejectsBareKeyAndUnknownAction()
    {
        Assert.True(ShortcutGesture.TryParse("S", out var bare));
        Assert.False(ShortcutBindings.TrySet(null, ShortcutCatalog.SettingsId, bare,
            out _, out var error));
        Assert.Contains("Ctrl or Alt", error);

        Assert.True(ShortcutGesture.TryParse("Ctrl+Alt+S", out var ok));
        Assert.False(ShortcutBindings.TrySet(null, "find", ok, out _, out _)); // fixed action
        Assert.False(ShortcutBindings.TrySet(null, "nope", ok, out _, out _)); // unknown action
    }

    [Fact]
    public void Bindings_SanitizeDropsGarbageDeterministically()
    {
        var stored = new Dictionary<string, string>
        {
            ["settings"] = "Ctrl+Alt+S",        // fine
            ["downloads"] = "Ctrl+OemComma",    // Settings' old default — but Settings moved away above, so this is free
            ["home"] = "banana",                // unparseable → dropped
            ["unknown-action"] = "Ctrl+Alt+X",  // unknown id → dropped
            ["find"] = "Ctrl+Alt+F",            // fixed action → dropped
        };
        var clean = ShortcutBindings.SanitizeOverrides(stored);
        Assert.Equal(new Dictionary<string, string>
        {
            ["settings"] = "Ctrl+Alt+S",
            ["downloads"] = "Ctrl+OemComma",
        }, clean);

        // …but taking a gesture another action still uses (here by
        // default) IS a collision and drops.
        var stealing = ShortcutBindings.SanitizeOverrides(
            new Dictionary<string, string> { ["downloads"] = "Ctrl+OemComma" });
        Assert.Empty(stealing);

        // A modifier-less stored gesture drops too.
        var bare = ShortcutBindings.SanitizeOverrides(
            new Dictionary<string, string> { ["home"] = "H" });
        Assert.Empty(bare);
    }

    // ───────────────────────── #55 reduce motion ─────────────────────────

    [Theory]
    [InlineData(ReduceMotionSetting.FollowSystem, true, false)]  // Windows animations ON → motion kept
    [InlineData(ReduceMotionSetting.FollowSystem, false, true)]  // Windows animations OFF → reduced
    [InlineData(ReduceMotionSetting.On, true, true)]
    [InlineData(ReduceMotionSetting.On, false, true)]
    [InlineData(ReduceMotionSetting.Off, true, false)]
    [InlineData(ReduceMotionSetting.Off, false, false)]
    public void MotionPolicy_Matrix(ReduceMotionSetting setting, bool systemAnimations, bool reduced) =>
        Assert.Equal(reduced, MotionPolicy.IsReduced(setting, systemAnimations));

    // ───────────────────────── #54 announcement wording ─────────────────────────

    [Fact]
    public void Announcement_WordingMatchesTheStatusBar()
    {
        Assert.Equal("Download complete: report.pdf", AnnouncementText.DownloadCompleted("report.pdf"));
        Assert.Equal("Download interrupted: report.pdf (NetworkDisconnected)",
            AnnouncementText.DownloadFailed("report.pdf", "NetworkDisconnected"));
        Assert.Equal("Download interrupted: report.pdf", AnnouncementText.DownloadFailed("report.pdf", null));
        Assert.Equal("MuseDesktop v0.10.0 is available — see the ⋯ menu",
            AnnouncementText.UpdateAvailable("v0.10.0"));
    }

    // ───────────────────────── #57 zoom steps & range ─────────────────────────

    [Fact]
    public void Zoom_StandardTableHasFineStepsNear100()
    {
        var steps = ZoomMath.Steps;
        // 5% steps across 75–125%, the nudge range.
        foreach (var expected in new[] { 0.75, 0.80, 0.85, 0.90, 0.95, 1.00, 1.05, 1.10, 1.15, 1.20, 1.25 })
            Assert.Contains(expected, steps);
        Assert.Equal(0.50, steps.Min());
        Assert.Equal(2.00, steps.Max());
        Assert.Equal(steps.OrderBy(x => x), steps); // strictly ordered table
    }

    [Theory]
    [InlineData(1.00, 1.05)]
    [InlineData(1.25, 1.50)]
    [InlineData(0.70, 0.75)]  // arbitrary value steps to the next table stop
    [InlineData(2.00, 2.00)]
    public void Zoom_StepUpStandard(double from, double to) =>
        Assert.Equal(to, ZoomMath.StepUp(from), precision: 3);

    [Theory]
    [InlineData(2.00, 2.50, true)]
    [InlineData(5.00, 5.00, true)]
    [InlineData(0.50, 0.33, false)]
    [InlineData(0.25, 0.25, false)]
    [InlineData(1.10, 1.15, true)] // identical to standard inside the shared range
    public void Zoom_ExtendedSteps(double from, double to, bool up)
    {
        var actual = up ? ZoomMath.StepUp(from, extendedRange: true)
                        : ZoomMath.StepDown(from, extendedRange: true);
        Assert.Equal(to, actual, precision: 3);
    }

    [Theory]
    [InlineData(3.0, false, 2.0)]
    [InlineData(3.0, true, 3.0)]
    [InlineData(0.30, false, 0.50)]
    [InlineData(0.30, true, 0.30)]
    [InlineData(99.0, true, 5.0)]
    public void Zoom_ClampHonorsTheRangeSetting(double input, bool extended, double expected) =>
        Assert.Equal(expected, ZoomMath.Clamp(input, extended), precision: 3);

    [Fact]
    public void Zoom_FormatShowsExtendedPercents()
    {
        Assert.Equal("300%", ZoomMath.FormatPercent(3.0));
        Assert.Equal("25%", ZoomMath.FormatPercent(0.25));
        Assert.Equal("500%", ZoomMath.FormatPercent(5.0));
    }

    [Fact]
    public void Zoom_ExtendedSettingFlowsThroughSettingsSanitize()
    {
        var on = new AppSettings { ExtendedZoomRange = true, ZoomFactor = 3.0 };
        on.Sanitized();
        Assert.Equal(3.0, on.ZoomFactor, precision: 3);

        var off = new AppSettings { ExtendedZoomRange = false, ZoomFactor = 3.0 };
        off.Sanitized();
        Assert.Equal(2.0, off.ZoomFactor, precision: 3);

        // Per-profile zooms obey the same switch (S3 #26 storage path).
        var profiled = new AppSettings { ExtendedZoomRange = true };
        profiled.Profiles[0].ZoomFactor = 4.0;
        profiled.Sanitized();
        Assert.Equal(4.0, profiled.Profiles[0].ZoomFactor!.Value, precision: 3);
        Assert.Equal(4.0, Profiles.EffectiveZoom(profiled.Profiles[0], 1.0, extendedRange: true), precision: 3);
        Assert.Equal(2.0, Profiles.EffectiveZoom(profiled.Profiles[0], 1.0), precision: 3);
    }

    // ───────────────────────── #60 placement clamp ─────────────────────────

    private static readonly ScreenRect Primary = new(0, 0, 1920, 1080);
    private static readonly ScreenRect SecondaryRight = new(1920, 0, 1920, 1080);

    [Fact]
    public void Clamp_WindowOnLiveMonitorIsUntouched()
    {
        var window = new ScreenRect(2100, 100, 1280, 860);
        Assert.Equal(window, PlacementClamp.Clamp(window, new[] { Primary, SecondaryRight }));
    }

    [Fact]
    public void Clamp_DisconnectedMonitorMovesWindowToPrimary()
    {
        // The window was saved entirely on the secondary; only the
        // primary remains → it lands inside the primary, size kept.
        var window = new ScreenRect(2100, 100, 1280, 860);
        var clamped = PlacementClamp.Clamp(window, new[] { Primary });
        Assert.Equal(new ScreenRect(640, 100, 1280, 860), clamped);
    }

    [Fact]
    public void Clamp_StraddlingWindowWithEnoughVisibleStays()
    {
        // 200px of width on the primary (≥ the 80px minimum) — the
        // user's placement is deliberate and both monitors exist.
        var window = new ScreenRect(1720, 100, 1280, 860);
        Assert.Equal(window, PlacementClamp.Clamp(window, new[] { Primary, SecondaryRight }));
    }

    [Fact]
    public void Clamp_BarelyVisibleSliverIsPulledBack()
    {
        // Only 30px showing on the one remaining monitor.
        var window = new ScreenRect(1890, 100, 1280, 860);
        var clamped = PlacementClamp.Clamp(window, new[] { Primary });
        Assert.Equal(640, clamped.Left);
        Assert.Equal(100, clamped.Top);
    }

    [Fact]
    public void Clamp_OversizedWindowPinsToPrimaryTopLeft()
    {
        var window = new ScreenRect(3000, 2000, 2560, 1440);
        var clamped = PlacementClamp.Clamp(window, new[] { Primary });
        Assert.Equal(new ScreenRect(0, 0, 2560, 1440), clamped);
    }

    [Fact]
    public void Clamp_NoMonitorsKnownLeavesPlacementAlone()
    {
        var window = new ScreenRect(5000, 5000, 1280, 860);
        Assert.Equal(window, PlacementClamp.Clamp(window, Array.Empty<ScreenRect>()));
    }

    [Fact]
    public void DipMath_ConvertsMixedScaleMonitors()
    {
        // A 150%-scaled 4K panel left of the primary: physical
        // (-3840, 0, 3840, 2160) at 1.5 → DIP (-2560, 0, 2560, 1440).
        var dip = DipMath.PhysicalToDip(new ScreenRect(-3840, 0, 3840, 2160), 1.5);
        Assert.Equal(new ScreenRect(-2560, 0, 2560, 1440), dip);
        // Unknown scale reads as 1.0, never a divide-by-zero.
        Assert.Equal(new ScreenRect(10, 10, 100, 100),
            DipMath.PhysicalToDip(new ScreenRect(10, 10, 100, 100), 0));
        // A window on that scaled monitor clamps in DIP space.
        var window = new ScreenRect(-2500, 50, 1280, 860);
        Assert.Equal(window, PlacementClamp.Clamp(window, new[] { dip, Primary }));
    }

    // ───────────────────────── settings plumbing (S6 fields) ─────────────────────────

    [Fact]
    public void Settings_S6FieldsRoundTripThroughCloneAndExportImport()
    {
        var settings = new AppSettings
        {
            ReduceMotion = ReduceMotionSetting.On,
            ExtendedZoomRange = true,
            ShortcutOverrides = new Dictionary<string, string>
            {
                [ShortcutCatalog.SettingsId] = "Ctrl+Alt+S",
                [ShortcutCatalog.DownloadsId] = "Ctrl+Alt+D",
            },
        };
        var clone = settings.Clone();
        Assert.Equal(ReduceMotionSetting.On, clone.ReduceMotion);
        Assert.True(clone.ExtendedZoomRange);
        Assert.Equal(settings.ShortcutOverrides, clone.ShortcutOverrides);

        var json = SettingsStore.Export(settings);
        Assert.True(SettingsStore.TryImport(json, out var imported, out _));
        Assert.Equal(ReduceMotionSetting.On, imported!.ReduceMotion);
        Assert.True(imported.ExtendedZoomRange);
        Assert.Equal(settings.ShortcutOverrides, imported.ShortcutOverrides);
    }

    [Fact]
    public void Settings_S6GarbageSanitizes()
    {
        var settings = new AppSettings
        {
            ReduceMotion = (ReduceMotionSetting)99,
            ShortcutOverrides = new Dictionary<string, string>
            {
                [ShortcutCatalog.SettingsId] = "not a gesture",
                ["bogus"] = "Ctrl+Alt+B",
                [ShortcutCatalog.HomeId] = "Ctrl+F", // fixed gesture — conflict, dropped
            },
        };
        settings.Sanitized();
        Assert.Equal(ReduceMotionSetting.FollowSystem, settings.ReduceMotion);
        Assert.Empty(settings.ShortcutOverrides);
    }

    [Fact]
    public void Settings_ResetClearsS6PreferencesButKeepsProfiles()
    {
        var settings = new AppSettings
        {
            ReduceMotion = ReduceMotionSetting.On,
            ExtendedZoomRange = true,
            ShortcutOverrides = new Dictionary<string, string>
            {
                [ShortcutCatalog.HomeId] = "Ctrl+Alt+H",
            },
        };
        var profileCount = settings.Profiles.Count;
        SettingsReset.ApplyDefaults(settings);
        Assert.Equal(ReduceMotionSetting.FollowSystem, settings.ReduceMotion);
        Assert.False(settings.ExtendedZoomRange);
        Assert.Empty(settings.ShortcutOverrides);
        Assert.Equal(profileCount, settings.Profiles.Count);
    }

    [Fact]
    public void SettingsSearch_FindsTheAccessibilitySection()
    {
        Assert.Contains(SettingsSearch.Search("keyboard"), s => s.Id == SettingsSearch.AccessibilityId);
        Assert.Contains(SettingsSearch.Search("remap"), s => s.Id == SettingsSearch.AccessibilityId);
        Assert.Contains(SettingsSearch.Search("reduce motion"), s => s.Id == SettingsSearch.AccessibilityId);
        Assert.True(SettingsSearch.IsKnownSection(SettingsSearch.AccessibilityId));
    }
}

/// <summary>
/// The #59 audit: every interactive control declared in the shell's
/// XAML must carry an AutomationProperties.Name, a ToolTip, or visible
/// text of its own (Content/Header/inner text) — so a future control
/// can't silently arrive invisible to a screen reader. Code-created
/// controls (the toolbar editor's checkboxes, menu items built in
/// handlers) are out of scope by construction; DataTemplate content is
/// skipped because its text arrives by binding.
/// </summary>
public class XamlAccessibilityAuditTests
{
    private static readonly string[] InteractiveElements =
    {
        "Button", "MenuItem", "TextBox", "ComboBox", "CheckBox",
        "RadioButton", "Hyperlink", "ToggleButton", "ListBox",
    };

    private static string RepoRoot()
    {
        // The test assembly's own folder (bin/<config>/net10.0) — NOT
        // AppContext.BaseDirectory, which under the xUnit console
        // runner points at the runner's folder instead.
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(typeof(XamlAccessibilityAuditTests).Assembly.Location)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MuseDesktop.sln"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "MuseDesktop")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }

    private static bool HasAccessibleLabel(XElement element)
    {
        string? Attr(string name) => element.Attribute(name)?.Value;
        if (!string.IsNullOrWhiteSpace(Attr("AutomationProperties.Name")))
            return true;
        if (!string.IsNullOrWhiteSpace(Attr("ToolTip")))
            return true;
        if (!string.IsNullOrWhiteSpace(Attr("Content")))
            return true;
        if (!string.IsNullOrWhiteSpace(Attr("Header")))
            return true;
        // Plain inner text (a Hyperlink's or a text-content Button's).
        var innerText = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
        return innerText.Length > 0;
    }

    private static IEnumerable<XElement> DescendantsSkippingTemplates(XElement root)
    {
        foreach (var child in root.Elements())
        {
            if (child.Name.LocalName is "DataTemplate" || child.Name.LocalName.EndsWith(".ItemTemplate"))
                continue;
            yield return child;
            foreach (var nested in DescendantsSkippingTemplates(child))
                yield return nested;
        }
    }

    [Fact]
    public void EveryShellXamlControlHasAnAccessibleLabel()
    {
        var shellDir = Path.Combine(RepoRoot(), "src", "MuseDesktop");
        var xamlFiles = Directory.GetFiles(shellDir, "*.xaml");
        Assert.NotEmpty(xamlFiles);
        var failures = new List<string>();
        foreach (var file in xamlFiles)
        {
            var doc = XDocument.Load(file);
            Assert.NotNull(doc.Root);
            foreach (var element in DescendantsSkippingTemplates(doc.Root))
            {
                if (!InteractiveElements.Contains(element.Name.LocalName))
                    continue;
                if (!HasAccessibleLabel(element))
                {
                    var name = element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name")?.Value
                               ?? element.Attribute("Name")?.Value ?? "(unnamed)";
                    failures.Add($"{Path.GetFileName(file)}: <{element.Name.LocalName}> '{name}'");
                }
            }
        }
        Assert.True(failures.Count == 0,
            "Interactive shell controls without an AutomationProperties.Name, ToolTip, or text content:\n"
            + string.Join("\n", failures));
    }
}
