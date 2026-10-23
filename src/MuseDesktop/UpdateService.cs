using System.Net.Http;
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

public sealed record UpdateOutcome(UpdateOutcomeKind Kind, string? Tag = null, string? Url = null);

/// <summary>
/// One quiet request to GitHub's latest-release endpoint. Any failure
/// (offline, rate limit, odd payload) is a log line and an outcome —
/// never an exception — because the app is offline-first and updates
/// are a courtesy.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static async Task<UpdateOutcome> CheckAsync(Version current, DiagnosticLog log)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UpdateInfo.LatestReleaseApiUrl);
            request.Headers.UserAgent.ParseAdd($"MuseDesktop/{current.ToString(3)}");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                log.Warn($"Update check: HTTP {(int)response.StatusCode}");
                return new UpdateOutcome(UpdateOutcomeKind.Unreachable);
            }
            var json = await response.Content.ReadAsStringAsync();
            var latest = UpdateInfo.ParseLatestReleaseJson(json);
            if (latest is null)
                return new UpdateOutcome(UpdateOutcomeKind.BadData);
            if (UpdateInfo.IsNewer(latest.Value.Tag, current))
            {
                log.Info($"Update available: {latest.Value.Tag} (running {current.ToString(3)})");
                return new UpdateOutcome(UpdateOutcomeKind.Available, latest.Value.Tag, latest.Value.Url);
            }
            log.Info($"Update check: up to date ({latest.Value.Tag})");
            return new UpdateOutcome(UpdateOutcomeKind.UpToDate, latest.Value.Tag);
        }
        catch (Exception ex)
        {
            log.Warn($"Update check failed: {ex.Message}");
            return new UpdateOutcome(UpdateOutcomeKind.Failed);
        }
    }
}
