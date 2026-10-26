using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>
/// v0.14.0 Slice 10 (items 89–95): log reading with redaction, the
/// support bundle's contents + settings sanitization, the help
/// catalog (incl. the shipped Help.md drift guard), the guided
/// self-test wording, and the extended diagnostics report.
/// </summary>
public class S10Tests
{
    // ──────────────────────── log reading (#89/#93) ────────────────────────

    private const string LogWithErrors =
        "2026-10-24 10:00:00 [INFO] Started\n" +
        "2026-10-24 10:01:00 [ERROR] First failure https://example.com/x?token=abc123\n" +
        "2026-10-24 10:02:00 [WARN] A warning\n" +
        "2026-10-24 10:03:00 [ERROR] Second failure\n" +
        "2026-10-24 10:04:00 [INFO] Recovered\n";

    [Fact]
    public void TailLines_KeepsOnlyTheLastLines()
    {
        Assert.Equal("c\nd", LogReading.TailLines("a\nb\nc\nd\n", 2));
        Assert.Equal("a\nb", LogReading.TailLines("a\nb", 5));
        Assert.Equal("", LogReading.TailLines("", 5));
        Assert.Equal("", LogReading.TailLines(null, 5));
    }

    [Fact]
    public void RecentErrors_AreChronologicalCappedAndRedacted()
    {
        var errors = LogReading.RecentErrors(LogWithErrors, 5);
        Assert.Equal(2, errors.Count);
        Assert.Contains("First failure", errors[0]);
        Assert.Contains("Second failure", errors[1]);
        // The token-bearing query string never survives.
        Assert.DoesNotContain("token=abc123", errors[0]);
        Assert.DoesNotContain("?token", errors[0]);

        var capped = LogReading.RecentErrors(LogWithErrors, 1);
        Assert.Single(capped);
        Assert.Contains("Second failure", capped[0]);
    }

    [Fact]
    public void LastError_ReturnsTheNewestRedactedErrorOrNull()
    {
        var last = LogReading.LastError(LogWithErrors);
        Assert.NotNull(last);
        Assert.Contains("Second failure", last);
        Assert.Null(LogReading.LastError("2026-10-24 10:00:00 [INFO] All quiet\n"));
        Assert.Null(LogReading.LastError(""));
        Assert.Null(LogReading.LastError(null));
        // An error line carrying a long token comes back redacted.
        var tokenLog = "2026-10-24 10:00:00 [ERROR] Died with key ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnop\n";
        var redacted = LogReading.LastError(tokenLog);
        Assert.NotNull(redacted);
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnop", redacted);
    }

    // ──────────────────────── support bundle (#90) ────────────────────────

    [Fact]
    public void Bundle_Contents_AreTheFixedSet_ScreenshotOnlyWhenIncluded()
    {
        var plain = SupportBundle.Contents(includeScreenshot: false);
        Assert.Equal(
            new[] { "manifest.txt", "diagnostics.txt", "log-redacted.txt", "settings-support.json", "downloads-history.json" },
            plain.Select(f => f.Name).ToArray());
        var withShot = SupportBundle.Contents(includeScreenshot: true);
        Assert.Equal(6, withShot.Count);
        Assert.Equal("screenshot.png", withShot[^1].Name);
        Assert.All(plain.Concat(withShot), f => Assert.False(string.IsNullOrWhiteSpace(f.Description)));
    }

