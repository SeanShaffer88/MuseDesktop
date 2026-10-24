using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>Slice 7 (v0.11.0, items 61–63, 65–70): privacy &amp; security — the Core half. Item #64 (app lock) is held for SEAN's design decision and has no code here.</summary>
public class S7Tests
{
    // ───────────────────────── #61 clear-data plan ─────────────────────────

    [Theory]
    [InlineData(ClearDataChoice.Cookies, true)]
    [InlineData(ClearDataChoice.Cache, false)]
    [InlineData(ClearDataChoice.AllSiteData, true)]
    public void ClearData_SignsOutMatchesCookies(ClearDataChoice choice, bool signsOut) =>
        Assert.Equal(signsOut, ClearDataPlan.SignsOut(choice));

    [Fact]
    public void ClearData_DescriptionsArePlainAndDistinct()
    {
        foreach (var choice in Enum.GetValues<ClearDataChoice>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ClearDataPlan.Title(choice)));
            Assert.Contains(choice == ClearDataChoice.Cache ? "signed out" : "signs out of Muse",
                ClearDataPlan.Describe(choice));
        }
        Assert.Equal(3, Enum.GetValues<ClearDataChoice>()
            .Select(c => ClearDataPlan.Describe(c)).Distinct().Count());
        Assert.Contains("Work", ClearDataPlan.DescribeScope(ClearDataScope.ActiveProfile, "Work", 3));
        Assert.Contains("all 3 profiles", ClearDataPlan.DescribeScope(ClearDataScope.AllProfiles, "Work", 3));
    }

    // ───────────────────────── #67/#62 privacy stats ─────────────────────────

    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PrivacyStats_RecordIncrementsCountsAndSince()
    {
        var stats = new PrivacyStats();
        stats.Record(PrivacyEventCategory.BlockedNavigation, "http", "example.com", T0);
        stats.Record(PrivacyEventCategory.BlockedNavigation, "http", "example.com", T0);
        stats.Record(PrivacyEventCategory.DeniedPermission, "Camera", "muse.ai", T0);
        Assert.Equal(T0, stats.SinceUtc);
        Assert.Equal(2, stats.Total(PrivacyEventCategory.BlockedNavigation));
        Assert.Equal(1, stats.Total(PrivacyEventCategory.DeniedPermission));
        Assert.Equal(3, stats.TotalBlocks);
        var row = stats.Rows().Single(r => r.Category == "blocked-navigation");
        Assert.Equal("http", row.Kind);
        Assert.Equal("example.com", row.Host);
        Assert.Equal(2, row.Count);
    }

    [Fact]
    public void PrivacyStats_RecentDenialsNewestFirstAndCapped()
    {
        var stats = new PrivacyStats();
        for (var i = 0; i < PrivacyStats.MaxRecentDenials + 5; i++)
            stats.Record(PrivacyEventCategory.DeniedPermission, "Camera", "muse.ai", T0.AddMinutes(i));
        Assert.Equal(PrivacyStats.MaxRecentDenials, stats.RecentDenials.Count);
        Assert.Equal(T0.AddMinutes(PrivacyStats.MaxRecentDenials + 4), stats.RecentDenials[0].TimeUtc);
        // Only denials land in the list — blocks don't.
        stats.Record(PrivacyEventCategory.BlockedNewWindow, "file", "", T0);
        Assert.Equal(PrivacyStats.MaxRecentDenials, stats.RecentDenials.Count);
    }

    [Fact]
    public void PrivacyStats_HostsAndKindsAreCleaned()
    {
        var stats = new PrivacyStats();
        // A "host" carrying a path/query is not a host — dropped to scheme-only.
        stats.Record(PrivacyEventCategory.BlockedNavigation, "HTTPS", "evil.com/path?token=abc", T0);
        var row = stats.Rows().Single();
        Assert.Equal("https", row.Kind);
        Assert.Equal("", row.Host);
        Assert.Equal("muse.ai", PrivacyStats.CleanHost("  MUSE.AI "));
        Assert.Equal("", PrivacyStats.CleanHost("user:pass@muse.ai"));
        Assert.Equal("unknown", PrivacyStats.CleanKind("!!!"));
        Assert.Equal("microphone", PrivacyStats.CleanKind("Microphone"));
    }

    [Fact]
    public void PrivacyStats_SanitizeDropsGarbageAndCaps()
    {
        var stats = new PrivacyStats
        {
            Counts = new Dictionary<string, long>
            {
                ["blocked-navigation|http|a.com"] = 5,
                ["blocked-navigation|http"] = 9,            // malformed key
                ["nonsense|http|a.com"] = 9,               // unknown category
                ["denied-permission|camera|b.com"] = -3,   // negative count
                ["denied-permission|Camera|B.COM"] = 2,    // cleans + merges case
            },
            RecentDenials = new List<PermissionDenial>
            {
                new() { Kind = "Camera", Host = "ok.com", TimeUtc = T0 },
                new() { Kind = "Geo", Host = "bad host!", TimeUtc = T0.AddHours(1) },
            },
        };
        stats.Sanitized();
        Assert.Equal(5, stats.Total(PrivacyEventCategory.BlockedNavigation));
        Assert.Equal(2, stats.Total(PrivacyEventCategory.DeniedPermission));
        Assert.Equal(2, stats.RecentDenials.Count);
        Assert.Equal("", stats.RecentDenials[0].Host); // "bad host!" cleaned to scheme-only, still newest first
        Assert.Equal("ok.com", stats.RecentDenials[1].Host);
    }

    [Fact]
    public void PrivacyStats_StoreRoundTripsThroughDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "musedesktop-s7-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "privacy-stats.json");
        try
        {
            var stats = new PrivacyStats();
            stats.Record(PrivacyEventCategory.DeniedPermission, "Microphone", "muse.ai", T0);
            PrivacyStatsStore.Save(path, stats);
            var loaded = PrivacyStatsStore.Load(path);
            Assert.Equal(1, loaded.TotalBlocks);
            Assert.Equal("microphone", loaded.RecentDenials.Single().Kind);
            Assert.Equal(T0, loaded.SinceUtc);
            // A corrupt file loads as empty, never throws.
            File.WriteAllText(path, "{ not json");
            Assert.Equal(0, PrivacyStatsStore.Load(path).TotalBlocks);
            Assert.Equal(0, PrivacyStatsStore.Load(path + ".missing").TotalBlocks);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    // ───────────────────────── #65 encrypted export ─────────────────────────

    [Fact]
    public void EncryptedExport_RoundTrips()
    {
        var settings = new AppSettings();
        settings.Profiles[0].Name = "ZebraUniqueName";
        var json = SettingsStore.Export(settings);
        var encrypted = EncryptedExport.Encrypt(json, "correct horse");
        Assert.True(EncryptedExport.IsEncrypted(encrypted));
        // The payload is ciphertext: a distinctive profile name from the
        // plaintext must not be readable anywhere in the export.
        Assert.DoesNotContain("ZebraUniqueName", encrypted);
        Assert.True(EncryptedExport.TryDecrypt(encrypted, "correct horse",
            out var plain, out var error, out _));
        Assert.Equal(EncryptedExportError.None, error);
        Assert.Equal(json, plain);
        // The decrypted payload is a real settings import.
        Assert.True(SettingsStore.TryImport(plain!, out var imported, out _));
        Assert.NotNull(imported);
    }

    [Fact]
    public void EncryptedExport_WrongPasswordIsDistinctFromDamage()
    {
        var encrypted = EncryptedExport.Encrypt("{\"a\":1}", "right password");
        Assert.False(EncryptedExport.TryDecrypt(encrypted, "wrong password",
            out _, out var wrongError, out var wrongMessage));
        Assert.Equal(EncryptedExportError.WrongPasswordOrAltered, wrongError);
        Assert.False(string.IsNullOrWhiteSpace(wrongMessage));

        // Structurally broken envelope → Damaged, a different error.
        var damaged = EncryptedExport.Magic + "\n{ not an envelope";
        Assert.False(EncryptedExport.TryDecrypt(damaged, "right password",
            out _, out var damagedError, out _));
        Assert.Equal(EncryptedExportError.Damaged, damagedError);

        // Tampered ciphertext (valid envelope, one flipped base64 char
        // in the data) → authentication failure, NOT "damaged".
        var dataAt = encrypted.IndexOf("\"data\":\"", StringComparison.Ordinal) + "\"data\":\"".Length;
        var flip = encrypted[dataAt] == 'A' ? 'B' : 'A';
        var tampered = encrypted[..dataAt] + flip + encrypted[(dataAt + 1)..];
        Assert.False(EncryptedExport.TryDecrypt(tampered, "right password",
            out _, out var tamperedError, out _));
        Assert.Equal(EncryptedExportError.WrongPasswordOrAltered, tamperedError);

        // Wrong field types in the envelope → Damaged.
        var wrongTypes = EncryptedExport.Magic + "\n{\"version\":1,\"salt\":123}";
        Assert.False(EncryptedExport.TryDecrypt(wrongTypes, "right password",
            out _, out var wrongTypesError, out _));
        Assert.Equal(EncryptedExportError.Damaged, wrongTypesError);

        // Plain settings JSON is simply not encrypted.
        Assert.False(EncryptedExport.TryDecrypt("{\"schemaVersion\":1}", "x",
            out _, out var plainError, out _));
        Assert.Equal(EncryptedExportError.NotEncrypted, plainError);
        Assert.False(EncryptedExport.IsEncrypted("{\"schemaVersion\":1}"));
        Assert.False(EncryptedExport.IsEncrypted(null));
        Assert.False(EncryptedExport.IsEncrypted(""));
    }

    [Fact]
    public void EncryptedExport_EachExportUsesFreshSaltAndNonce()
    {
        var a = EncryptedExport.Encrypt("same payload", "pw");
        var b = EncryptedExport.Encrypt("same payload", "pw");
        Assert.NotEqual(a, b);
        Assert.Throws<ArgumentException>(() => EncryptedExport.Encrypt("x", ""));
    }

    // ───────────────────────── #66 redaction audit ─────────────────────────

    [Fact]
    public void RedactionAudit_CleanLogPasses()
    {
        var log =
            "2026-10-24 10:00:00 [INFO] Granted microphone to https://muse.ai/\n" +
            "2026-10-24 10:01:00 [WARN] Blocked navigation: http://example.com/some/page\n" +
            "2026-10-24 10:02:00 [INFO] WebView2 131.0.6778.85 ready; profile C:\\Users\\sean\\AppData\\Local\\MuseDesktop\\WebView2\n";
        var findings = RedactionAudit.Scan(log);
        Assert.Empty(findings);
        Assert.Contains("nothing that looks like", RedactionAudit.Summarize(findings, 3));
    }

    [Fact]
    public void RedactionAudit_FlagsAndRedactsLeaks()
    {
        var log =
            "2026-10-24 10:00:00 [INFO] Navigating to https://muse.ai/chat?token=sekrit123\n" +
            "2026-10-24 10:01:00 [INFO] Contact was sean@example.com about it\n" +
            "2026-10-24 10:02:00 [WARN] api_key: hunter2secret\n";
        var findings = RedactionAudit.Scan(log);
        Assert.Contains(findings, f => f.LineNumber == 1 && f.Category == RedactionAudit.UrlWithQueryCategory);
        Assert.Contains(findings, f => f.LineNumber == 2 && f.Category == RedactionAudit.EmailCategory);
        Assert.Contains(findings, f => f.LineNumber == 3 && f.Category == RedactionAudit.CredentialCategory);
        Assert.Contains("found 3 thing", RedactionAudit.Summarize(findings, 3));

        var redacted = RedactionAudit.Redact(log);
        Assert.DoesNotContain("sekrit123", redacted);
        Assert.DoesNotContain("sean@example.com", redacted);
        Assert.DoesNotContain("hunter2secret", redacted);
        // The designed content (host + path) survives redaction.
        Assert.Contains("https://muse.ai/chat", redacted);
    }

    [Fact]
    public void RedactionAudit_LongTokenOutsideUrlsIsFlagged()
    {
        var log = "2026-10-24 10:00:00 [INFO] session blob ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnop end\n";
        var findings = RedactionAudit.Scan(log);
        Assert.Contains(findings, f => f.Category == RedactionAudit.LongTokenCategory);
        // But a long PATH segment in an ordinary address is not a token.
        var urlLog = "2026-10-24 10:00:00 [INFO] Opened https://muse.ai/chat/abcdefghijklmnopqrstuvwxyz0123456789abcd\n";
        Assert.DoesNotContain(RedactionAudit.Scan(urlLog),
            f => f.Category == RedactionAudit.LongTokenCategory);
    }

    // ───────────────────────── #69/#70 text models ─────────────────────────

    [Fact]
    public void ConnectionInfo_SpeaksPlainly()
    {
        Assert.Contains("Secure connection (HTTPS) to muse.ai", ConnectionInfo.SecurityLine("https://muse.ai/chat/1"));
        Assert.Contains("No page is loaded", ConnectionInfo.SecurityLine(null));
        Assert.Contains("Not secure", ConnectionInfo.SecurityLine("http://example.com/"));
        Assert.Equal("The certificate has expired.", ConnectionInfo.DescribeCertificateError("CertificateExpired"));
        Assert.Contains("could not be verified", ConnectionInfo.DescribeCertificateError("SomethingElse"));
        Assert.Contains("certificate", ConnectionInfo.CertificateErrorExplainer);
    }

    [Fact]
    public void BackupGuide_CoversSignInsSettingsAndTheDpapiLimit()
    {
        var text = BackupGuide.Compose(@"C:\Users\sean\AppData\Roaming\MuseDesktop",
                                       @"C:\Users\sean\AppData\Local\MuseDesktop");
        Assert.Contains(@"C:\Users\sean\AppData\Local\MuseDesktop", text);
        Assert.Contains("Export settings", text);
        Assert.Contains("DPAPI", text);
        Assert.Contains("different PC", text);
        Assert.Contains("sign in again", text);
    }

    // ───────────────────────── settings plumbing (S7 fields) ─────────────────────────

    [Fact]
    public void Settings_S7FieldsRoundTripThroughCloneAndExportImport()
    {
        var settings = new AppSettings
        {
            PasswordAutosaveEnabled = false,
            GeneralAutofillEnabled = false,
        };
        var clone = settings.Clone();
        Assert.False(clone.PasswordAutosaveEnabled);
        Assert.False(clone.GeneralAutofillEnabled);

        var json = SettingsStore.Export(settings);
        Assert.True(SettingsStore.TryImport(json, out var imported, out _));
        Assert.False(imported!.PasswordAutosaveEnabled);
        Assert.False(imported.GeneralAutofillEnabled);

        // Defaults stay at the WebView2 defaults (both on).
        Assert.True(new AppSettings().PasswordAutosaveEnabled);
        Assert.True(new AppSettings().GeneralAutofillEnabled);
    }

    [Fact]
    public void SettingsReset_RestoresAutofillDefaultsAndKeepsIdentity()
    {
        var settings = new AppSettings
        {
            PasswordAutosaveEnabled = false,
            GeneralAutofillEnabled = false,
            ActiveProfileId = Profiles.DefaultId,
        };
        SettingsReset.ApplyDefaults(settings);
        Assert.True(settings.PasswordAutosaveEnabled);
        Assert.True(settings.GeneralAutofillEnabled);
    }

    [Theory]
    [InlineData("privacy", SettingsSearch.PrivacyId)]
    [InlineData("permissions", SettingsSearch.PrivacyId)]
    [InlineData("microphone", SettingsSearch.PrivacyId)]
    [InlineData("cookies", SettingsSearch.PrivacyId)]
    [InlineData("certificate", SettingsSearch.PrivacyId)]
    [InlineData("reset", SettingsSearch.DataId)]
    [InlineData("export", SettingsSearch.DataId)]
    public void SettingsSearch_S7QueriesFindPrivacy(string query, string expectedSection)
    {
        var matches = SettingsSearch.Search(query);
        Assert.Contains(matches, m => m.Id == expectedSection);
    }

    [Fact]
    public void SettingsSearch_PrivacySectionIsKnown()
    {
        Assert.True(SettingsSearch.IsKnownSection(SettingsSearch.PrivacyId));
        Assert.Contains(SettingsSearch.Sections, s => s.Id == SettingsSearch.PrivacyId);
    }
}
