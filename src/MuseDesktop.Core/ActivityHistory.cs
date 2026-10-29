using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuseDesktop.Core;

public enum ActivityKind
{
    DownloadComplete,
    TestNotification,
    Reminder,
    HiddenInTray,
}

/// <summary>A privacy-safe activity item: fixed category and wording, timestamp only — never a file name, URL, or page text.</summary>
public sealed record ActivityRecord(ActivityKind Kind, DateTimeOffset AtUtc)
{
    [JsonIgnore]
    public string Category => Kind switch
    {
        ActivityKind.DownloadComplete => "Downloads",
        ActivityKind.TestNotification => "Notifications",
        ActivityKind.Reminder => "Focus",
        ActivityKind.HiddenInTray => "App status",
        _ => "Activity",
    };

    [JsonIgnore]
    public string Message => Kind switch
    {
        ActivityKind.DownloadComplete => "A download finished.",
        ActivityKind.TestNotification => "A test notification was shown.",
        ActivityKind.Reminder => "A focus or session reminder was shown.",
        ActivityKind.HiddenInTray => "MuseDesktop continued running in the notification area.",
        _ => "An app notification was shown.",
    };

    [JsonIgnore]
    public string When => AtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

/// <summary>The last 100 app-owned tray notifications, newest first.</summary>
public sealed class ActivityHistory
{
    public const int MaxEntries = 100;

    public List<ActivityRecord> Entries { get; set; } = new();

    public void Add(ActivityKind kind, DateTimeOffset now)
    {
        if (!Enum.IsDefined(kind))
            return;
        Entries ??= new List<ActivityRecord>();
        Entries.Insert(0, new ActivityRecord(kind, now));
        if (Entries.Count > MaxEntries)
            Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
    }

    public void Sanitize()
    {
        Entries ??= new List<ActivityRecord>();
        Entries.RemoveAll(entry => entry is null || !Enum.IsDefined(entry.Kind) || entry.AtUtc == default);
        Entries = Entries.Take(MaxEntries).ToList();
    }
}

/// <summary>Local, bounded, fail-soft persistence for the activity center.</summary>
public static class ActivityHistoryStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ActivityHistory Load(string path)
    {
        lock (Gate)
            return LoadUnderGate(path);
    }

    public static bool Record(string path, ActivityKind kind, DateTimeOffset now)
    {
        lock (Gate)
        {
            try
            {
                var history = LoadUnderGate(path);
                history.Add(kind, now);
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(history, Options));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static bool Clear(string path)
    {
        lock (Gate)
        {
            try
            {
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new ActivityHistory(), Options));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static ActivityHistory LoadUnderGate(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ActivityHistory();
            var history = JsonSerializer.Deserialize<ActivityHistory>(File.ReadAllText(path), Options)
                          ?? new ActivityHistory();
            history.Sanitize();
            return history;
        }
        catch (Exception)
        {
            return new ActivityHistory();
        }
    }
}
