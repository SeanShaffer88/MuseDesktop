using System.Text.Json;
using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>
/// S11 (v1.0.0, items 96–100): the global show/hide hotkey policy, the
/// one launch-intent model (CLI + musedesktop:// links), the protocol
/// registration policy, and the settings plumbing for the three new
/// fields. The shell pieces (RegisterHotKey, registry, the wake
/// channel) stay thin over these.
/// </summary>
public class S11Tests
{
    private static readonly string[] ProfileIds = { Profiles.DefaultId, "work", "p123abc4567" };

    // ──────────────────────── #100 command line ────────────────────────

    [Theory]
    [InlineData("--home")]
    [InlineData("--downloads")]
    [InlineData("--dictate")]
    [InlineData("--help")]
    [InlineData("--version")]
    public void CommandLine_S11FlagsParse(string flag)
    {
        var options = CommandLine.Parse(new[] { flag });
        Assert.Empty(options.Unknown);
        switch (flag)
        {
            case "--home": Assert.True(options.OpenHome); break;
            case "--downloads": Assert.True(options.OpenDownloads); break;
            case "--dictate": Assert.True(options.QuickDictate); break;
            case "--help": Assert.True(options.ShowHelp); break;
            case "--version": Assert.True(options.ShowVersion); break;
        }
    }

    [Fact]
    public void CommandLine_CapturesProtocolUri_AndFirstOneWins()
    {
        var options = CommandLine.Parse(new[]
            { "musedesktop://settings", "--background", "musedesktop://home" });
        Assert.Equal("musedesktop://settings", options.ProtocolUri);
        Assert.True(options.Background);
        Assert.Empty(options.Unknown);
    }

    [Fact]
    public void CommandLine_UriIsNotAProfileValue()
    {
        // "--profile" consumes the next argument only when it doesn't
        // look like a flag; a link is captured as a link, not an id.
        var options = CommandLine.Parse(new[] { "musedesktop://profile/work" });
        Assert.Equal("musedesktop://profile/work", options.ProtocolUri);
        Assert.Null(options.ProfileId);
    }

    [Fact]
    public void CommandLine_UnknownStillTolerated()
    {
        var options = CommandLine.Parse(new[] { "--wibble", "-x" });
        Assert.Equal(2, options.Unknown.Count);
    }

    [Fact]
    public void UsageText_DocumentsEveryFlagAndRoute()
    {
        var text = CommandLineUsage.Text("1.0.0");
        foreach (var flag in new[]
                 {
                     "--profile", "--background", "--home", "--settings", "--downloads",
                     "--dictate", "--safe-mode", "--help", "--version",
                 })
            Assert.Contains(flag, text);
        foreach (var route in new[]
                 {
                     "musedesktop://open", "musedesktop://home", "musedesktop://settings",
                     "musedesktop://downloads", "musedesktop://help", "musedesktop://profile/<id>",
                 })
            Assert.Contains(route, text);
    }

    // ──────────────────────── #100 intent reduction ────────────────────────

    [Theory]
    [InlineData(new string[0], LaunchIntentKind.ShowWindow)]
    [InlineData(new[] { "--home" }, LaunchIntentKind.GoHome)]
    [InlineData(new[] { "--settings" }, LaunchIntentKind.OpenSettings)]
    [InlineData(new[] { "--downloads" }, LaunchIntentKind.OpenDownloads)]
    [InlineData(new[] { "--dictate" }, LaunchIntentKind.QuickDictate)]
    [InlineData(new[] { "--background" }, LaunchIntentKind.ShowWindow)] // process-level, not an intent
    [InlineData(new[] { "--safe-mode" }, LaunchIntentKind.ShowWindow)]   // launch-time only
    [InlineData(new[] { "--downloads", "--settings" }, LaunchIntentKind.OpenSettings)] // precedence
    [InlineData(new[] { "--home", "--downloads" }, LaunchIntentKind.OpenDownloads)]
    public void Intent_FromCommandLine(string[] args, LaunchIntentKind expected)
    {
        var intent = LaunchIntent.FromCommandLine(CommandLine.Parse(args));
        Assert.Equal(expected, intent.Kind);
    }

