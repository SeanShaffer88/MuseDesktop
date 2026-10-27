using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The download progress/detail lines in the active shell language
/// (R7 #161): the localized twins of Core's DownloadText composers
/// (ProgressStatus, AggregateStatus, ActiveDetail) and FormatEta.
/// Same inputs, same math, same byte figures (Core's FormatBytes /
/// FormatSpeed keep the numbers — units are the same abbreviations
/// in Spanish); only the sentence wording moves to the catalog.
/// R7Tests pin the catalog texts against the Core lines for
/// representative states, so the two can never drift apart silently.
/// </summary>
public static class DownloadLines
{
    /// <summary>"45 sec" under a minute, "3 min" at or above it (rounded up) — Core: DownloadText.FormatEta.</summary>
    public static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalMinutes >= 1)
            return Strings.Format("dl.eta_min", (int)Math.Ceiling(eta.TotalMinutes));
        return Strings.Format("dl.eta_sec", Math.Max(1, (int)Math.Ceiling(eta.TotalSeconds)));
    }

    /// <summary>The status-bar pair for one in-progress download (Core: DownloadText.ProgressStatus).</summary>
    public static (string Text, string Right) ProgressStatus(
        string fileName, long bytesReceived, long? totalBytes, DateTimeOffset? estimatedEnd, DateTimeOffset now,
        double speedBytesPerSecond)
    {
        string text, right;
        if (totalBytes is { } total && total > 0)
        {
            var pct = (int)(bytesReceived * 100L / total);
            var etaText = "";
            if (estimatedEnd is { } end && end - now is { } eta && eta > TimeSpan.Zero)
                etaText = Strings.Format("dl.eta_suffix", FormatEta(eta));
            text = Strings.Format("dl.progress", fileName, pct) + etaText;
            right = Strings.Format("dl.progress_right", bytesReceived / 1024, total / 1024);
        }
        else
        {
            text = Strings.Format("dl.progress_unknown", fileName);
            right = Strings.Format("dl.progress_right_received", bytesReceived / 1024);
        }
        if (speedBytesPerSecond > 0)
            right = $"{right} · {DownloadText.FormatSpeed(speedBytesPerSecond)}";
        return (text, right);
    }

    /// <summary>The status-bar pair when several downloads run at once (Core: DownloadText.AggregateStatus).</summary>
    public static (string Text, string Right) AggregateStatus(ActiveDownloadSet set, DateTimeOffset now)
    {
        var active = set.Items
            .Where(d => d.State is DownloadActivityState.InProgress or DownloadActivityState.Paused)
            .ToList();
        var count = active.Count;
        if (count == 0)
            return ("", "");

        string text;
        if (!set.AnyTransferring)
            text = count == 1
                ? Strings.Get("dl.agg_paused_one")
                : Strings.Format("dl.agg_paused_many", count);
        else if (set.OverallFraction is { } fraction)
            text = Strings.Format("dl.agg_downloading_pct", count, (int)(fraction * 100));
        else
            text = Strings.Format("dl.agg_downloading", count);

        var parts = new List<string>();
        long bytes = active.Sum(d => d.BytesReceived);
        long total = active.Sum(d => d.TotalBytes ?? 0);
        if (total > 0)
            parts.Add(Strings.Format("dl.of_total", DownloadText.FormatBytes(bytes), DownloadText.FormatBytes(total)));
        else if (bytes > 0)
            parts.Add(Strings.Format("dl.received", DownloadText.FormatBytes(bytes)));
        if (set.AnyTransferring && set.TotalSpeed > 0)
            parts.Add(DownloadText.FormatSpeed(set.TotalSpeed));
        var eta = active
            .Where(d => d.State == DownloadActivityState.InProgress && d.EstimatedEnd is not null)
            .Select(d => d.EstimatedEnd!.Value - now)
            .Where(t => t > TimeSpan.Zero)
            .OrderBy(t => t)
            .FirstOrDefault();
        if (eta > TimeSpan.Zero)
            parts.Add(Strings.Format("dl.eta_about", FormatEta(eta)));
        return (text, string.Join(" · ", parts));
    }

    /// <summary>One-line detail for a row in the downloads manager (Core: DownloadText.ActiveDetail).</summary>
    public static string ActiveDetail(ActiveDownload download, DateTimeOffset now)
    {
        switch (download.State)
        {
            case DownloadActivityState.Paused:
                return download.Percent is { } pausedPct
                    ? Strings.Format("dl.detail_paused_pct", pausedPct,
                        DownloadText.FormatBytes(download.BytesReceived),
                        DownloadText.FormatBytes(download.TotalBytes ?? 0))
                    : Strings.Format("dl.detail_paused",
                        DownloadText.FormatBytes(download.BytesReceived));
            case DownloadActivityState.Failed:
                return Strings.Get("dl.detail_failed");
            case DownloadActivityState.Cancelled:
                return Strings.Get("dl.detail_cancelled");
            case DownloadActivityState.Completed:
                return Strings.Get("dl.detail_complete");
            default:
                var parts = new List<string>();
                if (download.Percent is { } pct)
                    parts.Add(Strings.Format("dl.detail_pct", pct));
                parts.Add(download.TotalBytes is { } total && total > 0
                    ? Strings.Format("dl.of_total", DownloadText.FormatBytes(download.BytesReceived),
                        DownloadText.FormatBytes(total))
                    : Strings.Format("dl.received", DownloadText.FormatBytes(download.BytesReceived)));
                if (download.EstimatedEnd is { } end && end - now is { } eta && eta > TimeSpan.Zero)
                    parts.Add(Strings.Format("dl.eta_about", FormatEta(eta)));
                if (download.SpeedBytesPerSecond > 0)
                    parts.Add(DownloadText.FormatSpeed(download.SpeedBytesPerSecond));
                return string.Join(" · ", parts);
        }
    }
}
