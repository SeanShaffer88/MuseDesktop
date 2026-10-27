using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>How an update check ended (v0.4.0 — the check used to live inline in the main window).</summary>
public enum UpdateOutcomeKind
{
    Available,
    UpToDate,
    Unreachable,
    BadData,
    Failed,
}

/// <summary>
/// The check outcome. Since v0.13.0 (S9 #81/#88) an Available outcome
/// carries the full selected release — assets included — so the update
/// window can download the right-arch installer without a second call.
/// </summary>
public sealed record UpdateOutcome(
    UpdateOutcomeKind Kind, string? Tag = null, string? Url = null, GitHubRelease? Release = null);

/// <summary>How an in-app update download ended (v0.13.0, S9 #81).</summary>
public enum UpdateDownloadOutcome
{
    /// <summary>Downloaded and hash-verified against the release's SHA256SUMS.txt; ready to install.</summary>
    Verified,

    /// <summary>The release carries no SHA256SUMS.txt (or no entry for the installer): refused — our releases always publish one.</summary>
    SumsMissing,

    /// <summary>The downloaded bytes do not match the published hash: the file was deleted, never launched.</summary>
    HashMismatch,

    /// <summary>The release has no installer for this PC's architecture.</summary>
    NoInstallerForArch,

    /// <summary>Network/IO failure mid-download; the partial file was deleted.</summary>
    Failed,
}

public sealed record UpdateDownloadResult(
    UpdateDownloadOutcome Outcome, string? InstallerPath = null, string? Message = null);