    [Fact]
    public void Bundle_PreviewAndManifest_StateTheExclusions()
    {
        var preview = SupportBundle.PreviewText(includeScreenshot: true);
        Assert.Contains("screenshot.png", preview);
        Assert.Contains("cookies", preview);
        Assert.Contains("Nothing is uploaded anywhere", preview);
        var manifest = SupportBundle.ComposeManifest("0.14.0", new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero), false);
        Assert.Contains("MuseDesktop version: 0.14.0", manifest);
        Assert.Contains("settings-support.json", manifest);
        Assert.Contains("app-lock password record", manifest);
        Assert.DoesNotContain("screenshot.png", manifest);
    }

    [Fact]
    public void Bundle_LogExcerpt_IsTheRedactedTailOnly()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < SupportBundle.LogTailLineCount + 50; i++)
            sb.Append($"2026-10-24 10:00:00 [INFO] line {i}\n");
        sb.Append("2026-10-24 11:00:00 [INFO] tail https://example.com/a?secret=shhh\n");
        var excerpt = SupportBundle.LogExcerpt(sb.ToString());
        Assert.DoesNotContain("line 0\n", excerpt);
        Assert.DoesNotContain("secret=shhh", excerpt);
        Assert.Contains("line " + (SupportBundle.LogTailLineCount + 49), excerpt);
    }

    [Fact]
    public void Bundle_SanitizedSettings_CarryNoLockRecordAndNoFullUrls()
    {
        var settings = new AppSettings
        {
            UpdateChannel = UpdateChannel.IncludePrereleases,
            AppLockEnabled = true,
            AppLockSalt = "AAECAwQFBgcICQoLDA0ODw==",
            AppLockHash = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
            AppLockIterations = 210000,
        };
        settings.Profiles[0].CustomStartUrl = "https://muse.ai/chat/abc123";
        settings.LastUrlByProfile[settings.ActiveProfileId] = "https://muse.ai/chat/xyz789";
        settings = settings.Sanitized();
        Assert.True(AppLock.HasValidRecord(settings));

        var bundle = SupportBundle.SanitizeSettingsForBundle(settings);

        // The original is untouched — the app keeps its lock and pages.
        Assert.True(AppLock.HasValidRecord(settings));
        Assert.Equal("https://muse.ai/chat/xyz789", settings.LastUrlByProfile[settings.ActiveProfileId]);

        // The bundle copy has no lock record at all.
        Assert.False(bundle.AppLockEnabled);
        Assert.Null(bundle.AppLockSalt);
        Assert.Null(bundle.AppLockHash);
        Assert.Equal(0, bundle.AppLockIterations);
        Assert.False(AppLock.HasValidRecord(bundle));
        // …and no full URLs anywhere.
        Assert.Empty(bundle.LastUrlByProfile);
        Assert.Null(bundle.LastUrl);
        Assert.All(bundle.Profiles, p => Assert.Null(p.CustomStartUrl));
        // The bundle's serialized form (which also drops the derived
        // get-only properties the plain serializer emits — a derived
        // startupUrl would otherwise reintroduce a full URL).
        var json = SupportBundle.SettingsJson(settings);
        Assert.DoesNotContain("http://", json);
        Assert.DoesNotContain("https://", json);
        Assert.DoesNotContain("startupUrl", json);
        Assert.DoesNotContain("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=", json);
        Assert.DoesNotContain("AAECAwQFBgcICQoLDA0ODw==", json);
        // Non-sensitive settings survive (support can still see them).
        Assert.Equal(UpdateChannel.IncludePrereleases, bundle.UpdateChannel);
        Assert.Equal("Default", bundle.Profiles[0].Name);
    }

    [Fact]
    public void Bundle_DefaultFileName_IsTimestamped()
    {
        var name = SupportBundle.DefaultFileName(new DateTimeOffset(2026, 10, 24, 20, 31, 0, TimeSpan.Zero));
        Assert.Equal("MuseDesktop-support-20261024-2031.zip", name);
    }

    // ──────────────────────── help catalog (#91) ────────────────────────

    [Fact]
    public void Help_Pages_AreNineUniquePagesWithActionsWired()
    {
        // Seven troubleshooting pages (S10) plus the generated
        // commands page (R1 #109) plus the downloads-organizing
        // page (R4).
        Assert.Equal(10, HelpCatalog.Pages.Count);
        Assert.Equal(10, HelpCatalog.Pages.Select(p => p.Id).Distinct().Count());
        Assert.Equal(10, HelpCatalog.Pages.Select(p => p.Title).Distinct().Count());
        Assert.All(HelpCatalog.Pages, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Keywords));
            if (p.Action == HelpAction.OpenSettings)
            {
                Assert.True(SettingsSearch.IsKnownSection(p.SettingsSectionId));
                Assert.False(string.IsNullOrWhiteSpace(p.ActionLabel));
            }
        });
        Assert.NotNull(HelpCatalog.ById("app-lock-forgotten"));
        Assert.Null(HelpCatalog.ById("nope"));
    }

    [Fact]
    public void Help_Search_MatchesSymptomsAndBlankReturnsAll()
    {
        Assert.Equal(HelpCatalog.Pages.Count, HelpCatalog.Search("").Count);
        Assert.Equal(HelpCatalog.Pages.Count, HelpCatalog.Search(null).Count);
        Assert.Contains(HelpCatalog.Search("microphone"), p => p.Id == "microphone-denied");
        Assert.Contains(HelpCatalog.Search("forgot password"), p => p.Id == "app-lock-forgotten");
        Assert.Contains(HelpCatalog.Search("sign in"), p => p.Id == "sign-in-loop");
        Assert.Empty(HelpCatalog.Search("zanzibar"));
    }

    [Fact]
    public void Help_ExtractBody_ReadsOnlyThatPagesSection()
    {
        var md = "# Help\n\n## First page\n\nBody one.\n\n## Second page\n\nBody two.\nStill two.\n";
        var first = new HelpPage("x", "First page", "", HelpAction.None, null, null);
        var second = new HelpPage("y", "Second page", "", HelpAction.None, null, null);
        Assert.Equal("Body one.", HelpCatalog.ExtractBody(md, first));
        Assert.Equal("Body two.\nStill two.", HelpCatalog.ExtractBody(md, second));
        Assert.Null(HelpCatalog.ExtractBody(md, new HelpPage("z", "Missing", "", HelpAction.None, null, null)));
        Assert.Null(HelpCatalog.ExtractBody("", first));
    }

    [Fact]
    public void Help_ShippedFile_CoversEveryCatalogPageWithNoOrphans()
    {
        // The drift guard (#91): the shipped Help.md carries a section
        // for every catalog page — and nothing else at section level.
        var help = File.ReadAllText(Path.Combine(RepoRoot(), "Help.md"));
        foreach (var page in HelpCatalog.Pages)
        {
            var body = HelpCatalog.ExtractBody(help, page);
            Assert.NotNull(body);
            Assert.True(body!.Length > 100, $"Help page '{page.Id}' body is too thin ({body.Length} chars)");
        }
        var headings = help.Replace("\r\n", "\n").Split('\n')
            .Where(l => l.StartsWith("## ", StringComparison.Ordinal))
            .Select(l => l[3..].Trim())
            .ToList();
        Assert.Equal(
            HelpCatalog.Pages.Select(p => p.Title).OrderBy(t => t, StringComparer.Ordinal),
            headings.OrderBy(t => t, StringComparer.Ordinal));
        // Offline honesty: the only address in the whole file is the
        // GitHub issues link (clearly marked as online in the UI too).
        var urls = System.Text.RegularExpressions.Regex.Matches(help, @"https?://\S+")
            .Select(m => m.Value).Distinct().ToList();
        Assert.All(urls, u => Assert.StartsWith("https://github.com/SeanShaffer88/MuseDesktop", u));
    }

    // ──────────────────────── guided self-tests (#92) ────────────────────────

    [Theory]
    [InlineData("{\"ok\":true,\"peak\":0.5}", MicTestOutcome.HeardSound)]
    [InlineData("{\"ok\":true,\"peak\":0.0}", MicTestOutcome.GrantedButSilent)]
    [InlineData("{\"ok\":false,\"error\":\"NotAllowedError\"}", MicTestOutcome.Denied)]
    [InlineData("{\"ok\":false,\"error\":\"SecurityError\"}", MicTestOutcome.Denied)]
    [InlineData("{\"ok\":false,\"error\":\"NotFoundError\"}", MicTestOutcome.NoDevice)]
    [InlineData("{\"ok\":false,\"error\":\"NotReadableError\"}", MicTestOutcome.DeviceError)]
    [InlineData("not json", MicTestOutcome.DeviceError)]
    [InlineData(null, MicTestOutcome.TimedOut)]
    [InlineData("", MicTestOutcome.TimedOut)]
    public void MicReport_ParsesToOutcomes(string? json, MicTestOutcome expected) =>
        Assert.Equal(expected, GuidedSelfTests.ParseMicReport(json));

    [Fact]
    public void MicLines_ArePlainAndDistinct()
    {
        var lines = Enum.GetValues<MicTestOutcome>().Select(GuidedSelfTests.MicrophoneLine).ToList();
        Assert.All(lines, l => Assert.True(l.Length > 40));
        Assert.Equal(lines.Count, lines.Distinct().Count());
        Assert.Contains("Windows Settings", GuidedSelfTests.MicrophoneLine(MicTestOutcome.Denied));
        Assert.Contains("grants it to muse.ai automatically", GuidedSelfTests.SavedMicLine(null));
        Assert.Contains("\"Allow\"", GuidedSelfTests.SavedMicLine("Allow"));
    }

    [Fact]
    public void NotificationLine_ReportsSuppressionInsteadOfHidingIt()
    {
        var normal = GuidedSelfTests.NotificationLine(NotificationSuppression.None, shown: true);
        Assert.Contains("Test notification sent", normal);
        var quiet = GuidedSelfTests.NotificationLine(NotificationSuppression.QuietHours, shown: true);
        Assert.Contains("because you asked for it", quiet);
        Assert.Contains("quiet hours", quiet);
        var dnd = GuidedSelfTests.NotificationLine(NotificationSuppression.WindowsDoNotDisturb, shown: true);
        Assert.Contains("Do Not Disturb", dnd);
        Assert.Contains("couldn't be shown", GuidedSelfTests.NotificationLine(NotificationSuppression.None, shown: false));
    }

    // ──────────────────────── diagnostics report (#89) ────────────────────────

    [Fact]
    public void DiagnosticsReport_WithS10Facts_RendersEveryNewSection()
    {
        var text = DiagnosticsReport.Compose(new DiagnosticsData
        {
            AppVersion = "0.14.0",
            SettingsPath = @"C:\cfg\settings.json",
            LogPath = @"C:\cfg\logs\musedesktop.log",
            ProfileDir = @"C:\cfg\WebView2",
            Architecture = "X64",
            WindowsVersion = "Microsoft Windows 10.0.26200",
            DotNetVersion = ".NET 10.0.0",
            ActiveProfileName = "Work",
            UpdateChannelLabel = UpdateChannelText.Label(UpdateChannel.IncludePrereleases),
            AppLockOn = true,
            DataSizes = new (string, long?)[] { ("Active profile data", 123_456_789), ("Log file", null) },
            RecentErrors = new[] { "2026-10-24 10:03:00 [ERROR] Second failure" },
            Runtime = RuntimeHealth.Evaluate("141.0.3537.71", false),
        });
        Assert.Contains("Architecture: X64", text);
        Assert.Contains("Windows: Microsoft Windows 10.0.26200", text);
        Assert.Contains(".NET: .NET 10.0.0", text);
        Assert.Contains("Active profile: Work", text);
        Assert.Contains("Update channel: Stable + pre-releases", text);
        Assert.Contains("App lock: on", text);
        Assert.Contains("Data sizes", text);
        Assert.Contains("couldn't be measured", text);
        Assert.Contains("Recent errors", text);
        Assert.Contains("Second failure", text);
    }

    [Fact]
    public void DiagnosticsReport_WithoutS10Facts_KeepsTheS8Shape()
    {
        var text = DiagnosticsReport.Compose(new DiagnosticsData
        {
            AppVersion = "0.14.0",
            SettingsPath = @"C:\cfg\settings.json",
            LogPath = @"C:\cfg\logs\musedesktop.log",
            ProfileDir = @"C:\cfg\WebView2",
        });
        Assert.DoesNotContain("App & PC", text);
        Assert.DoesNotContain("Data sizes", text);
        Assert.DoesNotContain("Recent errors", text);
        // An explicitly empty error list says so plainly.
        var withEmpty = DiagnosticsReport.Compose(new DiagnosticsData
        {
            AppVersion = "0.14.0",
            SettingsPath = @"C:\cfg\settings.json",
            LogPath = @"C:\cfg\logs\musedesktop.log",
            ProfileDir = @"C:\cfg\WebView2",
            RecentErrors = Array.Empty<string>(),
        });
        Assert.Contains("None — the current log has no ERROR lines.", withEmpty);
    }

    [Fact]
    public void SettingsSearch_FindsTheS10Surfaces()
    {
        Assert.Contains(SettingsSearch.Search("microphone test"), s => s.Id == SettingsSearch.PrivacyId);
        Assert.Contains(SettingsSearch.Search("support bundle"), s => s.Id == SettingsSearch.DataId);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(typeof(S10Tests).Assembly.Location)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MuseDesktop.sln"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "MuseDesktop")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }
}
