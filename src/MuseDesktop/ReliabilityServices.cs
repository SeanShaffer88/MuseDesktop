using System.Diagnostics;
using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// Reads the current user's WinINET proxy settings (S8 #77) for the
/// Diagnostics readout. Registry only — host names and ports, and
/// Core's parser strips any userinfo before the value is displayed or
/// logged, so credentials can never reach either surface.
/// </summary>
public static class ProxyReader
{
    public static ProxyInfo Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var enabled = key?.GetValue("ProxyEnable") is int on && on != 0;
            var server = key?.GetValue("ProxyServer") as string;
            var pac = key?.GetValue("AutoConfigURL") as string;
            return ProxyInfo.Parse(enabled, server, pac);
        }
        catch
        {
            return ProxyInfo.Parse(false, null, null);
        }
    }
}

/// <summary>
/// The startup WebView2 runtime health check (S8 #78): the Evergreen
/// runtime's version (the same static probe the missing-runtime path
/// uses) plus a scan for a Fixed-Version runtime folder beside the
/// exe. The classification itself is Core's <see cref="RuntimeHealth"/>.
/// </summary>
public static class RuntimeHealthCheck
{
    public static RuntimeHealthResult EvaluateCurrent()
    {
        string? version = null;
        try { version = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { /* no runtime installed — Evaluate reports Missing */ }
        var fixedPresent = false;
        try
        {
            fixedPresent = Directory.GetDirectories(AppContext.BaseDirectory)
                .Any(d => Path.GetFileName(d).Contains("FixedVersionRuntime", StringComparison.OrdinalIgnoreCase));
        }
        catch { /* an unreadable exe folder just loses the note */ }
        return RuntimeHealth.Evaluate(version, fixedPresent);
    }
}

/// <summary>
/// Automatic rotating backups (S8 #79): copies of settings.json and
/// downloads.json in the backups folder, written by the automatic path
/// at most once a day (checked on settings saves and at startup) and
/// on demand from Settings. Rotation itself — names, due rule,
/// pruning — is Core's <see cref="BackupRotation"/>.
/// </summary>
public static class BackupService
{
    /// <summary>Write today's automatic backup pair if either kind is due. Cheap when not due (a directory listing). Never throws.</summary>
    public static void MaybeAutoBackup(DiagnosticLog log)
    {
        try
        {
            var names = ExistingNames();
            var now = DateTimeOffset.Now;
            if (!BackupRotation.IsBackupDue(names, BackupRotation.SettingsKind, now)
                && !BackupRotation.IsBackupDue(names, BackupRotation.DownloadsKind, now))
                return;
            CreateBackup(log, automatic: true);
        }
        catch (Exception ex)
        {
            log.Warn($"Automatic backup check failed: {ex.Message}");
        }
    }

    /// <summary>Create a backup pair now and prune each kind to the newest five. Returns the file names written.</summary>
    public static IReadOnlyList<string> CreateBackup(DiagnosticLog log, bool automatic)
    {
        var written = new List<string>();
        try
        {
            Directory.CreateDirectory(AppPaths.BackupsDir);
            var now = DateTimeOffset.Now;
            WriteOne(AppPaths.SettingsPath, BackupRotation.SettingsKind, now, written);
            WriteOne(AppPaths.DownloadsHistoryPath, BackupRotation.DownloadsKind, now, written);
            var names = ExistingNames();
            foreach (var kind in new[] { BackupRotation.SettingsKind, BackupRotation.DownloadsKind })
            {
                foreach (var stale in BackupRotation.PruneCandidates(names, kind))
                {
                    try { File.Delete(Path.Combine(AppPaths.BackupsDir, stale)); }
                    catch (Exception ex) { log.Warn($"Pruning old backup {stale} failed: {ex.Message}"); }
                }
            }
            if (written.Count > 0)
                log.Info($"{(automatic ? "Automatic" : "Manual")} backup written: {string.Join(", ", written)}");
        }
        catch (Exception ex)
        {
            log.Warn($"Backup failed: {ex.Message}");
        }
        return written;
    }

    private static void WriteOne(string sourcePath, string kind, DateTimeOffset now, List<string> written)
    {
        if (!File.Exists(sourcePath))
            return;
        var name = BackupRotation.FileNameFor(kind, now);
        var target = Path.Combine(AppPaths.BackupsDir, name);
        // Two backups in the same second (auto + manual) get a suffix
        // rather than an overwrite.
        var suffix = 2;
        while (File.Exists(target))
        {
            name = BackupRotation.FileNameFor(kind, now)[..^".json".Length] + $"-{suffix}.json";
            target = Path.Combine(AppPaths.BackupsDir, name);
            suffix++;
        }
        File.Copy(sourcePath, target);
        written.Add(name);
    }

