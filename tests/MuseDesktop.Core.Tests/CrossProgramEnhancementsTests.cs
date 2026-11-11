using System.Globalization;
using System.Text.Json;
using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

public class CrossProgramEnhancementsTests
{
    private static string Source(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MuseDesktop.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir?.FullName ?? throw new InvalidOperationException(), file));
    }

    [Theory]
    [InlineData("https://person:secret@muse.ai/")]
    [InlineData("blob:null/private")]
    [InlineData("blob:file:///C:/private.txt")]
    [InlineData("blob:https://person:secret@muse.ai/id")]
    public void E01NavigationRejectsCredentialAndOpaqueBlobAddresses(string url)
    {
        Assert.Equal(LinkDisposition.Block, UrlPolicy.ClassifyNavigation(url));
        Assert.Equal(LinkDisposition.InApp, UrlPolicy.ClassifyNavigation("blob:https://muse.ai/id"));
        Assert.Equal(LinkDisposition.Block, UrlPolicy.ClassifyNewWindow("http://muse.ai/"));
        Assert.Equal(LinkDisposition.Block, UrlPolicy.ClassifyNewWindow("https://me:secret@example.com/"));
    }

    [Theory]
    [InlineData("https://muse.ai:8443")]
    [InlineData("https://me:secret@muse.ai")]
    [InlineData("https://muse.ai.evil.test")]
    public void E02AutomaticPermissionsRequireTheExactOrigin(string origin)
    {
        Assert.False(UrlPolicy.IsAutoGrantedPermission(origin, "microphone"));
        Assert.True(UrlPolicy.IsAutoGrantedPermission("https://muse.ai:443", "microphone"));
    }

    [Theory]
    [InlineData("data:text/plain,PRIVATE")]
    [InlineData("file:///C:/PRIVATE.txt")]
    [InlineData("javascript:PRIVATE")]
    [InlineData("blob:https://muse.ai/PRIVATE?token=SECRET")]
    public void E03OpaqueUrlPayloadsDoNotEnterLogs(string url)
    {
        var redacted = UrlPolicy.RedactForLog(url);
        Assert.DoesNotContain("PRIVATE", redacted);
        Assert.DoesNotContain("SECRET", redacted);
        Assert.Equal("https://muse.ai/chat", UrlPolicy.RedactForLog("https://me:secret@muse.ai/chat?code=SECRET"));
    }

    [Fact]
    public void E04EncryptedExportsRoundTripAndHaveBoundedFinallyClearedBuffers()
    {
        var encrypted = EncryptedExport.Encrypt("{\"note\":\"private\"}", "fixture password");
        Assert.True(EncryptedExport.TryDecrypt(encrypted, "fixture password", out var plain, out var error, out _));
        Assert.Equal("{\"note\":\"private\"}", plain);
        Assert.Equal(EncryptedExportError.None, error);
        Assert.False(EncryptedExport.TryDecrypt(encrypted, "wrong", out _, out error, out _));
        Assert.Equal(EncryptedExportError.WrongPasswordOrAltered, error);
        Assert.False(EncryptedExport.TryDecrypt(null!, "", out _, out error, out _));
        Assert.Equal(EncryptedExportError.NotEncrypted, error);
        var source = Source("src/MuseDesktop.Core/EncryptedExport.cs");
        Assert.Contains("CryptographicOperations.ZeroMemory(key)", source);
        Assert.Contains("CryptographicOperations.ZeroMemory(plainBytes)", source);
        Assert.Contains("Length: > MaxEnvelopeChars", source);
        Assert.Contains("data.Length > MaxPlainBytes", source);
        Assert.Equal(32 * 1024 * 1024, EncryptedExport.MaxPlainBytes);
    }

    [Fact]
    public void E05MalformedReleaseEntriesDoNotHideValidReleasesOrExposeDrafts()
    {
        var parsed = ReleaseSelection.ParseReleasesJson("""
            [null, 7, {"tag_name":"v3.0.0","assets":[null,{"name":"app","browser_download_url":"https://example.com/app","size":"bad"}]},
             {"tag_name":"v99.0.0","draft":true},
             {"tag_name":"v90.0.0","prerelease":"true"},
             {"tag_name":"v4.0.0","draft":false,"assets":[{"name":"app","browser_download_url":"https://example.com/app","size":-1}]}]
            """);
        Assert.Equal(new[] { "v3.0.0", "v4.0.0" }, parsed.Select(p => p.Tag));
        Assert.All(parsed, r => Assert.Equal(0, Assert.Single(r.Assets).Size));
        Assert.Equal("v4.0.0", ReleaseSelection.SelectNewest(parsed, new Version(2, 3, 0), UpdateChannel.Stable)!.Tag);
    }

    [Fact]
    public void E06ConflictingChecksumsNeverUseLastWriterWins()
    {
        var a = new string('a', 64); var b = new string('b', 64);
        Assert.Empty(SumsFile.Parse($"{a}  app.exe\n{b}  app.exe\n{a}  app.exe"));
        Assert.Equal(a, SumsFile.Parse($"{a}  app.exe\n{a.ToUpperInvariant()}  app.exe")["app.exe"]);
    }

    [Fact]
    public void E07AttachmentEnumerationIsOffThreadCancellableAndGenerationGuarded()
    {
        var source = Source("src/MuseDesktop/MainWindow.QuickAttach.cs");
        Assert.Contains("Task.Run(() => RecentFiles.Collect", source);
        Assert.Contains("Dispatcher.InvokeAsync", source);
        Assert.Contains("generation != _quickAttachGeneration", source);
        Assert.Contains("_isLocked || !_settings.QuickAttachStripEnabled", source);
        Assert.Contains("_quickAttachScan?.Cancel()", source);
        Assert.Contains("CancelQuickAttachRefresh()", Source("src/MuseDesktop/MainWindow.xaml.cs"));
    }

    [Fact]
    public void E08AttachmentCollectionKeepsTheCorrectTopKAndHonorsCancellation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "recent-bounded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 100; i++)
            {
                var path = Path.Combine(dir, $"{i:000}.txt");
                File.WriteAllText(path, "fixture");
                File.SetLastWriteTimeUtc(path, start.AddMinutes(i));
            }
            var entries = RecentFiles.Collect(new[] { dir, dir + Path.DirectorySeparatorChar }, 7);
            Assert.Equal(7, entries.Count);
            Assert.Equal("099.txt", entries[0].Name);
            Assert.Equal("093.txt", entries[^1].Name);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => RecentFiles.Collect(new[] { dir }, 7, cts.Token));
            Assert.Same(RecentFiles.AttachableExtensionList, RecentFiles.AttachableExtensionList);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("Ctrl++", "Ctrl+OemPlus")]
    [InlineData("Ctrl+Enter", "Ctrl+Return")]
    [InlineData("Ctrl+1", "Ctrl+D1")]
    [InlineData("Alt+PgUp", "Alt+PageUp")]
    [InlineData("Ctrl+Page Down", "Ctrl+PageDown")]
    [InlineData("Ctrl+Del", "Ctrl+Delete")]
    public void E09FriendlyShortcutNamesRoundTrip(string input, string expected)
    {
        Assert.True(ShortcutGesture.TryParse(input, out var gesture));
        Assert.Equal(expected, gesture.Format());
        Assert.True(ShortcutGesture.TryParse(gesture.DisplayText(), out var roundTrip));
        Assert.Equal(gesture, roundTrip);
        Assert.False(ShortcutGesture.TryParse("Ctrl++A", out _));
    }

    [Fact]
    public void E10FutureLastCheckDoesNotStarveUpdatesAfterAClockCorrection()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(UpdateSchedule.ShouldAutoCheck(now, true, UpdateCheckFrequency.Daily, now.AddDays(30), null));
        Assert.False(UpdateSchedule.ShouldAutoCheck(now, true, UpdateCheckFrequency.ManualOnly, now.AddDays(30), null));
        Assert.False(UpdateSchedule.ShouldAutoCheck(now, true, UpdateCheckFrequency.Daily, now.AddDays(30), now.AddDays(1)));
    }

    [Fact]
    public void E11TransferRateRecoversAfterTheClockMovesBackwards()
    {
        var sampler = new DownloadRateSampler();
        var now = DateTimeOffset.UtcNow;
        sampler.Sample(0, now);
        Assert.True(sampler.Sample(1000, now.AddSeconds(1)) > 0);
        Assert.Equal(0, sampler.Sample(1500, now.AddSeconds(-5)));
        Assert.True(sampler.Sample(2500, now.AddSeconds(-4)) > 0);
        Assert.Equal(0, sampler.Sample(-1, now.AddSeconds(3)));
    }

    [Theory]
    [InlineData("de-DE", "1,5 KB")]
    [InlineData("fr-FR", "1,5 KB")]
    [InlineData("en-US", "1.5 KB")]
    public void E12DownloadNumbersRespectTheLocaleAndNeverShowBrokenSpeeds(string culture, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expected, DownloadText.FormatBytes(1536));
            Assert.Equal("1 KB", DownloadText.FormatBytes(1024));
            Assert.Equal("0 B/s", DownloadText.FormatSpeed(double.PositiveInfinity));
            Assert.DoesNotContain("-", DownloadText.FormatSpeed(double.MaxValue));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void E13LongDownloadEtasUseReadableHoursAndDays()
    {
        Assert.Equal("1 hr 30 min", DownloadText.FormatEta(TimeSpan.FromMinutes(90)));
        Assert.Equal("1 d 1 hr", DownloadText.FormatEta(TimeSpan.FromHours(25)));
        Assert.Equal("45 sec", DownloadText.FormatEta(TimeSpan.FromSeconds(45)));
        var localizer = StringCatalog.CreateLocalizer("en-US", "en-US");
        Assert.Equal(DownloadText.FormatEta(TimeSpan.FromMinutes(90)), localizer.Format("dl.eta_hours", 1, 30));
        Assert.Contains("dl.eta_days", Source("src/MuseDesktop/DownloadLines.cs"));
    }

    [Fact]
    public void E14TextPromptsHaveVisibleLimitsAndKeyboardSafeMultilineEditing()
    {
        var source = Source("src/MuseDesktop/TextPromptWindow.xaml.cs");
        Assert.Contains("InputText.MaxLength = _maxLength", source);
        Assert.Contains("InputText.Text.Length > _maxLength", source);
        Assert.Contains("ui.input_count", source);
        Assert.Contains("ModifierKeys.Control", source);
        Assert.Contains("SetHelpText", source);
        Assert.Contains("ResizeMode=\"CanResizeWithGrip\"", Source("src/MuseDesktop/TextPromptWindow.xaml"));
    }

    [Fact]
    public void E15AnnouncementsMarshalToTheRegisteredUiDispatcher()
    {
        var source = Source("src/MuseDesktop/Announcer.cs");
        Assert.Contains("dispatcher.CheckAccess()", source);
        Assert.Contains("dispatcher.BeginInvoke", source);
        Assert.Contains("HasShutdownStarted", source);
        Assert.True(source.IndexOf("dispatcher.CheckAccess()", StringComparison.Ordinal) <
                    source.IndexOf("Record(kind, text", StringComparison.Ordinal));
    }

    [Fact]
    public void E16DialogFocusIsRestoredInFinallyWithoutFocusingHiddenWindows()
    {
        var source = Source("src/MuseDesktop/FocusReturn.cs");
        Assert.Contains("finally { Restore(invoker); }", source);
        Assert.Contains("IsVisible: true, IsEnabled: true, Focusable: true", source);
        Assert.Contains("MainWindow is { IsVisible: true, IsEnabled: true }", source);
    }

    [Fact]
    public void E17SearchFindsAccentedNamesAndDownloadNotesWithoutChangingIdentity()
    {
        Assert.True(SearchText.Contains("Résumé café", "resume cafe"));
        Assert.True(SearchText.Contains("Cafe\u0301", "cafe"));
        var pin = new PinnedConversation { Title = "Café résumé", Url = "https://muse.ai/chat/x" };
        Assert.Same(pin, Assert.Single(PinnedConversations.Filter(new[] { pin }, "cafe")));
        var download = new DownloadRecord { FileName = "report.pdf", Note = "Résumé de réunion" };
        Assert.Same(download, Assert.Single(DownloadHistoryQuery.Apply(new[] { download }, "resume",
            DownloadStateFilter.All, DownloadHistorySort.NewestFirst)));
    }

    [Fact]
    public void E18AttachmentPreferencesPreserveDefaultsAndSurviveCloning()
    {
        var defaults = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.True(defaults.QuickAttachIncludeScreenshots);
        Assert.Equal(12, defaults.QuickAttachMaxFiles);
        var settings = new AppSettings { QuickAttachIncludeScreenshots = false, QuickAttachMaxFiles = 4 };
        var clone = settings.Clone();
        Assert.False(clone.QuickAttachIncludeScreenshots);
        Assert.Equal(4, clone.QuickAttachMaxFiles);
        settings.QuickAttachMaxFiles = int.MaxValue;
        Assert.Equal(12, settings.Sanitized().QuickAttachMaxFiles);
        Assert.Single(RecentFiles.DefaultSourceFolders(includeScreenshots: false));
        Assert.Contains("QuickAttachOptionsHost", Source("src/MuseDesktop/SettingsWindow.xaml.cs"));
    }

    [Theory]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("#F00", "#FF0000")]
    [InlineData("#000", "#000000")]
    public void E19CustomAccentsAcceptUnambiguousCssShortHex(string value, string expected)
    {
        Assert.True(HexColor.TryNormalize(value, out var hex));
        Assert.Equal(expected, hex);
        Assert.False(HexColor.TryNormalize("##FF0000", out _));
        Assert.False(HexColor.TryNormalize("#abcd", out _));
        Assert.False(HexColor.TryNormalize("bad", out _));
    }

    [Fact]
    public void E20ReadAloudKeepsNestedAndMismatchedCodeFencesSilent()
    {
        var segments = SpeechText.Segment("Before\n\n````text\nSECRET1\n```\nSECRET2\n~~~\nSECRET3\n````\n\nAfter", "Code block.");
        Assert.Equal(new[] { "Before", "Code block.", "After" }, segments.Select(s => s.Text));
        Assert.DoesNotContain(segments, s => s.Text.Contains("SECRET", StringComparison.Ordinal));
    }
}
