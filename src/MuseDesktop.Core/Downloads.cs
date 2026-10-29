using System.Text.Json;

namespace MuseDesktop.Core;

/// <summary>What a download history entry represents (v0.6.0, S2 #16).</summary>
public enum DownloadRecordState
{
    Completed,
    Failed,
}

/// <summary>
/// One finished download, remembered so the Downloads window can offer
/// Open / Show-in-folder after the status-bar message has moved on
/// (v0.4.0). Only the file name, path, size, and time are kept — never
/// the URL it came from. Since v0.6.0 (S2 #16) failures are recorded too
/// (<see cref="DownloadRecordState.Failed"/>); files written by older
/// builds have no state field and deserialize as Completed, which is
/// what they all were.
/// </summary>
public sealed class DownloadRecord
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset CompletedUtc { get; set; }
    public DownloadRecordState State { get; set; } = DownloadRecordState.Completed;

    /// <summary>For a failed download, the interrupt reason name (e.g. "NetworkFailed") — never a URL.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// A short note the user attached to this entry (R4 #137), kept with
    /// the history on this PC only. Files written by older builds have
    /// no note field and read as null.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// The verify-after-download outcome (R4 #136): true = the file's
    /// SHA-256 matched the hash the user pasted, false = it did NOT,
    /// null = never checked. A later re-download of the same name is a
    /// new record, so a stale verdict can never ride along.
    /// </summary>
    public bool? HashVerified { get; set; }
}

/// <summary>
/// The download history: a small JSON list next to the settings file,
/// newest first, capped so it can never grow without bound. Loading a
/// missing or corrupt file yields an empty history — this is a
/// convenience record, not user data worth preserving heroics for.
/// Read-modify-write operations are serialized per path within this
/// process; AtomicFile still provides the final atomic replacement.
/// Cross-process coordination is not provided by this store.
/// The cap is configurable since v0.6.0 (S2 #16): 50 (the historical
/// default), 200, or 1000 — see <see cref="AppSettings.DownloadHistoryCap"/>.
/// Existing callers that omit the cap keep the old 50-entry behavior.
/// </summary>
public static class DownloadHistoryStore
{
    public const int MaxEntries = 50;

    /// <summary>The only cap values the settings UI offers; anything else sanitizes back to the default.</summary>
    public static readonly int[] AllowedCaps = { 50, 200, 1000 };

    public static int SanitizeCap(int cap) => AllowedCaps.Contains(cap) ? cap : MaxEntries;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Serialize each history-file transaction, not merely its final atomic
    // replacement. Fixed stripes bound lock memory; collisions only cause
    // harmless extra serialization between unrelated paths.
    private static readonly StringComparer HistoryPathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly SemaphoreSlim[] TransactionGates = CreateTransactionGates();