    public static IReadOnlyList<string> ExistingNames()
    {
        try
        {
            return Directory.Exists(AppPaths.BackupsDir)
                ? Directory.GetFiles(AppPaths.BackupsDir).Select(Path.GetFileName).Cast<string>().ToList()
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>The outcome of one cache-cleanup run (S8 #75), for the log and the caller's note.</summary>
public sealed record CacheCleanupOutcome(CacheCleanupPlan Plan, long FreedBytes, int DeletedCount, int FailedCount);

/// <summary>
/// Executes <see cref="CacheCleanup"/> plans (S8 #75). Deletion is
/// deliberately boring: only the exact folders the Core plan named
/// (allowlisted cache names, never inside a protected subtree), each
/// delete individually guarded — a locked file in the running
/// profile's cache just leaves that folder for next time.
/// </summary>
public static class CacheCleanupRunner
{
    /// <summary>Run the cap-driven cleanup for one profile directory.</summary>
    public static CacheCleanupOutcome Run(string profileDir, long capBytes, DiagnosticLog log)
    {
        var plan = CacheCleanup.BuildPlan(profileDir, capBytes);
        return Execute(plan, log);
    }

    /// <summary>Run the manual cleanup (every disposable cache folder) for one profile directory.</summary>
    public static CacheCleanupOutcome RunManual(string profileDir, DiagnosticLog log)
    {
        var plan = CacheCleanup.BuildPlan(profileDir, targetTotalBytes: 0);
        return Execute(plan, log);
    }

    private static CacheCleanupOutcome Execute(CacheCleanupPlan plan, DiagnosticLog log)
    {
        long freed = 0;
        var deleted = 0;
        var failed = 0;
        foreach (var entry in plan.ToDelete)
        {
            var full = Path.Combine(plan.ProfileDir, entry.RelativePath);
            try
            {
                if (Directory.Exists(full))
                    Directory.Delete(full, recursive: true);
                freed += entry.Bytes;
                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                log.Warn($"Cache cleanup couldn't delete {entry.RelativePath}: {ex.Message}");
            }
        }
        if (deleted > 0)
            log.Info($"Cache cleanup freed {DownloadText.FormatBytes(freed)} from {deleted} cache folder(s) in {plan.ProfileDir}" +
                     (failed > 0 ? $" ({failed} folder(s) couldn't be deleted — likely in use)" : ""));
        return new CacheCleanupOutcome(plan, freed, deleted, failed);
    }

    /// <summary>
    /// Startup pass (S8 #75): every profile with a cap whose folder is
    /// over it gets cleaned, and its cleanup stamp updated. Returns
    /// true when any stamp changed (the caller then saves settings).
    /// </summary>
    public static bool RunAllProfiles(AppSettings settings, DiagnosticLog log)
    {
        var changed = false;
        foreach (var profile in settings.Profiles)
        {
            if (profile.DataCapBytes <= 0)
                continue;
            try
            {
                var dir = AppPaths.ProfileDir(profile.Id);
                var plan = CacheCleanup.BuildPlan(dir, profile.DataCapBytes);
                if (!plan.IsOverTarget)
                    continue;
                var outcome = Execute(plan, log);
                profile.LastCacheCleanupUtc = DateTimeOffset.UtcNow;
                profile.LastCacheCleanupFreedBytes = outcome.FreedBytes;
                changed = true;
                log.Info($"Profile {profile.Id} was over its {ProfileDataCaps.Label(profile.DataCapBytes)} data cap " +
                         $"({DownloadText.FormatBytes(plan.TotalBytes)}); cache cleanup freed {DownloadText.FormatBytes(outcome.FreedBytes)}. " +
                         "Only disposable cache folders were deleted — sign-in data is never part of a cleanup plan.");
            }
            catch (Exception ex)
            {
                log.Warn($"Cache cleanup for profile {profile.Id} failed: {ex.Message}");
            }
        }
        return changed;
    }
}

/// <summary>
/// Current memory figures (S8 #74): the app's own working set plus the
/// WebView2 environment's process list (browser, renderers, GPU…) with
/// each PID's working set read from the OS. Aggregation is Core's;
/// processes that exit mid-read are skipped.
/// </summary>
public static class MemoryCollector
{
    public static MemorySnapshot? Collect(CoreWebView2Environment? environment)
    {
        try
        {
            long own = 0;
            try { own = Process.GetCurrentProcess().WorkingSet64; }
            catch { /* the total just loses the app's own share */ }
            var entries = new List<ProcessMemoryEntry>();
            if (environment is not null)
            {
                foreach (var info in environment.GetProcessInfos())
                {
                    try
                    {
                        using var process = Process.GetProcessById(info.ProcessId);
                        entries.Add(new ProcessMemoryEntry(
                            info.Kind.ToString(), info.ProcessId, process.WorkingSet64));
                    }
                    catch { /* exited between enumeration and read */ }
                }
            }
            return MemoryReadout.Snapshot(own, entries, DateTimeOffset.UtcNow);
        }
        catch
        {
            return null;
        }
    }
}
