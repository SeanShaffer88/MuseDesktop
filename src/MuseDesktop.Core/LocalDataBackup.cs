using System.Text.Json;

namespace MuseDesktop.Core;

/// <summary>A validated local-data backup ready for the Settings window's staged import.</summary>
public sealed class LocalDataBackupContents
{
    internal LocalDataBackupContents(
        AppSettings settings,
        IReadOnlyDictionary<string, string?> stores,
        DateTimeOffset exportedUtc)
    {
        Settings = settings;
        Stores = stores;
        ExportedUtc = exportedUtc;
    }

    public AppSettings Settings { get; }
    public IReadOnlyDictionary<string, string?> Stores { get; }
    public DateTimeOffset ExportedUtc { get; }
}

/// <summary>
/// Portable backup of settings and the app's structured local records.
/// The fixed store list intentionally excludes WebView2 profile data,
/// downloaded file contents, logs, update installers, and transient
/// session state. Store names are allowlisted; imported names never
/// become filesystem paths.
/// </summary>
public static class LocalDataBackup
{
    public const int FormatVersion = 1;
    public const int MaxCharacters = 64_000_000;

    private static readonly (string Key, string Label)[] StoreCatalog =
    {
        ("downloads", "Download history (names, local paths, notes, and verification results)"),
        ("readingList", "Reading lists"),
        ("notes", "Quick notes"),
        ("weeklyStats", "Weekly productivity counts"),
        ("bookmarks", "Message bookmarks and excerpts"),
        ("drafts", "Unsent conversation drafts"),
        ("snippets", "Prompt snippets"),
        ("privacyStats", "Privacy counters and recent denials"),
        ("profileUsage", "Profile launch counts"),
        ("paletteHistory", "Command-palette recents"),
        ("activityHistory", "Recent generic app notifications"),
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Capture the settings shown in the dialog and the known local JSON stores.</summary>
    public static string Export(AppSettings settings, DateTimeOffset now) =>
        CreateJson(settings, CaptureStores(), now);

    /// <summary>Build a bundle from explicit data — also the small test seam for its versioned format.</summary>
    public static string CreateJson(
        AppSettings settings,
        IReadOnlyDictionary<string, string?> stores,
        DateTimeOffset now)
    {
        if (stores.Keys.Any(key => !StoreCatalog.Any(store => store.Key == key)))
            throw new ArgumentException("The backup contains an unknown local store.", nameof(stores));

        var completeStores = StoreCatalog.ToDictionary(
            store => store.Key,
            store => stores.TryGetValue(store.Key, out var contents) ? contents : null,
            StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(new Envelope
        {
            Version = FormatVersion,
            ExportedUtc = now,
            SettingsJson = SettingsStore.Export(settings),
            Stores = completeStores,
        }, Options);
        if (json.Length > MaxCharacters)
            throw new InvalidDataException("The local-data backup is larger than MuseDesktop can import.");
        return json;
    }

    /// <summary>Validate a bundle without touching local files; safe to call before showing an import preview.</summary>
    public static bool TryImport(string? json, out LocalDataBackupContents? contents, out string? error)
    {
        contents = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxCharacters)
        {
            error = "That backup is empty or larger than MuseDesktop can import.";
            return false;
        }

        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(json, Options);
        }
        catch (Exception)
        {
            error = "That file isn't a readable MuseDesktop data backup.";
            return false;
        }

        if (envelope is null || envelope.Version != FormatVersion || envelope.Stores is null
            || envelope.Stores.Count != StoreCatalog.Length
            || StoreCatalog.Any(store => !envelope.Stores.ContainsKey(store.Key))
            || envelope.Stores.Keys.Any(key => !StoreCatalog.Any(store => store.Key == key)))
        {
            error = "That backup is incomplete or uses an unsupported format.";
            return false;
        }

        if (!SettingsStore.TryImport(envelope.SettingsJson ?? "", out var settings, out var settingsError)
            || settings is null)
        {
            error = $"The settings in that backup couldn't be validated: {settingsError}";
            return false;
        }

        long totalCharacters = envelope.SettingsJson?.Length ?? 0;
        foreach (var (key, value) in envelope.Stores)
        {
            if (value is null)
                continue;
            totalCharacters += value.Length;
            if (totalCharacters > MaxCharacters)
            {
                error = "That backup is larger than MuseDesktop can import.";
                return false;
            }
            if (key == "downloads")
            {
                if (!DownloadHistoryStore.TryParseJson(value, out _, out var downloadError))
                {
                    error = downloadError ?? "The download history in that backup is damaged.";
                    return false;
                }
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(value);
                var expectedKind = key == "paletteHistory" ? JsonValueKind.Array : JsonValueKind.Object;
                if (document.RootElement.ValueKind != expectedKind)
                    throw new JsonException();
            }
            catch (Exception)
            {
                error = $"The {StoreCatalog.First(store => store.Key == key).Label.ToLowerInvariant()} data in that backup is damaged.";
                return false;
            }
        }

        contents = new LocalDataBackupContents(
            settings,
            new Dictionary<string, string?>(envelope.Stores, StringComparer.Ordinal),
            envelope.ExportedUtc);
        return true;
    }