/// <summary>
/// GitHub Releases over one quiet HttpClient. Any failure (offline,
/// rate limit, odd payload) is a log line and an outcome — never an
/// exception — because the app is offline-first and updates are a
/// courtesy. Since v0.13.0 (S9 #88) the check reads the releases LIST
/// (not /latest) and lets Core's channel filter decide what may be
/// offered; asset URLs are used but never logged (asset NAMES are).
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>This process's architecture token for asset names ("x64" / "arm64").</summary>
    public static string ArchToken =>
        UpdateAssets.ArchToken(RuntimeInformation.ProcessArchitecture.ToString());

    public static async Task<UpdateOutcome> CheckAsync(
        Version current, UpdateChannel channel, DiagnosticLog log)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseSelection.ReleasesApiUrl);
            request.Headers.UserAgent.ParseAdd($"MuseDesktop/{current.ToString(3)}");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                log.Warn($"Update check: HTTP {(int)response.StatusCode}");
                return new UpdateOutcome(UpdateOutcomeKind.Unreachable);
            }
            var json = await response.Content.ReadAsStringAsync();
            var releases = ReleaseSelection.ParseReleasesJson(json);
            if (releases.Count == 0)
                return new UpdateOutcome(UpdateOutcomeKind.BadData);
            var selected = ReleaseSelection.SelectNewest(releases, current, channel);
            if (selected is not null)
            {
                log.Info($"Update available: {selected.Tag} (running {current.ToString(3)}, channel {channel})");
                return new UpdateOutcome(UpdateOutcomeKind.Available, selected.Tag, selected.PageUrl, selected);
            }
            var newestTag = releases[0].Tag;
            log.Info($"Update check: up to date (newest release {newestTag}, channel {channel})");
            return new UpdateOutcome(UpdateOutcomeKind.UpToDate, newestTag);
        }
        catch (Exception ex)
        {
            log.Warn($"Update check failed: {ex.Message}");
            return new UpdateOutcome(UpdateOutcomeKind.Failed);
        }
    }

    /// <summary>
    /// Download the release's installer for this architecture into the
    /// updates folder, verify its SHA-256 against the release's
    /// SHA256SUMS.txt, and report. The file is written to a ".partial"
    /// name first and only renamed once verified; every failure path
    /// deletes it — an unverified installer is never left launchable.
    /// Progress reports percent/ETA text built by Core.
    /// </summary>
    public static async Task<UpdateDownloadResult> DownloadAndVerifyAsync(
        GitHubRelease release, string updatesDir, DiagnosticLog log,
        IProgress<string> progress, CancellationToken cancellation = default)
    {
        var installer = UpdateAssets.PickInstaller(release, ArchToken);
        if (installer is null)
        {
            log.Warn($"Update download: release {release.Tag} has no installer for {ArchToken}");
            return new UpdateDownloadResult(UpdateDownloadOutcome.NoInstallerForArch,
                Message: Strings.Format("update.no_installer_for_arch", release.Tag, ArchToken));
        }
        var sumsAsset = release.FindAsset(SumsFile.AssetName);
        if (sumsAsset is null)
        {
            log.Warn($"Update download: release {release.Tag} publishes no {SumsFile.AssetName} — refusing");
            return new UpdateDownloadResult(UpdateDownloadOutcome.SumsMissing,
                Message: Strings.Format("update.no_sums_file", release.Tag, SumsFile.AssetName));
        }

        Directory.CreateDirectory(updatesDir);
        var finalPath = Path.Combine(updatesDir, installer.Name);
        var partialPath = finalPath + ".partial";
        try
        {
            string sumsText;
            using (var sumsRequest = NewRequest(sumsAsset.DownloadUrl))
            {
                sumsText = await DownloadHttp.GetStringAsync(sumsRequest.RequestUri, cancellation);
            }
            var expected = SumsFile.ExpectedHash(sumsText, installer.Name);
            if (expected is null)
            {
                log.Warn($"Update download: {SumsFile.AssetName} has no entry for {installer.Name} — refusing");
                return new UpdateDownloadResult(UpdateDownloadOutcome.SumsMissing,
                    Message: Strings.Format("update.sums_missing_entry", SumsFile.AssetName, installer.Name));
            }

            var sampler = new DownloadRateSampler();
            using (var request = NewRequest(installer.DownloadUrl))
            using (var response = await DownloadHttp.SendAsync(
                       request, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? installer.Size;
                await using var source = await response.Content.ReadAsStreamAsync(cancellation);
                await using var file = new FileStream(
                    partialPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    received += read;
                    progress.Report(UpdateDownloadStatus.Format(
                        received, total > 0 ? total : null,
                        sampler.Sample(received, DateTimeOffset.UtcNow)));
                }
            }

            string actual;
            await using (var verifyStream = File.OpenRead(partialPath))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(verifyStream, cancellation));
            if (!SumsFile.HashesMatch(expected, actual))
            {
                TryDelete(partialPath);
                log.Error($"Update download: SHA-256 mismatch for {installer.Name} — file deleted, never launched");
                return new UpdateDownloadResult(UpdateDownloadOutcome.HashMismatch,
                    Message: Strings.Get("update.hash_mismatch"));
            }
            File.Move(partialPath, finalPath, overwrite: true);
            log.Info($"Update downloaded and verified: {installer.Name} ({release.Tag}, SHA-256 matches the release sums)");
            return new UpdateDownloadResult(UpdateDownloadOutcome.Verified, finalPath);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partialPath);
            log.Info("Update download cancelled by the user");
            return new UpdateDownloadResult(UpdateDownloadOutcome.Failed, Message: Strings.Get("update.download_cancelled"));
        }
        catch (Exception ex)
        {
            TryDelete(partialPath);
            log.Warn($"Update download failed: {ex.Message}");
            return new UpdateDownloadResult(UpdateDownloadOutcome.Failed,
                Message: Strings.Get("update.download_failed"));
        }
    }

    /// <summary>
    /// The rollback rotation (S9 #84), run right before a verified new
    /// installer launches: the previously kept "current" installer
    /// becomes the rollback (previous slot), and the new installer is
    /// copied into the current slot for the NEXT rotation. Records the
    /// change in settings via the Core plan; file moves are best-effort
    /// (a failed rotation never blocks the install the user approved).
    /// </summary>
    public static void PrepareRollback(
        AppSettings settings, string updatesDir, string newVersion,
        string newInstallerPath, DiagnosticLog log)
    {
        try
        {
            var currentDir = Path.Combine(updatesDir, InstallerKeep.CurrentSlot);
            var previousDir = Path.Combine(updatesDir, InstallerKeep.PreviousSlot);
            Directory.CreateDirectory(currentDir);
            Directory.CreateDirectory(previousDir);

            var (rotate, newPrevious) = InstallerKeep.PlanRotation(
                settings.KeptInstaller?.Version, settings.KeptInstaller?.FileName,
                newVersion, DateTimeOffset.UtcNow);
            if (rotate && newPrevious is not null && settings.KeptInstaller is not null)
            {
                var from = Path.Combine(currentDir, settings.KeptInstaller.FileName);
                if (File.Exists(from))
                {
                    // Exactly one rollback is kept: the old previous goes.
                    foreach (var old in Directory.EnumerateFiles(previousDir))
                        TryDelete(old);
                    File.Copy(from, Path.Combine(previousDir, newPrevious.FileName), overwrite: true);
                    settings.PreviousInstaller = newPrevious;
                    log.Info($"Rollback kept: installer for {newPrevious.Version} is now the previous version");
                }
            }

            var newName = Path.GetFileName(newInstallerPath);
            var keptPath = Path.Combine(currentDir, newName);
            if (!string.Equals(newInstallerPath, keptPath, StringComparison.OrdinalIgnoreCase))
                File.Copy(newInstallerPath, keptPath, overwrite: true);
            settings.KeptInstaller = new PreviousInstallerInfo
            {
                Version = newVersion,
                FileName = newName,
                KeptUtc = DateTimeOffset.UtcNow,
            };
        }
        catch (Exception ex)
        {
            log.Warn($"Rollback preparation failed (install continues): {ex.Message}");
        }
    }

    private static HttpRequestMessage NewRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("MuseDesktop");
        return request;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* a leftover partial is inert: it is never launched and the next download overwrites it */ }
    }
}