    private static SemaphoreSlim[] CreateTransactionGates()
    {
        var gates = new SemaphoreSlim[64];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static SemaphoreSlim GetTransactionGate(string fullPath)
    {
        var hash = HistoryPathComparer.GetHashCode(fullPath) & int.MaxValue;
        return TransactionGates[hash % TransactionGates.Length];
    }

    public static List<DownloadRecord> Load(string path, int cap = MaxEntries)
    {
        cap = Math.Max(1, cap);
        try
        {
            var fullPath = NormalizePath(path);
            var gate = GetTransactionGate(fullPath);
            gate.Wait();
            try { return LoadUnderGate(fullPath, cap); }
            finally { gate.Release(); }
        }
        catch (Exception)
        {
            return new List<DownloadRecord>();
        }
    }

    /// <summary>Serialize the validated full download history while holding the same transaction gate as mutations.</summary>
    public static string ExportFullJson(string path, int cap = MaxEntries)
    {
        var fullPath = NormalizePath(path);
        var gate = GetTransactionGate(fullPath);
        gate.Wait();
        try { return JsonSerializer.Serialize(LoadUnderGate(fullPath, cap), Options); }
        finally { gate.Release(); }
    }

    /// <summary>Parse and validate a portable download-history snapshot without writing it.</summary>
    public static bool TryParseJson(string json, out List<DownloadRecord>? records, out string? error)
    {
        error = null;
        List<DownloadRecord>? imported;
        try
        {
            imported = JsonSerializer.Deserialize<List<DownloadRecord>>(json, Options);
        }
        catch (Exception)
        {
            imported = null;
        }
        if (imported is null || imported.Any(record => !IsValidRecord(record)))
        {
            records = null;
            error = "The download history is damaged or contains an invalid record.";
            return false;
        }
        records = SanitizeRecords(imported, MaxEntries);
        return true;
    }

    /// <summary>Replace the history from a validated backup through the ordinary path transaction gate.</summary>
    public static bool TryReplaceFromJson(string path, string json, int cap, out string? error)
    {
        if (!TryParseJson(json, out var imported, out error) || imported is null)
            return false;
        try
        {
            Save(path, imported, cap);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // Caller owns the matching transaction gate. Do not call public Load
    // from a transaction: SemaphoreSlim is deliberately non-reentrant.
    private static List<DownloadRecord> LoadUnderGate(string path, int cap)
    {
        try
        {
            if (!File.Exists(path))
                return new List<DownloadRecord>();
            var records = JsonSerializer.Deserialize<List<DownloadRecord>>(File.ReadAllText(path), Options)
                          ?? new List<DownloadRecord>();
            return SanitizeRecords(records, cap);
        }
        catch (Exception)
        {
            return new List<DownloadRecord>();
        }
    }

    private static List<DownloadRecord> SanitizeRecords(IEnumerable<DownloadRecord?> records, int cap)
    {
        var valid = records.Where(IsValidRecord).Cast<DownloadRecord>().ToList();
        foreach (var record in valid)
        {
            if (record.Note is { } note)
            {
                note = note.Trim();
                record.Note = note.Length > 200 ? note[..200] : note;
            }
            if (record.FailureReason is { } reason &&
                (reason.Length > 64 || reason.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
                record.FailureReason = null;
        }
        return valid.OrderByDescending(r => r.CompletedUtc).Take(Math.Max(1, cap)).ToList();
    }

    private static bool IsValidRecord(DownloadRecord? record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.FileName) ||
            record.FileName.Length > 255 || record.FileName is "." or ".." ||
            record.FileName.IndexOfAny(new[] { '/', '\\' }) >= 0 ||
            record.FileName.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(record.FullPath) || record.FullPath.Length > 32_767 ||
            record.FullPath.Any(char.IsControl) || record.SizeBytes < 0 ||
            record.CompletedUtc == default || !Enum.IsDefined(record.State))
            return false;

        var pathLeaf = record.FullPath.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault();
        return string.Equals(pathLeaf, record.FileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Diagnostics-safe history export, built from the validated, capped
    /// history rather than copying the raw file. Preserve useful metadata,
    /// but never include the local destination path in a support bundle.
    /// This is intentionally not a round-trip backup format.
    /// </summary>
    public static string ExportJson(string path, int cap = MaxEntries)
    {
        var export = Load(path, cap).Select(record => new
        {
            record.FileName,
            record.SizeBytes,
            record.CompletedUtc,
            record.State,
            record.FailureReason,
            record.Note,
            record.HashVerified,
        });
        return JsonSerializer.Serialize(export, Options);
    }

    public static void Save(string path, List<DownloadRecord> records, int cap = MaxEntries)
    {
        cap = Math.Max(1, cap);
        var fullPath = NormalizePath(path);
        var gate = GetTransactionGate(fullPath);
        gate.Wait();
        try { SaveUnderGate(fullPath, records, cap); }
        finally { gate.Release(); }
    }

    private static void SaveUnderGate(string fullPath, List<DownloadRecord> records, int cap)
    {
        cap = Math.Max(1, cap);
        AtomicFile.WriteAllText(fullPath, JsonSerializer.Serialize(records.Take(cap).ToList(), Options));
    }

    /// <summary>Prepend a finished download and persist, keeping the cap.</summary>
    public static void Add(string path, DownloadRecord record, int cap = MaxEntries)
    {
        cap = Math.Max(1, cap);
        var fullPath = NormalizePath(path);
        var gate = GetTransactionGate(fullPath);
        gate.Wait();
        try
        {
            var records = LoadUnderGate(fullPath, cap);
            records.Insert(0, record);
            SaveUnderGate(fullPath, records, cap);
        }
        finally { gate.Release(); }
    }

    /// <summary>Remove one entry (the Downloads window's Delete-file action, S2 #17). Returns true when an entry was removed.</summary>
    public static bool Remove(string path, DownloadRecord record, int cap = MaxEntries)
    {
        cap = Math.Max(1, cap);
        string fullPath;
        try { fullPath = NormalizePath(path); }
        catch { return false; }
        var gate = GetTransactionGate(fullPath);
        gate.Wait();
        try
        {
            var records = LoadUnderGate(fullPath, cap);
            var removed = records.RemoveAll(r =>
                string.Equals(r.FullPath, record.FullPath, StringComparison.OrdinalIgnoreCase)
                && r.CompletedUtc == record.CompletedUtc) > 0;
            if (removed)
                SaveUnderGate(fullPath, records, cap);
            return removed;
        }
        finally { gate.Release(); }
    }

    public static void Clear(string path) => Save(path, new List<DownloadRecord>());

    /// <summary>
    /// Mutate one stored entry in place (matched like
    /// <see cref="Remove"/>: full path + completion time) and persist.
    /// Returns true when an entry matched. The R4 note (#137) and
    /// hash-verdict (#136) writes go through here.
    /// </summary>
    public static bool UpdateRecord(string path, DownloadRecord record, Action<DownloadRecord> mutate, int cap = MaxEntries)
    {
        cap = Math.Max(1, cap);
        string fullPath;
        try { fullPath = NormalizePath(path); }
        catch { return false; }
        var gate = GetTransactionGate(fullPath);
        gate.Wait();
        try
        {
            var records = LoadUnderGate(fullPath, cap);
            var match = records.FirstOrDefault(r =>
                string.Equals(r.FullPath, record.FullPath, StringComparison.OrdinalIgnoreCase)
                && r.CompletedUtc == record.CompletedUtc);
            if (match is null)
                return false;
            mutate(match);
            SaveUnderGate(fullPath, records, cap);
            return true;
        }
        finally { gate.Release(); }
    }

    /// <summary>Update one history record using async atomic persistence.</summary>
    public static async Task<bool> UpdateRecordAsync(
        string path, DownloadRecord record, Action<DownloadRecord> mutate, int cap = MaxEntries,
        CancellationToken cancellationToken = default)
    {
        cap = Math.Max(1, cap);
        string fullPath;
        try { fullPath = NormalizePath(path); }
        catch { return false; }
        var gate = GetTransactionGate(fullPath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = LoadUnderGate(fullPath, cap);
            var match = records.FirstOrDefault(r =>
                string.Equals(r.FullPath, record.FullPath, StringComparison.OrdinalIgnoreCase)
                && r.CompletedUtc == record.CompletedUtc);
            if (match is null)
                return false;
            mutate(match);
            await AtomicFile.WriteAllTextAsync(
                fullPath, JsonSerializer.Serialize(records, Options), cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { gate.Release(); }
    }

    /// <summary>Attach (or clear) a note on one entry (R4 #137). Notes are trimmed and capped at 200 characters.</summary>
    public static bool SetNote(string path, DownloadRecord record, string? note, int cap = MaxEntries)
    {
        var cleaned = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (cleaned is { Length: > 200 })
            cleaned = cleaned[..200];
        return UpdateRecord(path, record, r => r.Note = cleaned, cap);
    }

    /// <summary>Attach (or clear) a note using async atomic persistence.</summary>
    public static Task<bool> SetNoteAsync(
        string path, DownloadRecord record, string? note, int cap = MaxEntries,
        CancellationToken cancellationToken = default)
    {
        var cleaned = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (cleaned is { Length: > 200 })
            cleaned = cleaned[..200];
        return UpdateRecordAsync(path, record, r => r.Note = cleaned, cap, cancellationToken);
    }

    /// <summary>Record a verify-after-download verdict on one entry (R4 #136); null clears it.</summary>
    public static bool SetHashVerified(string path, DownloadRecord record, bool? verified, int cap = MaxEntries) =>
        UpdateRecord(path, record, r => r.HashVerified = verified, cap);

    /// <summary>Record a verify-after-download verdict using async atomic persistence.</summary>
    public static Task<bool> SetHashVerifiedAsync(
        string path, DownloadRecord record, bool? verified, int cap = MaxEntries,
        CancellationToken cancellationToken = default) =>
        UpdateRecordAsync(path, record, r => r.HashVerified = verified, cap, cancellationToken);

    /// <summary>
    /// Point history entries at their files' new locations after a sort
    /// or archive move (R4 #131/#138): keys are the OLD full paths,
    /// values the NEW ones; file names follow their paths. Returns how
    /// many entries were rewritten.
    /// </summary>
    public static int RewritePaths(string path, IReadOnlyDictionary<string, string> newPathByOld, int cap = MaxEntries)
    {
        if (newPathByOld.Count == 0)
            return 0;
        cap = Math.Max(1, cap);
        string fullPath;
        try { fullPath = NormalizePath(path); }
        catch { return 0; }
        var gate = GetTransactionGate(fullPath);
        gate.Wait();
        try
        {
            var lookup = new Dictionary<string, string>(newPathByOld, StringComparer.OrdinalIgnoreCase);
            var records = LoadUnderGate(fullPath, cap);
            var changed = 0;
            foreach (var record in records)
            {
                if (lookup.TryGetValue(record.FullPath, out var newPath) &&
                    !string.Equals(newPath, record.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    record.FullPath = newPath;
                    record.FileName = Path.GetFileName(newPath);
                    changed++;
                }
            }
            if (changed > 0)
                SaveUnderGate(fullPath, records, cap);
            return changed;
        }
        finally { gate.Release(); }
    }
}

/// <summary>State filter for the Downloads window history list (S2 #16).</summary>
public enum DownloadStateFilter
{
    All,
    Completed,
    Failed,
}

/// <summary>Sort order for the Downloads window history list (S2 #16).</summary>
public enum DownloadHistorySort
{
    NewestFirst,
    LargestFirst,
    NameAscending,
}

/// <summary>
/// Pure search / filter / sort over history records for the Downloads
/// window (S2 #16): search matches the file name (case-insensitive),
/// the filter selects by <see cref="DownloadRecordState"/>, and the
/// sort is date (newest first), size (largest first), or name (A–Z).
/// </summary>
public static class DownloadHistoryQuery
{
    public static List<DownloadRecord> Apply(
        IEnumerable<DownloadRecord> records,
        string? search,
        DownloadStateFilter filter,
        DownloadHistorySort sort)
    {
        IEnumerable<DownloadRecord> query = records;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(r => r.FileName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        query = filter switch
        {
            DownloadStateFilter.Completed => query.Where(r => r.State == DownloadRecordState.Completed),
            DownloadStateFilter.Failed => query.Where(r => r.State == DownloadRecordState.Failed),
            _ => query,
        };
        return sort switch
        {
            DownloadHistorySort.LargestFirst => query.OrderByDescending(r => r.SizeBytes).ToList(),
            DownloadHistorySort.NameAscending => query.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => query.OrderByDescending(r => r.CompletedUtc).ToList(),
        };
    }
}

/// <summary>What to do when a download's target file already exists (S2 #21).</summary>
public enum DownloadCollisionPolicy
{
    /// <summary>Save as "name (1).ext", "name (2).ext", … (default).</summary>
    AutoRename,

    /// <summary>Fall back to the Save-As dialog for that file so the user chooses.</summary>
    AskWhereToSave,
}

/// <summary>
/// Effective downloads folder (S2 #20): a per-profile override wins over
/// the shared downloads folder; when neither is set the result is null,
/// meaning "the system Downloads folder" (WebView2's own default), so
/// a user who moved their Windows Downloads folder is still honored.
/// </summary>
public static class DownloadFolderResolver
{
    public static string? Resolve(string? profileOverride, string? sharedFolder)
    {
        if (!string.IsNullOrWhiteSpace(profileOverride))
            return profileOverride.Trim();
        if (!string.IsNullOrWhiteSpace(sharedFolder))
            return sharedFolder.Trim();
        return null;
    }
}

/// <summary>
/// Collision-free file names (S2 #21): "report.pdf" that already exists
/// becomes "report (1).pdf", then "(2)", and so on — the Windows
/// Explorer convention. The existence check is injectable for tests.
/// </summary>
public static class UniqueFileName
{
    public static string ForPath(string fullPath) => ForPath(fullPath, File.Exists);

    public static string ForPath(string fullPath, Func<string, bool> exists)
    {
        if (!exists(fullPath))
            return fullPath;
        var dir = Path.GetDirectoryName(fullPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);
        for (var i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!exists(candidate))
                return candidate;
        }

        // Returning fullPath here would silently re-select the occupied
        // destination after exhausting the collision range. Callers must
        // treat exhaustion as a failure rather than risk overwriting data.
        throw new InvalidOperationException(
            $"Unable to find an unused filename for '{Path.GetFileName(fullPath)}'.");
    }
}

/// <summary>Live state of one active download, for the manager UI (S2 #13).</summary>
public enum DownloadActivityState
{
    InProgress,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// One active (or just-finished) download as a plain model the shell
/// keeps in step with the WebView2 operation (S2 #13). The session-only
/// identity is <see cref="Id"/>; nothing here is persisted, and there is
/// deliberately no source-URL field — the URL lives only in the shell's
/// in-memory retry record.
/// </summary>
public sealed class ActiveDownload
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public DateTimeOffset? EstimatedEnd { get; set; }
    public DownloadActivityState State { get; set; } = DownloadActivityState.InProgress;
    public bool CanResume { get; set; }
    public double SpeedBytesPerSecond { get; set; }

    /// <summary>Whole percent complete, or null when the total is unknown.</summary>
    public int? Percent => TotalBytes is { } total && total > 0
        ? (int)Math.Clamp(BytesReceived * 100L / total, 0, 100)
        : null;
}

/// <summary>
/// The set of downloads currently in flight (S2 #13): add / remove /
/// find, plus the aggregate progress the status bar and taskbar show
/// when more than one download runs at once.
/// </summary>
public sealed class ActiveDownloadSet
{
    private readonly List<ActiveDownload> _items = new();

    public IReadOnlyList<ActiveDownload> Items => _items;
    public int Count => _items.Count;

    public void Add(ActiveDownload download) => _items.Add(download);

    public bool Remove(string id) => _items.RemoveAll(d => d.Id == id) > 0;

    public ActiveDownload? Find(string id) => _items.FirstOrDefault(d => d.Id == id);

    public bool AnyTransferring => _items.Any(d => d.State == DownloadActivityState.InProgress);
    public bool AnyPaused => _items.Any(d => d.State == DownloadActivityState.Paused);

    /// <summary>
    /// Overall completion across the downloads whose totals are known:
    /// summed bytes over summed totals. Null when no download has a
    /// known total (the shell shows an indeterminate state instead).
    /// </summary>
    public double? OverallFraction
    {
        get
        {
            long bytes = 0, total = 0;
            foreach (var d in _items)
            {
                if (d.State is not (DownloadActivityState.InProgress or DownloadActivityState.Paused))
                    continue;
                if (d.TotalBytes is { } t && t > 0)
                {
                    bytes += d.BytesReceived;
                    total += t;
                }
            }
            return total > 0 ? Math.Clamp((double)bytes / total, 0, 1) : null;
        }
    }

    /// <summary>Total current transfer speed across in-progress downloads (bytes/sec).</summary>
    public double TotalSpeed => _items
        .Where(d => d.State == DownloadActivityState.InProgress)
        .Sum(d => d.SpeedBytesPerSecond);
}

/// <summary>
/// A smoothed download rate (S2 #22): samples byte counts over time and
/// blends each instantaneous rate into an exponential moving average
/// (alpha 0.3), so the readout doesn't flicker with every progress tick.
/// </summary>
public sealed class DownloadRateSampler
{
    public const double Alpha = 0.3;

    private long _lastBytes;
    private DateTimeOffset _lastTime;
    private bool _hasSample;
    private double _rate;

    public double BytesPerSecond => _rate;

    /// <summary>Feed a new byte count; returns the smoothed rate (bytes/sec).</summary>
    public double Sample(long bytesReceived, DateTimeOffset now)
    {
        if (!_hasSample)
        {
            _lastBytes = bytesReceived;
            _lastTime = now;
            _hasSample = true;
            return 0;
        }
        var dt = (now - _lastTime).TotalSeconds;
        if (dt <= 0.05)
            return _rate;
        var delta = bytesReceived - _lastBytes;
        if (delta < 0)
        {
            // The operation restarted its count (e.g. a no-range resume):
            // restart the sampler rather than reporting a negative rate.
            _lastBytes = bytesReceived;
            _lastTime = now;
            _rate = 0;
            return 0;
        }
        var instant = delta / dt;
        _rate = _rate <= 0 ? instant : _rate + Alpha * (instant - _rate);
        _lastBytes = bytesReceived;
        _lastTime = now;
        return _rate;
    }

    /// <summary>Forget all samples (e.g. after a pause/resume).</summary>
    public void Reset()
    {
        _hasSample = false;
        _rate = 0;
    }
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

    /// <summary>Transfer speed for the manager and status bar (S2 #22): "512 B/s", "1.5 KB/s", "2.3 MB/s".</summary>
    public static string FormatSpeed(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond <= 0)
            return "0 B/s";
        return FormatBytes((long)Math.Round(bytesPerSecond)) + "/s";
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

    /// <summary>
    /// Progress with a speed readout (S2 #22): the main text is
    /// byte-identical to <see cref="ProgressStatus(string, long, long?, DateTimeOffset?, DateTimeOffset)"/>;
    /// a known speed is appended to the right-hand detail.
    /// </summary>
    public static (string Text, string Right) ProgressStatus(
        string fileName, long bytesReceived, long? totalBytes, DateTimeOffset? estimatedEnd, DateTimeOffset now,
        double speedBytesPerSecond)
    {
        var (text, right) = ProgressStatus(fileName, bytesReceived, totalBytes, estimatedEnd, now);
        if (speedBytesPerSecond > 0)
            right = $"{right} · {FormatSpeed(speedBytesPerSecond)}";
        return (text, right);
    }

    /// <summary>
    /// The status-bar pair when several downloads run at once (S2 #13):
    /// an aggregate, never a claim about any single file.
    /// </summary>
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
        {
            text = count == 1 ? "Download paused" : $"Downloads paused — {count} files";
        }
        else if (set.OverallFraction is { } fraction)
        {
            text = $"Downloading {count} files — {(int)(fraction * 100)}%";
        }
        else
        {
            text = $"Downloading {count} files…";
        }

        var parts = new List<string>();
        long bytes = active.Sum(d => d.BytesReceived);
        long total = active.Sum(d => d.TotalBytes ?? 0);
        if (total > 0)
            parts.Add($"{FormatBytes(bytes)} of {FormatBytes(total)}");
        else if (bytes > 0)
            parts.Add($"{FormatBytes(bytes)} received");
        if (set.AnyTransferring && set.TotalSpeed > 0)
            parts.Add(FormatSpeed(set.TotalSpeed));
        // An ETA only makes sense for the soonest-finishing known estimate.
        var eta = active
            .Where(d => d.State == DownloadActivityState.InProgress && d.EstimatedEnd is not null)
            .Select(d => d.EstimatedEnd!.Value - now)
            .Where(t => t > TimeSpan.Zero)
            .OrderBy(t => t)
            .FirstOrDefault();
        if (eta > TimeSpan.Zero)
            parts.Add($"about {FormatEta(eta)} left");
        return (text, string.Join(" · ", parts));
    }

    /// <summary>
    /// One-line detail for a row in the downloads manager (S2 #13):
    /// percent / bytes, ETA, and speed, or the paused/failed wording.
    /// </summary>
    public static string ActiveDetail(ActiveDownload download, DateTimeOffset now)
    {
        switch (download.State)
        {
            case DownloadActivityState.Paused:
                return download.Percent is { } pausedPct
                    ? $"Paused — {pausedPct}% · {FormatBytes(download.BytesReceived)} of {FormatBytes(download.TotalBytes ?? 0)}"
                    : $"Paused — {FormatBytes(download.BytesReceived)} received";
            case DownloadActivityState.Failed:
                return "Failed";
            case DownloadActivityState.Cancelled:
                return "Cancelled";
            case DownloadActivityState.Completed:
                return "Complete";
            default:
                var parts = new List<string>();
                if (download.Percent is { } pct)
                    parts.Add($"{pct}%");
                parts.Add(download.TotalBytes is { } total && total > 0
                    ? $"{FormatBytes(download.BytesReceived)} of {FormatBytes(total)}"
                    : $"{FormatBytes(download.BytesReceived)} received");
                if (download.EstimatedEnd is { } end && end - now is { } eta && eta > TimeSpan.Zero)
                    parts.Add($"about {FormatEta(eta)} left");
                if (download.SpeedBytesPerSecond > 0)
                    parts.Add(FormatSpeed(download.SpeedBytesPerSecond));
                return string.Join(" · ", parts);
        }
    }
}