    [Fact]
    public void Intent_ProfileOutranksSurfaceFlags()
    {
        var intent = LaunchIntent.FromCommandLine(
            CommandLine.Parse(new[] { "--settings", "--profile", "work" }));
        Assert.Equal(LaunchIntentKind.SwitchProfile, intent.Kind);
        Assert.Equal("work", intent.ProfileId);
    }

    [Fact]
    public void Intent_JsonRoundTrip()
    {
        foreach (var intent in new[]
                 {
                     LaunchIntent.ShowWindow,
                     new LaunchIntent(LaunchIntentKind.GoHome),
                     new LaunchIntent(LaunchIntentKind.SwitchProfile, "work"),
                 })
        {
            Assert.Equal(intent, LaunchIntent.TryFromJson(intent.ToJson()));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("{\"kind\":\"Teleport\"}")]
    [InlineData("{\"kind\":\"SwitchProfile\"}")] // no profile id
    public void Intent_BadJsonReadsAsNull(string? json) =>
        Assert.Null(LaunchIntent.TryFromJson(json));

    [Fact]
    public void PendingIntentStore_SaveTakeDelete()
    {
        var dir = Path.Combine(Path.GetTempPath(), "md-s11-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "pending-intent.json");
        try
        {
            Assert.Null(PendingIntentStore.Take(path)); // missing
            PendingIntentStore.Save(path, new LaunchIntent(LaunchIntentKind.OpenDownloads));
            Assert.True(File.Exists(path));
            var taken = PendingIntentStore.Take(path);
            Assert.Equal(LaunchIntentKind.OpenDownloads, taken?.Kind);
            Assert.False(File.Exists(path)); // taken = deleted
            File.WriteAllText(path, "{corrupt");
            Assert.Null(PendingIntentStore.Take(path)); // corrupt reads null
            Assert.False(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    // ──────────────────────── #99 protocol routes ────────────────────────

    [Theory]
    [InlineData("musedesktop://open", LaunchIntentKind.ShowWindow)]
    [InlineData("musedesktop://open/", LaunchIntentKind.ShowWindow)]
    [InlineData("musedesktop://home", LaunchIntentKind.GoHome)]
    [InlineData("musedesktop://settings", LaunchIntentKind.OpenSettings)]
    [InlineData("musedesktop://downloads", LaunchIntentKind.OpenDownloads)]
    [InlineData("musedesktop://help", LaunchIntentKind.ShowHelp)]
    [InlineData("MuseDesktop://SETTINGS", LaunchIntentKind.OpenSettings)] // scheme/host are case-insensitive
    public void Protocol_ValidRoutes(string uri, LaunchIntentKind expected)
    {
        var result = LaunchIntent.ParseProtocolUri(uri, ProfileIds);
        Assert.False(result.IsRefusal, result.Refusal);
        Assert.Equal(expected, result.Intent?.Kind);
    }

    [Fact]
    public void Protocol_ProfileRoute_KnownId()
    {
        var result = LaunchIntent.ParseProtocolUri("musedesktop://profile/work", ProfileIds);
        Assert.Equal(LaunchIntentKind.SwitchProfile, result.Intent?.Kind);
        Assert.Equal("work", result.Intent?.ProfileId);
    }

    [Fact]
    public void Protocol_ProfileRoute_CaseInsensitiveId()
    {
        // Ids are lowercase slugs; a hand-typed "Work" still finds "work".
        var result = LaunchIntent.ParseProtocolUri("musedesktop://profile/Work", ProfileIds);
        Assert.Equal(LaunchIntentKind.SwitchProfile, result.Intent?.Kind);
        Assert.Equal("work", result.Intent?.ProfileId);
    }

    [Fact]
    public void Protocol_ProfileRoute_UnknownIdShowsWindowWithNote()
    {
        var result = LaunchIntent.ParseProtocolUri("musedesktop://profile/nope", ProfileIds);
        Assert.False(result.IsRefusal);
        Assert.Equal(LaunchIntentKind.ShowWindow, result.Intent?.Kind);
        Assert.NotNull(result.Note);
    }

    [Theory]
    [InlineData("musedesktop://open?url=https://example.com")]   // no arbitrary URLs, ever
    [InlineData("musedesktop://settings#frag")]
    [InlineData("musedesktop://opensesame")]
    [InlineData("musedesktop://settings/extra")]
    [InlineData("musedesktop://profile")]
    [InlineData("musedesktop://profile/")]
    [InlineData("musedesktop://profile/work/extra")]
    [InlineData("musedesktop://user@settings")]
    [InlineData("musedesktop://settings:99/")]
    [InlineData("https://muse.ai/")]
    [InlineData("")]
    [InlineData(null)]
    public void Protocol_Refusals(string? uri)
    {
        var result = LaunchIntent.ParseProtocolUri(uri, ProfileIds);
        Assert.True(result.IsRefusal, $"expected refusal for {uri}");
        Assert.Null(result.Intent);
        Assert.NotNull(result.Refusal);
    }

    [Fact]
    public void Protocol_DescribeForLogStripsQueryAndSecrets()
    {
        var described = LaunchIntent.DescribeForLog(
            "musedesktop://open?url=https://example.com/?token=abc123");
        Assert.Equal("musedesktop://open/", described);
        Assert.DoesNotContain("token", described);
        Assert.DoesNotContain("example.com", described);
    }

    // ──────────────────────── #99/#100 lock routing ────────────────────────

    [Theory]
    [InlineData(LaunchIntentKind.ShowWindow)]
    [InlineData(LaunchIntentKind.GoHome)]
    [InlineData(LaunchIntentKind.OpenSettings)]
    [InlineData(LaunchIntentKind.OpenDownloads)]
    [InlineData(LaunchIntentKind.ShowHelp)]
    [InlineData(LaunchIntentKind.QuickDictate)]
    [InlineData(LaunchIntentKind.SwitchProfile)]
    public void Routing_LockedQueuesEverything(LaunchIntentKind kind)
    {
        var intent = new LaunchIntent(kind, kind == LaunchIntentKind.SwitchProfile ? "work" : null);
        Assert.Equal(IntentDisposition.QueueBehindLock, LaunchIntentRouting.Decide(intent, appLocked: true));
        Assert.Equal(IntentDisposition.ApplyNow, LaunchIntentRouting.Decide(intent, appLocked: false));
    }

    [Fact]
    public void PendingQueue_LastWins_AndTakeClears()
    {
        var queue = new PendingIntentQueue();
        Assert.False(queue.HasQueued);
        Assert.Null(queue.Take());
        queue.Offer(new LaunchIntent(LaunchIntentKind.OpenSettings));
        queue.Offer(new LaunchIntent(LaunchIntentKind.OpenDownloads));
        Assert.True(queue.HasQueued);
        Assert.Equal(LaunchIntentKind.OpenDownloads, queue.Take()?.Kind);
        Assert.False(queue.HasQueued);
        Assert.Null(queue.Take());
    }

    // ──────────────────────── #99 registration policy ────────────────────────

    [Fact]
    public void ProtocolLinks_CommandAndClassify()
    {
        const string exe = @"C:\Users\sean\AppData\Local\Programs\MuseDesktop\MuseDesktop.exe";
        Assert.Equal($"\"{exe}\" \"%1\"", ProtocolLinks.CommandValue(exe));
        Assert.Equal(ProtocolLinkState.RegisteredToThisApp,
            ProtocolLinks.Classify(ProtocolLinks.CommandValue(exe), exe));
        Assert.Equal(ProtocolLinkState.RegisteredToAnotherApp,
            ProtocolLinks.Classify("\"C:\\Other\\Thing.exe\" \"%1\"", exe));
        Assert.Equal(ProtocolLinkState.NotRegistered, ProtocolLinks.Classify(null, exe));
        Assert.Equal(ProtocolLinkState.NotRegistered, ProtocolLinks.Classify("  ", exe));
    }

    [Theory]
    [InlineData(true, ProtocolLinkState.NotRegistered, ProtocolSyncAction.Register)]
    [InlineData(true, ProtocolLinkState.RegisteredToThisApp, ProtocolSyncAction.None)]
    [InlineData(true, ProtocolLinkState.RegisteredToAnotherApp, ProtocolSyncAction.None)]   // never stomp
    [InlineData(false, ProtocolLinkState.RegisteredToThisApp, ProtocolSyncAction.Unregister)]
    [InlineData(false, ProtocolLinkState.NotRegistered, ProtocolSyncAction.None)]
    [InlineData(false, ProtocolLinkState.RegisteredToAnotherApp, ProtocolSyncAction.None)]  // never remove another's
    public void ProtocolLinks_SyncMatrix(bool want, ProtocolLinkState state, ProtocolSyncAction expected) =>
        Assert.Equal(expected, ProtocolLinks.DecideSync(want, state));

    [Fact]
    public void ProtocolLinks_StatusTextNeverClaimsAnothersRegistration()
    {
        var text = ProtocolLinks.StatusText(true, ProtocolLinkState.RegisteredToAnotherApp);
        Assert.Contains("different program", text);
    }

    // ──────────────────────── #96 hotkey policy ────────────────────────

    [Fact]
    public void Hotkey_DefaultIsCtrlAltM()
    {
        var resolution = GlobalHotkey.Resolve(true, null, null);
        Assert.Equal(HotkeyResolutionKind.Ready, resolution.Kind);
        Assert.Equal("Ctrl+Alt+M", resolution.Gesture.Format());
    }

    [Fact]
    public void Hotkey_DisabledStaysDisabled()
    {
        var resolution = GlobalHotkey.Resolve(false, null, null);
        Assert.Equal(HotkeyResolutionKind.Disabled, resolution.Kind);
    }

    [Fact]
    public void Hotkey_CustomGestureRespected()
    {
        var resolution = GlobalHotkey.Resolve(true, "Ctrl+Shift+K", null);
        Assert.Equal(HotkeyResolutionKind.Ready, resolution.Kind);
        Assert.Equal("Ctrl+Shift+K", resolution.Gesture.Format());
    }

    [Theory]
    [InlineData("not-a-gesture")]
    [InlineData("M")]       // no modifier: a global bare M would eat typing everywhere
    [InlineData("Shift+M")] // Shift alone is not enough either
    public void Hotkey_BadStoredTextFallsBackToDefault(string stored)
    {
        var resolution = GlobalHotkey.Resolve(true, stored, null);
        Assert.Equal(HotkeyResolutionKind.Ready, resolution.Kind);
        Assert.Equal("Ctrl+Alt+M", resolution.Gesture.Format());
    }

    [Fact]
    public void Hotkey_ConflictWithFixedGestureBlocks()
    {
        var resolution = GlobalHotkey.Resolve(true, "Ctrl+P", null);
        Assert.Equal(HotkeyResolutionKind.BlockedByConflict, resolution.Kind);
        Assert.Equal("Print", resolution.ConflictOwner);
    }

    [Fact]
    public void Hotkey_ConflictWithInAppDefaultBlocks()
    {
        // Ctrl+Shift+D is Open Downloads' in-app default.
        var resolution = GlobalHotkey.Resolve(true, "Ctrl+Shift+D", null);
        Assert.Equal(HotkeyResolutionKind.BlockedByConflict, resolution.Kind);
        Assert.Equal("Open Downloads", resolution.ConflictOwner);
    }

    [Fact]
    public void Hotkey_ConflictUsesEffectiveInAppGestures()
    {
        // Downloads remapped to Ctrl+Alt+K: the hotkey may not take it,
        // but Downloads' abandoned default (Ctrl+Shift+D) is free.
        var overrides = new Dictionary<string, string>
            { [ShortcutCatalog.DownloadsId] = "Ctrl+Alt+K" };
        var blocked = GlobalHotkey.Resolve(true, "Ctrl+Alt+K", overrides);
        Assert.Equal(HotkeyResolutionKind.BlockedByConflict, blocked.Kind);
        var free = GlobalHotkey.Resolve(true, "Ctrl+Shift+D", overrides);
        Assert.Equal(HotkeyResolutionKind.Ready, free.Kind);
    }

    [Fact]
    public void Hotkey_TrySetGesture_Validates()
    {
        Assert.True(GlobalHotkey.TrySetGesture(null,
            new ShortcutGesture(GestureModifiers.Control | GestureModifiers.Alt, "K"), out var ok));
        Assert.Null(ok);
        Assert.False(GlobalHotkey.TrySetGesture(null,
            new ShortcutGesture(GestureModifiers.None, "K"), out var noMod));
        Assert.Contains("Ctrl or Alt", noMod);
        Assert.False(GlobalHotkey.TrySetGesture(null,
            new ShortcutGesture(GestureModifiers.Control, "P"), out var fixedConflict));
        Assert.Contains("Print", fixedConflict);
        Assert.False(GlobalHotkey.TrySetGesture(null,
            new ShortcutGesture(GestureModifiers.Control, "BogusKey"), out var badKey));
        Assert.NotNull(badKey);
    }

    [Theory]
    // locked outranks everything
    [InlineData(true, false, false, false, HotkeyToggleAction.ShowLockScreen)]
    [InlineData(true, true, true, true, HotkeyToggleAction.ShowLockScreen)]
    // modal: forward only, never hide
    [InlineData(false, true, true, true, HotkeyToggleAction.BringForwardOnly)]
    [InlineData(false, true, false, false, HotkeyToggleAction.BringForwardOnly)]
    // the plain toggle
    [InlineData(false, false, true, true, HotkeyToggleAction.HideToTray)]
    [InlineData(false, false, true, false, HotkeyToggleAction.ShowWindow)]  // visible but not in front → come forward
    [InlineData(false, false, false, false, HotkeyToggleAction.ShowWindow)] // hidden in the tray
    public void Hotkey_ToggleMatrix(bool locked, bool modal, bool visible, bool foreground,
        HotkeyToggleAction expected) =>
        Assert.Equal(expected, GlobalHotkey.DecideToggle(locked, modal, visible, foreground));

    [Fact]
    public void HotkeyNative_ConstantsAndModifiers()
    {
        Assert.Equal(0x0001u, HotkeyNative.ModAlt);
        Assert.Equal(0x0002u, HotkeyNative.ModControl);
        Assert.Equal(0x0004u, HotkeyNative.ModShift);
        Assert.Equal(0x0312, HotkeyNative.WmHotkey);
        var mods = HotkeyNative.ModifiersFor(GlobalHotkey.DefaultGesture);
        Assert.Equal(HotkeyNative.ModControl | HotkeyNative.ModAlt | HotkeyNative.ModNoRepeat, mods);
    }

    [Theory]
    [InlineData("M", 0x4Du)]
    [InlineData("A", 0x41u)]
    [InlineData("D5", 0x35u)]
    [InlineData("NumPad3", 0x63u)]
    [InlineData("F1", 0x70u)]
    [InlineData("F11", 0x7Au)]
    [InlineData("F24", 0x87u)]
    [InlineData("Home", 0x24u)]
    [InlineData("End", 0x23u)]
    [InlineData("Return", 0x0Du)]
    [InlineData("Space", 0x20u)]
    [InlineData("OemComma", 0xBCu)]
    [InlineData("OemQuestion", 0xBFu)]
    [InlineData("OemPipe", 0xDCu)]
    [InlineData("Decimal", 0x6Eu)]
    public void HotkeyNative_VirtualKeys(string key, uint expected)
    {
        Assert.True(HotkeyNative.TryVirtualKey(key, out var vk));
        Assert.Equal(expected, vk);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Escape")]        // not gesture-eligible
    [InlineData("MediaPlayPause")] // not in the allowed set
    [InlineData("F25")]
    public void HotkeyNative_UnknownKeysRefused(string? key) =>
        Assert.False(HotkeyNative.TryVirtualKey(key, out _));

    // ──────────────────────── #97 dictate wording ────────────────────────

    [Fact]
    public void QuickDictate_NotesAreHonestAboutTheHandoff()
    {
        Assert.Contains("voice button", QuickDictateText.Note(microphoneReady: true));
        Assert.Equal(
            "Voice needs the microphone — use Muse's voice button once to grant it, then dictation is one click away.",
            QuickDictateText.Note(microphoneReady: false));
    }

    // ──────────────────────── settings plumbing ────────────────────────

    [Fact]
    public void Settings_S11Fields_RoundTripThroughCloneAndExportImport()
    {
        var settings = new AppSettings
        {
            GlobalHotkeyEnabled = false,
            GlobalHotkeyGesture = "Ctrl+Shift+K",
            RegisterProtocolLinks = false,
        };
        var clone = settings.Clone();
        Assert.False(clone.GlobalHotkeyEnabled);
        Assert.Equal("Ctrl+Shift+K", clone.GlobalHotkeyGesture);
        Assert.False(clone.RegisterProtocolLinks);

        var json = SettingsStore.Export(settings);
        Assert.True(SettingsStore.TryImport(json, out var imported, out _));
        Assert.False(imported!.GlobalHotkeyEnabled);
        Assert.Equal("Ctrl+Shift+K", imported.GlobalHotkeyGesture);
        Assert.False(imported.RegisterProtocolLinks);
    }

    [Theory]
    [InlineData("garbage++", null)]
    [InlineData("M", null)]        // no Ctrl/Alt
    [InlineData("Shift+K", null)]  // Shift alone is not enough
    [InlineData("ctrl+alt+k", "Ctrl+Alt+K")] // canonicalized
    [InlineData("Ctrl+Alt+M", "Ctrl+Alt+M")]
    public void Settings_S11GestureSanitize(string stored, string? expected)
    {
        var settings = new AppSettings { GlobalHotkeyGesture = stored }.Sanitized();
        Assert.Equal(expected, settings.GlobalHotkeyGesture);
    }

    [Fact]
    public void SettingsReset_ResetsS11Preferences()
    {
        var settings = new AppSettings
        {
            GlobalHotkeyEnabled = false,
            GlobalHotkeyGesture = "Ctrl+Shift+K",
            RegisterProtocolLinks = false,
        };
        SettingsReset.ApplyDefaults(settings);
        Assert.True(settings.GlobalHotkeyEnabled);
        Assert.Null(settings.GlobalHotkeyGesture);
        Assert.True(settings.RegisterProtocolLinks);
    }

    [Fact]
    public void SettingsSearch_S11WordsFindTheirSections()
    {
        Assert.Contains(SettingsSearch.Search("global hotkey"), s => s.Id == SettingsSearch.AccessibilityId);
        Assert.Contains(SettingsSearch.Search("protocol"), s => s.Id == SettingsSearch.BehaviorId);
        Assert.Contains(SettingsSearch.Search("musedesktop links"), s => s.Id == SettingsSearch.BehaviorId);
    }

    [Fact]
    public void JsonExport_S11FieldsUseCamelCaseNames()
    {
        var json = SettingsStore.Export(new AppSettings());
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("globalHotkeyEnabled", out _));
        Assert.True(doc.RootElement.TryGetProperty("registerProtocolLinks", out _));
    }
}
