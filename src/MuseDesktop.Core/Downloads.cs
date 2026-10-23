using System.Text.Json;

namespace MuseDesktop.Core;

/// <summary>
/// One finished download, remembered so the Downloads window can offer
/// Open / Show-in-folder after the status-bar message has moved on
/// (v0.4.0). Only the file name, path, size, and time are kept — never
/// the URL it came from.
/// </summary>
public sealed class DownloadRecord
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset CompletedUtc { get; set; }
}

/// <summary>
/// The download history: a small JSON list next to the settings file,
/// newest first, capped so it can never grow without bound. Loading a
/// missing or corrupt file yields an empty history — this is a
/// convenience record, not user data worth preserving heroics for.
/// </summary>
public static class DownloadHistoryStore
{
    public const int MaxEntries = 50;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static List<DownloadRecord> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new List<DownloadRecord>();
            var records = JsonSerializer.Deserialize<List<DownloadRecord>>(File.ReadAllText(path), Options)
                          ?? new List<DownloadRecord>();
            records.RemoveAll(r => r is null || string.IsNullOrWhiteSpace(r.FullPath));
            return records
                .OrderByDescending(r => r.CompletedUtc)
                .Take(MaxEntries)
                .ToList();
        }
        catch (Exception)
        {
            return new List<DownloadRecord>();
        }
    }

    public static void Save(string path, List<DownloadRecord> records)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(records.Take(MaxEntries).ToList(), Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Prepend a finished download and persist, keeping the cap.</summary>
    public static void Add(string path, DownloadRecord record)
    {
        var records = Load(path);
        records.Insert(0, record);
        Save(path, records);
    }

    public static void Clear(string path) => Save(path, new List<DownloadRecord>());
}

/// <summary>
/// The exact status-bar wording for downloads, kept in Core so it is
/// unit-tested and identical wherever it is shown (v0.4.0 — the ETA
/// formatter used to live in the main window).
/// </summary>
public static class DownloadText
{
    /// <summary>"45 sec" under a minute, "3 min" at or above it (rounded up).</summary>
    public static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalMinutes >= 1) return $"{(int)Math.Ceiling(eta.TotalMinutes)} min";
        return $"{Math.Max(1, (int)Math.Ceiling(eta.TotalSeconds))} sec";
    }

    /// <summary>Human byte count for the Downloads window: "512 B", "3.2 KB", "1.4 MB", "2.30 GB".</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        if (unit == 0)
            return $"{bytes} B";
        var text = value >= 100 ? value.ToString("0") : value.ToString("0.0").TrimEnd('0').TrimEnd('.');
        return $"{text} {units[unit]}";
    }

    /// <summary>
    /// The status-bar pair (main text, right-aligned detail) for an
    /// in-progress download — byte-for-byte the wording the app has
    /// always used, including the whole-KB detail figures.
    /// </summary>
    public static (string Text, string Right) ProgressStatus(
        string fileName, long bytesReceived, long? totalBytes, DateTimeOffset? estimatedEnd, DateTimeOffset now)
    {
        if (totalBytes is { } total && total > 0)
        {
            var pct = (int)(bytesReceived * 100L / total);
            var etaText = "";
            if (estimatedEnd is { } end && end - now is { } eta && eta > TimeSpan.Zero)
                etaText = $", about {FormatEta(eta)} left";
            return ($"Downloading {fileName} — {pct}%{etaText}",
                    $"{bytesReceived / 1024} KB of {total / 1024} KB");
        }
        return ($"Downloading {fileName}…", $"{bytesReceived / 1024} KB received");
    }
}
