using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

/// <summary>
/// Slice 8 (v0.12.0, items 71–80): performance, reliability &amp; recovery —
/// the recovery coordinator's one-recovery-per-incident rule, the
/// watchdog's miss counting, safe mode, startup timings, the memory
/// readout, the cache-cap allowlist (protected folders can never be in
/// a plan), the proxy readout's credential stripping, runtime health,
/// and backup rotation.
/// </summary>
public class S8Tests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    // ───────────────────────── #71/#76 recovery coordinator ─────────────────────────

    [Fact]
    public void Recovery_RendererExitReloadsPageOnce()
    {
        var coordinator = new RecoveryCoordinator();
        Assert.Equal(RecoveryDecision.ReloadPage,
            coordinator.OnProcessFailed(ProcessFailureKind.RenderProcessExited, T0));
        Assert.True(coordinator.PageInFailedState);
        Assert.False(coordinator.AutoRecoveryAvailable);
    }

    [Fact]
    public void Recovery_BrowserExitRecreatesWebView()
    {
        var coordinator = new RecoveryCoordinator();
        Assert.Equal(RecoveryDecision.RecreateWebView,
            coordinator.OnProcessFailed(ProcessFailureKind.BrowserProcessExited, T0));
    }

    [Fact]
    public void Recovery_FrameRendererExitReloadsPage()
    {
        var coordinator = new RecoveryCoordinator();
        Assert.Equal(RecoveryDecision.ReloadPage,
            coordinator.OnProcessFailed(ProcessFailureKind.FrameRenderProcessExited, T0));
    }

    [Fact]
    public void Recovery_SecondFatalFailureWithoutSuccessGivesUp()
    {
        var coordinator = new RecoveryCoordinator();
        coordinator.OnProcessFailed(ProcessFailureKind.RenderProcessExited, T0);
        // The automatic reload happened but the renderer died again
        // before any page loaded — no second automatic attempt.
        Assert.Equal(RecoveryDecision.ShowFailureOverlay,
            coordinator.OnProcessFailed(ProcessFailureKind.RenderProcessExited, T0.AddSeconds(5)));
        // And it stays given up.
        Assert.Equal(RecoveryDecision.ShowFailureOverlay,
            coordinator.OnProcessFailed(ProcessFailureKind.BrowserProcessExited, T0.AddSeconds(9)));
    }

    [Fact]
    public void Recovery_SuccessfulLoadRefillsTheBudget()
    {
        var coordinator = new RecoveryCoordinator();
        coordinator.OnProcessFailed(ProcessFailureKind.RenderProcessExited, T0);
        coordinator.OnPageLoaded();
        Assert.False(coordinator.PageInFailedState);
        Assert.True(coordinator.AutoRecoveryAvailable);
        // A fresh incident an hour later recovers again.
        Assert.Equal(RecoveryDecision.ReloadPage,
            coordinator.OnProcessFailed(ProcessFailureKind.RenderProcessExited, T0.AddHours(1)));
    }

    [Theory]
    [InlineData(ProcessFailureKind.GpuProcessExited)]
    [InlineData(ProcessFailureKind.UtilityProcessExited)]
    [InlineData(ProcessFailureKind.PpapiPluginProcessExited)]
    [InlineData(ProcessFailureKind.SandboxHelperProcessExited)]
    [InlineData(ProcessFailureKind.UnknownProcessExited)]
    [InlineData(ProcessFailureKind.RenderProcessUnresponsive)] // the watchdog (#80) owns this one
    public void Recovery_NonFatalKindsDoNothing(ProcessFailureKind kind)
    {
        var coordinator = new RecoveryCoordinator();
        Assert.Equal(RecoveryDecision.None, coordinator.OnProcessFailed(kind, T0));
        Assert.False(coordinator.PageInFailedState);
        Assert.True(coordinator.AutoRecoveryAvailable);
    }

    [Fact]
    public void Recovery_ConnectivityRetryFiresOncePerFailedLoad()
    {
        var coordinator = new RecoveryCoordinator();
        // Connectivity flapping while the page is fine: nothing.
        Assert.Equal(RecoveryDecision.None, coordinator.OnConnectivityRestored(T0));
        coordinator.OnLoadFailed();
        Assert.True(coordinator.PageInFailedState);
        Assert.Equal(RecoveryDecision.ReloadPage, coordinator.OnConnectivityRestored(T0.AddSeconds(3)));
        // Still failed, network flaps again — the one retry is spent.
        Assert.Equal(RecoveryDecision.None, coordinator.OnConnectivityRestored(T0.AddSeconds(6)));
        // Success closes the incident; a later failure earns a new retry.
        coordinator.OnPageLoaded();
        coordinator.OnLoadFailed();
        Assert.Equal(RecoveryDecision.ReloadPage, coordinator.OnConnectivityRestored(T0.AddMinutes(1)));
    }

    // ───────────────────────── #80 watchdog ─────────────────────────

    [Fact]
    public void Watchdog_TwoConsecutiveMissesShowThePrompt()
    {
        var policy = new WatchdogPolicy();
        Assert.Equal(WatchdogDecision.None, policy.OnPingMissed(T0));
        Assert.False(policy.PromptVisible);
        Assert.Equal(WatchdogDecision.ShowHungPrompt, policy.OnPingMissed(T0.AddSeconds(15)));
        Assert.True(policy.PromptVisible);
        // Further misses don't re-fire the prompt decision.
        Assert.Equal(WatchdogDecision.None, policy.OnPingMissed(T0.AddSeconds(30)));
    }

    [Fact]
    public void Watchdog_ASuccessResetsTheCountAndHidesThePrompt()
    {
        var policy = new WatchdogPolicy();
        policy.OnPingMissed(T0);
        Assert.Equal(WatchdogDecision.None, policy.OnPingSucceeded(T0.AddSeconds(15)));
        Assert.Equal(0, policy.ConsecutiveMisses);
        policy.OnPingMissed(T0.AddSeconds(30));
        policy.OnPingMissed(T0.AddSeconds(45));
        Assert.True(policy.PromptVisible);
        Assert.Equal(WatchdogDecision.HideHungPrompt, policy.OnPingSucceeded(T0.AddSeconds(60)));
        Assert.False(policy.PromptVisible);
    }

    [Fact]
    public void Watchdog_WaitSnoozesForTwoMinutes()
    {
        var policy = new WatchdogPolicy();
        policy.OnPingMissed(T0);
        policy.OnPingMissed(T0.AddSeconds(15));
        Assert.True(policy.PromptVisible);
        policy.Snooze(T0.AddSeconds(20));
        Assert.False(policy.PromptVisible);
        Assert.True(policy.IsSnoozed(T0.AddMinutes(2)));
        // Misses during the snooze are ignored entirely.
        Assert.Equal(WatchdogDecision.None, policy.OnPingMissed(T0.AddMinutes(1)));
        Assert.Equal(0, policy.ConsecutiveMisses);
        // After the snooze, counting starts fresh.
        Assert.False(policy.IsSnoozed(T0.AddSeconds(20).Add(WatchdogPolicy.SnoozeDuration).AddSeconds(1)));
        Assert.Equal(WatchdogDecision.None, policy.OnPingMissed(T0.AddMinutes(3)));
        Assert.Equal(1, policy.ConsecutiveMisses);
    }

    [Fact]
    public void Watchdog_ReloadChosenFullyResets()
    {
        var policy = new WatchdogPolicy();
        policy.OnPingMissed(T0);
        policy.OnPingMissed(T0.AddSeconds(15));
        policy.OnReloadChosen();
        Assert.False(policy.PromptVisible);
        Assert.Equal(0, policy.ConsecutiveMisses);
        Assert.False(policy.IsSnoozed(T0.AddSeconds(16)));
    }

    // ───────────────────────── #72 safe mode ─────────────────────────

    [Fact]
    public void CommandLine_SafeModeFlagParses()
    {
        var options = CommandLine.Parse(new[] { "--safe-mode" });
        Assert.True(options.SafeMode);
        Assert.False(CommandLine.Parse(Array.Empty<string>()).SafeMode);
        // Case-insensitive like every other flag, and combinable.
        var mixed = CommandLine.Parse(new[] { "--SAFE-MODE", "--background" });
        Assert.True(mixed.SafeMode);
        Assert.True(mixed.Background);
    }

    [Fact]
    public void SafeMode_SkipsUpdateCheckAndForcesHome()
    {
        Assert.False(SafeModePolicy.ShouldCheckForUpdates(safeMode: true, settingEnabled: true));
        Assert.True(SafeModePolicy.ShouldCheckForUpdates(safeMode: false, settingEnabled: true));
        Assert.False(SafeModePolicy.ShouldCheckForUpdates(safeMode: false, settingEnabled: false));
        Assert.Equal(UrlPolicy.HomeUrl, SafeModePolicy.StartupUrl(true, "https://muse.ai/chat/abc"));
        Assert.Equal("https://muse.ai/chat/abc", SafeModePolicy.StartupUrl(false, "https://muse.ai/chat/abc"));
    }

    // ───────────────────────── #73 startup timings ─────────────────────────

    [Fact]
    public void StartupClock_RecordsEachMilestoneOnce()
    {
        long now = 0;
        var clock = new StartupClock(() => now, T0);
        now = 120; clock.MarkWindowShown();
        now = 900; clock.MarkWebViewReady();
        now = 2100; clock.MarkFirstNavigation();
        now = 5000; clock.MarkWindowShown(); // first mark wins
        var timings = clock.Snapshot();
        Assert.Equal(T0, timings.StartedUtc);
        Assert.Equal(120, timings.WindowShownMs);
        Assert.Equal(900, timings.WebViewReadyMs);
        Assert.Equal(2100, timings.FirstNavigationMs);
        Assert.Contains("window 120 ms", timings.Describe());
        Assert.Contains("WebView2 ready 900 ms", timings.Describe());
    }

    [Fact]
    public void StartupTimingsStore_KeepsNewestTenAndRoundTrips()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "musedesktop-s8-" + Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(dir.FullName, "startup-timings.json");
            var runs = StartupTimingsStore.Load(path);
            Assert.Empty(runs);
            for (var i = 0; i < 12; i++)
                StartupTimingsStore.Add(runs, new StartupTimings
                {
                    StartedUtc = T0.AddMinutes(i),
                    WindowShownMs = 100 + i,
                });
            Assert.Equal(StartupTimingsStore.MaxKept, runs.Count);
            Assert.Equal(T0.AddMinutes(11), runs[0].StartedUtc); // newest first
            StartupTimingsStore.Save(path, runs);
            var loaded = StartupTimingsStore.Load(path);
            Assert.Equal(runs.Count, loaded.Count);
            Assert.Equal(111, loaded[0].WindowShownMs);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ───────────────────────── #74 memory readout ─────────────────────────

    [Fact]
    public void MemoryReadout_AggregatesWebViewProcesses()
    {
        var snapshot = MemoryReadout.Snapshot(
            appWorkingSetBytes: 50_000_000,
            new[]
            {
                new ProcessMemoryEntry("Browser", 101, 120_000_000),
                new ProcessMemoryEntry("Renderer", 102, 200_000_000),
                new ProcessMemoryEntry("Gpu", 103, -5), // garbage dropped
            },
            T0);
        Assert.Equal(320_000_000, snapshot.WebViewTotalBytes);
        Assert.Equal(2, snapshot.WebViewProcessCount);
        Assert.Equal(370_000_000, snapshot.TotalBytes);
        Assert.Contains("WebView2", snapshot.Describe());
    }

    [Fact]
    public void MemoryReadout_SuspendComparisonNeedsBothHalves()
    {
        var before = new MemorySnapshot(T0, 50_000_000, 300_000_000, 3);
        var after = new MemorySnapshot(T0.AddMinutes(6), 50_000_000, 120_000_000, 3);
        Assert.Null(MemoryReadout.SuspendComparison(before, null));
        Assert.Null(MemoryReadout.SuspendComparison(null, after));
        var text = MemoryReadout.SuspendComparison(before, after);
        Assert.NotNull(text);
        Assert.Contains("before tray suspend", text);
        Assert.Contains("less after resume", text);
    }

    // ───────────────────────── #75 data cap + cache cleanup ─────────────────────────

    [Fact]
    public void DataCaps_SanitizeToTheOfferedSizes()
    {
        Assert.Equal(ProfileDataCaps.Mb500, ProfileDataCaps.Sanitize(ProfileDataCaps.Mb500));
        Assert.Equal(ProfileDataCaps.Gb2, ProfileDataCaps.Sanitize(ProfileDataCaps.Gb2));
        Assert.Equal(ProfileDataCaps.Off, ProfileDataCaps.Sanitize(123_456));
        Assert.Equal(ProfileDataCaps.Off, ProfileDataCaps.Sanitize(-1));
        Assert.Equal("1 GB", ProfileDataCaps.Label(ProfileDataCaps.Gb1));
        Assert.Equal("Off", ProfileDataCaps.Label(0));
    }

    /// <summary>Build a fake WebView2 profile folder: disposable caches plus protected sign-in data, with controlled sizes and ages.</summary>
    private static string BuildFakeProfile(
        string root, long cacheBytes, long gpuCacheBytes, long networkBytes, long localStorageBytes)
    {
        var profile = Path.Combine(root, "Default");
        WriteFile(Path.Combine(profile, "Cache", "Cache_Data", "data_0"), cacheBytes);
        WriteFile(Path.Combine(profile, "GPUCache", "data_0"), gpuCacheBytes);
        WriteFile(Path.Combine(profile, "Network", "Cookies"), networkBytes);
        WriteFile(Path.Combine(profile, "Local Storage", "leveldb", "000003.log"), localStorageBytes);
        WriteFile(Path.Combine(profile, "Code Cache", "js", "index"), 1000);
        // An unknown folder with bulk — never disposable, never touched.
        WriteFile(Path.Combine(profile, "SomethingNew", "big.bin"), 5000);
        return root;

        static void WriteFile(string path, long bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
        }
    }

    [Fact]
    public void CacheCleanup_PlanNeverIncludesProtectedOrUnknownFolders()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "musedesktop-s8-" + Guid.NewGuid().ToString("N")));
        try
        {
            var root = BuildFakeProfile(dir.FullName, 4000, 3000, 9000, 8000);
            // Target 0 = free everything disposable; protected data must
            // still be untouchable even at the most aggressive target.
            var plan = CacheCleanup.BuildPlan(root, targetTotalBytes: 0);
            Assert.Contains(plan.ToDelete, e => e.RelativePath.Contains("Cache"));
            Assert.Contains(plan.ToDelete, e => e.RelativePath.Contains("GPUCache"));
            Assert.DoesNotContain(plan.ToDelete, e => e.RelativePath.Contains("Network"));
            Assert.DoesNotContain(plan.ToDelete, e => e.RelativePath.Contains("Local Storage"));
            Assert.DoesNotContain(plan.ToDelete, e => e.RelativePath.Contains("SomethingNew"));
            Assert.DoesNotContain(plan.Candidates, e => e.RelativePath.Contains("Network"));
            Assert.True(plan.TotalBytes >= 9000 + 8000); // protected data counts toward size, just never toward deletion
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CacheCleanup_DeletesOldestFirstOnlyUntilUnderTarget()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "musedesktop-s8-" + Guid.NewGuid().ToString("N")));
        try
        {
            var root = BuildFakeProfile(dir.FullName, 4000, 3000, 1000, 1000);
            // Age the GPU cache older than the main Cache.
            Directory.SetLastWriteTimeUtc(Path.Combine(root, "Default", "GPUCache"), new DateTime(2026, 1, 1));
            Directory.SetLastWriteTimeUtc(Path.Combine(root, "Default", "Cache"), new DateTime(2026, 6, 1));
            // Total = 4000+3000+1000+1000+1000(code cache)+5000(unknown) = 15000.
            // Target 12000 → over by 3000 → only the oldest cache (GPU, 3000) is picked.
            var plan = CacheCleanup.BuildPlan(root, targetTotalBytes: 12000);
            Assert.Equal(15000, plan.TotalBytes);
            Assert.Single(plan.ToDelete);
            Assert.Contains("GPUCache", plan.ToDelete[0].RelativePath);
            Assert.True(plan.BytesToFree >= plan.OverByBytes);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CacheCleanup_UnderTargetAndMissingFolderAreNoOps()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "musedesktop-s8-" + Guid.NewGuid().ToString("N")));
        try
        {
            var root = BuildFakeProfile(dir.FullName, 100, 100, 100, 100);
            var plan = CacheCleanup.BuildPlan(root, targetTotalBytes: ProfileDataCaps.Gb1);
            Assert.False(plan.IsOverTarget);
            Assert.Empty(plan.ToDelete);

            var missing = CacheCleanup.BuildPlan(Path.Combine(dir.FullName, "nope"), 0);
            Assert.Equal(0, missing.TotalBytes);
            Assert.Empty(missing.ToDelete);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ProfileDataCap_RoundTripsAndSanitizes()
    {
        var settings = new AppSettings();
        settings.Profiles[0].DataCapBytes = 777; // not an offered size
        settings.Sanitized();
        Assert.Equal(ProfileDataCaps.Off, settings.Profiles[0].DataCapBytes);

        settings.Profiles[0].DataCapBytes = ProfileDataCaps.Gb1;
        settings.Profiles[0].LastCacheCleanupFreedBytes = -50;
        var clone = settings.Clone().Sanitized();
        Assert.Equal(ProfileDataCaps.Gb1, clone.Profiles[0].DataCapBytes);
        Assert.Equal(0, clone.Profiles[0].LastCacheCleanupFreedBytes);

        // Copy-settings carries the cap (a setting) but not the stamps.
        settings.Profiles[0].LastCacheCleanupUtc = T0;
        var copy = Profiles.CopySettingsFrom(settings.Profiles[0], "p-copy", "Copy");
        Assert.Equal(ProfileDataCaps.Gb1, copy.DataCapBytes);
        Assert.Null(copy.LastCacheCleanupUtc);
    }

    // ───────────────────────── #77 proxy readout ─────────────────────────

    [Fact]
    public void ProxyInfo_StripsCredentialsAndDescribesStates()
    {
        var info = ProxyInfo.Parse(true, "user:secret@proxy.example.com:8080", null);
        Assert.Equal("proxy.example.com:8080", info.Server);
        Assert.DoesNotContain("secret", info.Describe());

        var perProtocol = ProxyInfo.Parse(true, "http=user:pw@h1:3128;https=h2:3128", null);
        Assert.Equal("http=h1:3128; https=h2:3128", perProtocol.Server);

        Assert.Contains("PAC", ProxyInfo.Parse(false, null, "http://wpad.example.com/proxy.pac").Describe());
        Assert.Contains("directly", ProxyInfo.Parse(false, null, null).Describe());
        Assert.Null(ProxyInfo.SanitizeServer("   "));
    }

    // ───────────────────────── #78 runtime health ─────────────────────────

    [Fact]
    public void RuntimeHealth_ClassifiesVersions()
    {
        var ok = RuntimeHealth.Evaluate("141.0.3537.71", false);
        Assert.Equal(RuntimeHealthStatus.Ok, ok.Status);
        Assert.Equal("141.0.3537.71", ok.Version);

        Assert.Equal(RuntimeHealthStatus.Missing, RuntimeHealth.Evaluate(null, false).Status);
        Assert.Equal(RuntimeHealthStatus.Missing, RuntimeHealth.Evaluate("not-a-version", false).Status);

        var old = RuntimeHealth.Evaluate("109.0.1518.0", false);
        Assert.Equal(RuntimeHealthStatus.Outdated, old.Status);
        Assert.Contains("far out of date", old.Message);

        var fixedFolder = RuntimeHealth.Evaluate("141.0.3537.71", true);
        Assert.Equal(RuntimeHealthStatus.FixedVersionFolderPresent, fixedFolder.Status);
        Assert.Contains("ignored", fixedFolder.Message);
    }

    // ───────────────────────── #79 backup rotation ─────────────────────────

    [Fact]
    public void BackupRotation_NamesParseAndCatalogSorts()
    {
        var name = BackupRotation.FileNameFor(BackupRotation.SettingsKind, T0);
        Assert.StartsWith("auto-settings-", name);
        var parsed = BackupRotation.Parse(name);
        Assert.NotNull(parsed);
        Assert.Equal(BackupRotation.SettingsKind, parsed!.Kind);
        Assert.Equal(T0.Date, parsed.Stamp.Date);

        Assert.Null(BackupRotation.Parse("settings-backup-20261024-120000.json")); // the S5 reset backup is a different family
        Assert.Null(BackupRotation.Parse("settings.json"));
        Assert.Null(BackupRotation.Parse("auto-settings-notadate.json"));
    }

    [Fact]
    public void BackupRotation_PrunesBeyondFivePerKind()
    {
        var names = new List<string>();
        for (var i = 0; i < 7; i++)
            names.Add(BackupRotation.FileNameFor(BackupRotation.SettingsKind, T0.AddDays(i)));
        for (var i = 0; i < 3; i++)
            names.Add(BackupRotation.FileNameFor(BackupRotation.DownloadsKind, T0.AddDays(i)));
        var prune = BackupRotation.PruneCandidates(names, BackupRotation.SettingsKind);
        Assert.Equal(2, prune.Count); // 7 settings backups → keep newest 5
        Assert.Contains(BackupRotation.FileNameFor(BackupRotation.SettingsKind, T0), prune); // the oldest go
        Assert.Empty(BackupRotation.PruneCandidates(names, BackupRotation.DownloadsKind));
    }

    [Fact]
    public void BackupRotation_AtMostOneAutomaticBackupPerDay()
    {
        var names = new List<string> { BackupRotation.FileNameFor(BackupRotation.SettingsKind, T0) };
        Assert.False(BackupRotation.IsBackupDue(names, BackupRotation.SettingsKind, T0.AddHours(3)));
        Assert.True(BackupRotation.IsBackupDue(names, BackupRotation.SettingsKind, T0.AddDays(1)));
        Assert.True(BackupRotation.IsBackupDue(names, BackupRotation.DownloadsKind, T0)); // other kind still due
    }

    // ───────────────────────── diagnostics report ─────────────────────────

    [Fact]
    public void DiagnosticsReport_ComposesEverySection()
    {
        var text = DiagnosticsReport.Compose(new DiagnosticsData
        {
            AppVersion = "0.12.0",
            SafeMode = true,
            SettingsPath = @"C:\cfg\settings.json",
            LogPath = @"C:\cfg\logs\musedesktop.log",
            ProfileDir = @"C:\cfg\WebView2",
            Runtime = RuntimeHealth.Evaluate("141.0.3537.71", false),
            Proxy = ProxyInfo.Parse(false, null, null),
            Memory = new MemorySnapshot(T0, 50_000_000, 300_000_000, 3),
            StartupRuns = new[]
            {
                new StartupTimings { StartedUtc = T0, WindowShownMs = 100, WebViewReadyMs = 800, FirstNavigationMs = 2000 },
            },
        });
        Assert.Contains("SAFE MODE", text);
        Assert.Contains("WebView2 Runtime 141.0.3537.71", text);
        Assert.Contains("No system proxy", text);
        Assert.Contains("window 100 ms", text);
        Assert.Contains(@"C:\cfg\settings.json", text);
    }
}