    /// <summary>Describe exactly which local categories the staged restore will replace or clear.</summary>
    public static string Preview(LocalDataBackupContents contents)
    {
        var lines = StoreCatalog.Select(store =>
            $"• {store.Label}: {(contents.Stores[store.Key] is null ? "empty in this backup" : "will be restored")}");
        return $"Backup created {contents.ExportedUtc.ToLocalTime():yyyy-MM-dd HH:mm}.\n\n" +
               "Settings and local records:\n" + string.Join("\n", lines) +
               "\n\nSign-ins/cookies, downloaded file contents, logs, and session state are not included. " +
               "Download history does include its saved local paths.";
    }

    /// <summary>Apply the local records only after the Settings window accepts; individual writes are atomic and a failed batch rolls back prior files.</summary>
    public static void RestoreStores(
        IReadOnlyDictionary<string, string?> stores,
        int downloadHistoryCap = DownloadHistoryStore.MaxEntries)
    {
        if (stores.Count != StoreCatalog.Length
            || StoreCatalog.Any(store => !stores.ContainsKey(store.Key))
            || stores.Keys.Any(key => !StoreCatalog.Any(store => store.Key == key)))
            throw new InvalidDataException("The staged backup store list is incomplete.");

        var previous = CaptureStores();
        var changed = new List<string>();
        try
        {
            foreach (var store in StoreCatalog)
            {
                WriteStore(store.Key, stores[store.Key], downloadHistoryCap);
                changed.Add(store.Key);
            }
        }
        catch (Exception restoreError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var key in changed.AsEnumerable().Reverse())
            {
                try { WriteStore(key, previous[key], DownloadHistoryStore.MaxEntries); }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            if (rollbackErrors.Count > 0)
                throw new AggregateException("The local-data restore failed and one or more rollback writes also failed.",
                    new[] { restoreError }.Concat(rollbackErrors));
            throw new IOException("The local-data restore failed; the previous records were restored.", restoreError);
        }
    }

    private static Dictionary<string, string?> CaptureStores()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var store in StoreCatalog)
        {
            if (store.Key == "downloads")
            {
                result[store.Key] = DownloadHistoryStore.ExportFullJson(AppPaths.DownloadsHistoryPath);
                continue;
            }
            var path = StorePath(store.Key);
            result[store.Key] = File.Exists(path) ? File.ReadAllText(path) : null;
        }
        return result;
    }

    private static void WriteStore(string key, string? contents, int downloadHistoryCap)
    {
        if (key == "downloads")
        {
            if (contents is null)
                DownloadHistoryStore.Clear(AppPaths.DownloadsHistoryPath);
            else if (!DownloadHistoryStore.TryReplaceFromJson(AppPaths.DownloadsHistoryPath, contents,
                         downloadHistoryCap, out var error))
                throw new InvalidDataException(error ?? "The download history couldn't be restored.");
            return;
        }

        var path = StorePath(key);
        if (contents is null)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        else
        {
            AtomicFile.WriteAllText(path, contents);
        }
    }

    private static string StorePath(string key) => key switch
    {
        "readingList" => AppPaths.ReadingListPath,
        "notes" => AppPaths.NotesPath,
        "weeklyStats" => AppPaths.WeeklyStatsPath,
        "bookmarks" => AppPaths.BookmarksPath,
        "drafts" => AppPaths.DraftsPath,
        "snippets" => AppPaths.SnippetsPath,
        "privacyStats" => AppPaths.PrivacyStatsPath,
        "profileUsage" => AppPaths.ProfileUsagePath,
        "paletteHistory" => AppPaths.PaletteHistoryPath,
        "activityHistory" => AppPaths.ActivityHistoryPath,
        _ => throw new InvalidDataException("The backup refers to an unknown local store."),
    };

    private sealed class Envelope
    {
        public int Version { get; set; }
        public DateTimeOffset ExportedUtc { get; set; }
        public string? SettingsJson { get; set; }
        public Dictionary<string, string?>? Stores { get; set; }
    }
}
