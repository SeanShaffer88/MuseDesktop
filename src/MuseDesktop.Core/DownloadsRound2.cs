using System.Security.Cryptography;
using System.Text.Json;

namespace MuseDesktop.Core;

// ───────────────────────────── sort rules (#131) ─────────────────────────────

/// <summary>The built-in file-type buckets downloads can be sorted into (R4 #131).</summary>
public enum DownloadFileCategory
{
    Images,
    Documents,
    Archives,
    Audio,
    Video,
    Programs,
    Other,
}

/// <summary>
/// Extension → category map for the built-in sort (R4 #131). Extensions
/// are matched case-insensitively with the leading dot; anything unknown
/// is <see cref="DownloadFileCategory.Other"/>.
/// </summary>
public static class DownloadFileTypes
{
    private static readonly Dictionary<string, DownloadFileCategory> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = DownloadFileCategory.Images,
        [".jpg"] = DownloadFileCategory.Images,
        [".jpeg"] = DownloadFileCategory.Images,
        [".gif"] = DownloadFileCategory.Images,
        [".webp"] = DownloadFileCategory.Images,
        [".bmp"] = DownloadFileCategory.Images,
        [".svg"] = DownloadFileCategory.Images,
        [".ico"] = DownloadFileCategory.Images,
        [".tif"] = DownloadFileCategory.Images,
        [".tiff"] = DownloadFileCategory.Images,

        [".pdf"] = DownloadFileCategory.Documents,
        [".doc"] = DownloadFileCategory.Documents,
        [".docx"] = DownloadFileCategory.Documents,
        [".txt"] = DownloadFileCategory.Documents,
        [".md"] = DownloadFileCategory.Documents,
        [".rtf"] = DownloadFileCategory.Documents,
        [".odt"] = DownloadFileCategory.Documents,
        [".xls"] = DownloadFileCategory.Documents,
        [".xlsx"] = DownloadFileCategory.Documents,
        [".csv"] = DownloadFileCategory.Documents,
        [".ppt"] = DownloadFileCategory.Documents,
        [".pptx"] = DownloadFileCategory.Documents,
        [".epub"] = DownloadFileCategory.Documents,

        [".zip"] = DownloadFileCategory.Archives,
        [".7z"] = DownloadFileCategory.Archives,
        [".rar"] = DownloadFileCategory.Archives,
        [".tar"] = DownloadFileCategory.Archives,
        [".gz"] = DownloadFileCategory.Archives,
        [".tgz"] = DownloadFileCategory.Archives,
        [".bz2"] = DownloadFileCategory.Archives,
        [".xz"] = DownloadFileCategory.Archives,

        [".mp3"] = DownloadFileCategory.Audio,
        [".wav"] = DownloadFileCategory.Audio,
        [".flac"] = DownloadFileCategory.Audio,
        [".ogg"] = DownloadFileCategory.Audio,
        [".m4a"] = DownloadFileCategory.Audio,
        [".aac"] = DownloadFileCategory.Audio,

        [".mp4"] = DownloadFileCategory.Video,
        [".mkv"] = DownloadFileCategory.Video,
        [".mov"] = DownloadFileCategory.Video,
        [".avi"] = DownloadFileCategory.Video,
        [".webm"] = DownloadFileCategory.Video,
        [".wmv"] = DownloadFileCategory.Video,

        [".exe"] = DownloadFileCategory.Programs,
        [".msi"] = DownloadFileCategory.Programs,
        [".msix"] = DownloadFileCategory.Programs,
        [".appx"] = DownloadFileCategory.Programs,
        [".dmg"] = DownloadFileCategory.Programs,
        [".deb"] = DownloadFileCategory.Programs,
        [".rpm"] = DownloadFileCategory.Programs,
    };

    public static DownloadFileCategory CategoryFor(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "");
        return !string.IsNullOrEmpty(ext) && ByExtension.TryGetValue(ext, out var category)
            ? category
            : DownloadFileCategory.Other;
    }

    /// <summary>The extensions in one category (sorted, with leading dots) — the category sort rules are built from this.</summary>
    public static IReadOnlyList<string> ExtensionsFor(DownloadFileCategory category) =>
        ByExtension
            .Where(pair => pair.Value == category)
            .Select(pair => pair.Key)
            .OrderBy(ext => ext, StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// One download sort rule (R4 #131): files whose name matches — by
/// extension list and/or a <c>*</c>/<c>?</c> name pattern — move into
/// <see cref="TargetSubfolder"/> under the downloads folder. Rules are
/// user data (they live in settings and in the Settings text editor), so
/// every rule passes through <see cref="Sanitized"/> before use: the
/// subfolder must stay a plain relative subfolder — never rooted, never
/// escaping with ".." — because the shell moves real files by it.
/// </summary>
public sealed class DownloadSortRule
{
    public string Name { get; set; } = "";
    public List<string> Extensions { get; set; } = new();
    public string? NamePattern { get; set; }
    public string TargetSubfolder { get; set; } = "";

    public bool Matches(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;
        if (Extensions.Count > 0)
        {
            var ext = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(ext) &&
                Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                return true;
        }
        return !string.IsNullOrWhiteSpace(NamePattern) && GlobMatch(NamePattern, fileName);
    }

    /// <summary>
    /// Case-insensitive wildcard match supporting <c>*</c> (any run) and
    /// <c>?</c> (one character) — the classic two-pointer glob, no regex.
    /// </summary>
    public static bool GlobMatch(string pattern, string value)
    {
        var p = 0;
        var v = 0;
        var starP = -1;
        var starV = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length &&
                (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(value[v])))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starV = v;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                v = ++starV;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    /// <summary>
    /// The cleaned-up rule, or null when nothing usable remains (no
    /// matcher, or a subfolder that isn't a plain relative subfolder).
    /// </summary>
    public DownloadSortRule? Sanitized()
    {
        var subfolder = (TargetSubfolder ?? "").Trim().Replace('/', '\\');
        if (subfolder.Length == 0 || Path.IsPathRooted(subfolder))
            return null;
        // The rooted checks above follow the HOST OS — but the shell
        // that moves files is always Windows, so reject Windows-rooted
        // shapes explicitly whatever OS sanitizes the settings.
        if (subfolder.StartsWith('\\'))
            return null; // rooted (\abs) or UNC (\\server\share)
        if (subfolder.Length >= 2 && char.IsLetter(subfolder[0]) && subfolder[1] == ':')
            return null; // drive-qualified (C:\abs, C:abs)
        var segments = subfolder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is ".." or "."))
            return null;
        subfolder = string.Join("\\", segments);

        var extensions = new List<string>();
        foreach (var raw in Extensions ?? new List<string>())
        {
            var ext = (raw ?? "").Trim().ToLowerInvariant();
            if (ext.Length < 2 || !ext.StartsWith('.'))
                continue;
            if (ext.IndexOfAny(new[] { '\\', '/', '*', '?', ' ' }) >= 0)
                continue;
            if (!extensions.Contains(ext))
                extensions.Add(ext);
        }

        var pattern = string.IsNullOrWhiteSpace(NamePattern) ? null : NamePattern.Trim();
        if (pattern is { Length: > 100 })
            pattern = pattern[..100];
        if (extensions.Count == 0 && pattern is null)
            return null;

        var name = (Name ?? "").Trim();
        if (name.Length == 0)
            name = subfolder;
        if (name.Length > 60)
            name = name[..60];

        return new DownloadSortRule
        {
            Name = name,
            Extensions = extensions,
            NamePattern = pattern,
            TargetSubfolder = subfolder,
        };
    }

    /// <summary>
    /// The Settings text format (R4 #131): one rule per line —
    /// <c>pdf, docx = Documents\School</c> or <c>*invoice* = Finance</c>;
    /// blank lines and <c>#</c> comments are skipped. A matcher token
    /// containing <c>*</c> or <c>?</c> is a name pattern, anything else is
    /// an extension (the dot is optional when typing). Bad lines are
    /// reported, never silently kept.
    /// </summary>
    public static List<DownloadSortRule> ParseLines(string? text, out List<string> errors)
    {
        errors = new List<string>();
        var rules = new List<DownloadSortRule>();
        if (string.IsNullOrWhiteSpace(text))
            return rules;
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var arrow = line.IndexOf("->", StringComparison.Ordinal);
            var eq = line.IndexOf('=');
            var split = arrow >= 0 ? arrow : eq;
            if (split <= 0)
            {
                errors.Add($"\"{line}\" — expected \"extensions or *pattern* = subfolder\".");
                continue;
            }
            var matchPart = line[..split].Trim();
            var subfolder = line[(split + (arrow >= 0 ? 2 : 1))..].Trim();
            var rule = new DownloadSortRule { TargetSubfolder = subfolder };
            foreach (var token in matchPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.Contains('*') || token.Contains('?'))
                    rule.NamePattern = token;
                else
                    rule.Extensions.Add(token.StartsWith('.') ? token : "." + token);
            }
            if (rule.Sanitized() is { } clean)
                rules.Add(clean);
            else
                errors.Add($"\"{line}\" — the subfolder must be a plain folder name (no drive, no \"..\").");
        }
        return rules;
    }

    /// <summary>Render rules back to the Settings text format (a stable round-trip through <see cref="ParseLines"/>).</summary>
    public static string FormatLines(IEnumerable<DownloadSortRule> rules)
    {
        var lines = new List<string>();
        foreach (var rule in rules)
        {
            var matchers = new List<string>(rule.Extensions);
            if (!string.IsNullOrWhiteSpace(rule.NamePattern))
                matchers.Add(rule.NamePattern);
            lines.Add($"{string.Join(", ", matchers)} = {rule.TargetSubfolder}");
        }
        return string.Join("\n", lines);
    }
}

/// <summary>One planned file move (R4 #131/#138): where a file goes, and which rule sent it there.</summary>
public sealed class DownloadMove
{
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public string RuleName { get; set; } = "";

    public DownloadMove() { }

    public DownloadMove(string sourcePath, string destinationPath, string ruleName)
    {
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        RuleName = ruleName;
    }
}

/// <summary>
/// The archive hook for the sort planner (R4 #138): completed downloads
/// older than <see cref="AfterDays"/> go to a dated subfolder of
/// <see cref="ArchiveFolder"/> instead of a sort subfolder.
/// </summary>
public sealed record DownloadArchiveOptions(string ArchiveFolder, int AfterDays);

/// <summary>
/// Plans download sorts and archives (R4 #131/#138) WITHOUT touching the
/// disk: the shell shows the plan (the preview), applies the moves the
/// user approved, and journals them for undo. Only completed downloads
/// sitting directly in the base folder are planned — files already in a
/// subfolder are sorted by definition. Collisions resolve through
/// <see cref="UniqueFileName"/> against both the disk and the moves
/// already planned, so a plan never overwrites anything.
/// </summary>
public static class DownloadSortPlanner
{
    public const int MaxRules = 50;

    /// <summary>One rule per file-type category (including Other, so "sort by type" files everything).</summary>
    public static List<DownloadSortRule> CategoryRules() =>
        Enum.GetValues<DownloadFileCategory>()
            .Select(category => new DownloadSortRule
            {
                Name = category.ToString(),
                Extensions = DownloadFileTypes.ExtensionsFor(category).ToList(),
                TargetSubfolder = category.ToString(),
            })
            .ToList();

    /// <summary>Drop unusable rules and cap the list — settings repair for hand-edited files.</summary>
    public static List<DownloadSortRule> SanitizeRules(IEnumerable<DownloadSortRule>? rules)
    {
        var clean = new List<DownloadSortRule>();
        if (rules is null)
            return clean;
        foreach (var rule in rules)
        {
            if (rule?.Sanitized() is { } sanitized)
                clean.Add(sanitized);
            if (clean.Count >= MaxRules)
                break;
        }
        return clean;
    }

    /// <summary>
    /// The sort plan for the completed records sitting in
    /// <paramref name="baseFolder"/>: custom rules first, then (when
    /// enabled) the category rules; the archive hook beats both for old
    /// files. <paramref name="exists"/> is the disk check, injectable
    /// for tests.
    /// </summary>
    public static List<DownloadMove> Plan(
        IEnumerable<DownloadRecord> records,
        string baseFolder,
        IReadOnlyList<DownloadSortRule> customRules,
        bool useCategoryRules,
        Func<string, bool> exists,
        DownloadArchiveOptions? archive = null,
        DateTimeOffset? now = null)
    {
        var rules = new List<DownloadSortRule>(customRules);
        if (useCategoryRules)
            rules.AddRange(CategoryRules());
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Taken(string path) => exists(path) || planned.Contains(path);

        var moves = new List<DownloadMove>();
        foreach (var record in records)
        {
            if (record.State != DownloadRecordState.Completed || string.IsNullOrWhiteSpace(record.FullPath))
                continue;
            var dir = Path.GetDirectoryName(record.FullPath);
            if (dir is null || !string.Equals(dir, baseFolder, StringComparison.OrdinalIgnoreCase))
                continue;
            var name = Path.GetFileName(record.FullPath);

            string destDir;
            string ruleName;
            if (archive is { AfterDays: > 0 } && now is { } moment &&
                record.CompletedUtc <= moment.AddDays(-archive.AfterDays))
            {
                destDir = Path.Combine(archive.ArchiveFolder,
                    record.CompletedUtc.ToLocalTime().ToString("yyyy-MM-dd"));
                ruleName = "Archive";
            }
            else
            {
                var rule = rules.FirstOrDefault(r => r.Matches(name));
                if (rule is null)
                    continue;
                destDir = Path.Combine(baseFolder, rule.TargetSubfolder);
                ruleName = rule.Name;
            }

            var destination = UniqueFileName.ForPath(Path.Combine(destDir, name), Taken);
            if (string.Equals(destination, record.FullPath, StringComparison.OrdinalIgnoreCase))
                continue;
            planned.Add(destination);
            moves.Add(new DownloadMove(record.FullPath, destination, ruleName));
        }
        return moves;
    }

    /// <summary>
    /// The manual archive plan (R4 #138): every completed record NOT
    /// already inside the archive folder moves to a dated subfolder
    /// (the day it finished downloading).
    /// </summary>
    public static List<DownloadMove> PlanArchive(
        IEnumerable<DownloadRecord> records,
        string archiveFolder,
        Func<string, bool> exists)
    {
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Taken(string path) => exists(path) || planned.Contains(path);
        var prefix = archiveFolder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

        var moves = new List<DownloadMove>();
        foreach (var record in records)
        {
            if (record.State != DownloadRecordState.Completed || string.IsNullOrWhiteSpace(record.FullPath))
                continue;
            if (record.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var destDir = Path.Combine(archiveFolder,
                record.CompletedUtc.ToLocalTime().ToString("yyyy-MM-dd"));
            var destination = UniqueFileName.ForPath(
                Path.Combine(destDir, Path.GetFileName(record.FullPath)), Taken);
            if (string.Equals(destination, record.FullPath, StringComparison.OrdinalIgnoreCase))
                continue;
            planned.Add(destination);
            moves.Add(new DownloadMove(record.FullPath, destination, "Archive"));
        }
        return moves;
    }

    /// <summary>
    /// The undo plan for one applied batch: each move reversed (newest
    /// first), skipping files that have since vanished, renaming around
    /// anything that now sits at the original spot — undo never
    /// overwrites either.
    /// </summary>
    public static List<DownloadMove> PlanUndo(DownloadSortBatch batch, Func<string, bool> exists)
    {
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Taken(string path) => exists(path) || planned.Contains(path);
        var moves = new List<DownloadMove>();
        for (var i = batch.Moves.Count - 1; i >= 0; i--)
        {
            var move = batch.Moves[i];
            if (!exists(move.DestinationPath))
                continue;
            var back = UniqueFileName.ForPath(move.SourcePath, Taken);
            planned.Add(back);
            moves.Add(new DownloadMove(move.DestinationPath, back, $"Undo {move.RuleName}"));
        }
        return moves;
    }
}

/// <summary>One applied sort/archive batch, kept so it can be undone (R4 #131).</summary>
public sealed class DownloadSortBatch
{
    public DateTimeOffset AppliedUtc { get; set; }
    public List<DownloadMove> Moves { get; set; } = new();
}

/// <summary>
/// The sort journal: the last <see cref="MaxBatches"/> applied batches
/// beside the settings file. Undo consumes the newest batch. A missing
/// or corrupt journal is simply empty — sorting is a convenience, and
/// the worst case is an undo the user does by hand in Explorer.
/// </summary>
public static class DownloadSortJournalStore
{
    public const int MaxBatches = 20;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static List<DownloadSortBatch> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new List<DownloadSortBatch>();
            return JsonSerializer.Deserialize<List<DownloadSortBatch>>(File.ReadAllText(path), Options)
                   ?? new List<DownloadSortBatch>();
        }
        catch (Exception)
        {
            return new List<DownloadSortBatch>();
        }
    }

    public static void Save(string path, List<DownloadSortBatch> batches)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var kept = batches.TakeLast(MaxBatches).ToList();
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(kept, Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static void Append(string path, DownloadSortBatch batch)
    {
        if (batch.Moves.Count == 0)
            return;
        var batches = Load(path);
        batches.Add(batch);
        Save(path, batches);
    }

    public static DownloadSortBatch? LastBatch(string path)
    {
        var batches = Load(path);
        return batches.Count > 0 ? batches[^1] : null;
    }

    public static bool RemoveLast(string path)
    {
        var batches = Load(path);
        if (batches.Count == 0)
            return false;
        batches.RemoveAt(batches.Count - 1);
        Save(path, batches);
        return true;
    }
}

// ───────────────────────────── scheduling (#132) ─────────────────────────────

/// <summary>Whether a transfer may run now, and if not, why and when to look again (R4 #132).</summary>
public sealed record DownloadScheduleDecision(bool Allowed, string? Reason, DateTimeOffset? NextChance);

/// <summary>
/// Download quiet hours (R4 #132): during the window, KNOWN-large files
/// wait; small files and files of unknown size pass (a size only exists
/// once the server reports one, and holding every download hostage to a
/// missing header would stall more than it saves). The decision names
/// the moment the window ends so the shell can re-issue the held
/// download then. Reuses the notification <see cref="QuietHours"/> math,
/// but it is a separate schedule with its own settings.
/// </summary>
public static class DownloadSchedule
{
    public static readonly DownloadScheduleDecision Allow = new(true, null, null);

    public static DownloadScheduleDecision Evaluate(
        bool quietEnabled,
        int startMinutes,
        int endMinutes,
        long largeFileBytes,
        DateTimeOffset localNow,
        long? sizeBytes)
    {
        if (!quietEnabled)
            return Allow;
        var nowMinutes = QuietHours.MinutesOfDay(localNow);
        if (!QuietHours.IsActive(startMinutes, endMinutes, nowMinutes))
            return Allow;
        if (largeFileBytes > 0 && sizeBytes is { } size && size < largeFileBytes)
            return Allow;
        // Defer only when a size is KNOWN to be at/over the threshold —
        // or when the threshold is "everything" (0) — per the doc above.
        if (largeFileBytes > 0 && sizeBytes is null)
            return Allow;
        var end = new DateTimeOffset(
            localNow.Year, localNow.Month, localNow.Day,
            endMinutes / 60, endMinutes % 60, 0, localNow.Offset);
        if (end <= localNow)
            end = end.AddDays(1);
        return new DownloadScheduleDecision(
            false,
            "Large downloads wait during the download quiet hours.",
            end);
    }
}

/// <summary>
/// The bandwidth limit (R4 #132). WebView2's pinned SDK has no
/// per-transfer throttle, so the limit is enforced as a governor over
/// whole transfers: while the measured aggregate speed is over the
/// limit it pauses the NEWEST transfer; while there is headroom it
/// resumes transfers IT paused (oldest first, one per evaluation, with
/// a hysteresis band so borderline speeds don't flap). Downloads the
/// user paused by hand are never touched. Pausing/resuming ride the
/// tracker's existing WebView2 controls. The result is a coarse average
/// cap, not a smooth shaper — see KNOWN_LIMITATIONS.
/// </summary>
public sealed class BandwidthGovernor
{
    /// <summary>Resume only when the aggregate is under this fraction of the limit.</summary>
    public const double ResumeFraction = 0.85;

    private readonly List<string> _pausedByGovernor = new();

    /// <summary>Ids this governor has paused and not yet resumed (its own bookkeeping only).</summary>
    public IReadOnlyList<string> GovernorPausedIds => _pausedByGovernor;

    /// <summary>
    /// Decide one governance step over the current transfers. Returns
    /// the ids to pause and to resume (at most one of each per call —
    /// the shell calls this on a timer, so convergence is gradual and
    /// stable).
    /// </summary>
    public (IReadOnlyList<string> ToPause, IReadOnlyList<string> ToResume) Evaluate(
        IReadOnlyList<ActiveDownload> items, long limitBytesPerSecond)
    {
        var toPause = new List<string>();
        var toResume = new List<string>();

        // Forget transfers that finished or were resumed by hand.
        _pausedByGovernor.RemoveAll(id =>
        {
            var item = items.FirstOrDefault(d => d.Id == id);
            return item is null || item.State != DownloadActivityState.Paused;
        });

        if (limitBytesPerSecond <= 0)
        {
            toResume.AddRange(_pausedByGovernor);
            _pausedByGovernor.Clear();
            return (toPause, toResume);
        }

        var transferring = items
            .Where(d => d.State == DownloadActivityState.InProgress)
            .ToList();
        var totalSpeed = transferring.Sum(d => d.SpeedBytesPerSecond);

        if (totalSpeed > limitBytesPerSecond)
        {
            var victim = transferring[^1];
            _pausedByGovernor.Add(victim.Id);
            toPause.Add(victim.Id);
        }
        else if (totalSpeed <= limitBytesPerSecond * ResumeFraction && _pausedByGovernor.Count > 0)
        {
            var id = _pausedByGovernor[0];
            _pausedByGovernor.RemoveAt(0);
            toResume.Add(id);
        }
        return (toPause, toResume);
    }
}

/// <summary>"9:00"-style time-of-day text for the Settings quiet-hours fields (R4 #132).</summary>
public static class TimeOfDayText
{
    public static bool TryParseMinutes(string? text, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2)
            return false;
        if (!int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute))
            return false;
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            return false;
        minutes = hour * 60 + minute;
        return true;
    }

    public static string Format(int minutes)
    {
        minutes = Math.Clamp(minutes, 0, QuietHours.MinutesPerDay - 1);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }
}

// ─────────────────── duplicate guard (#133) + hash check (#136) ───────────────────

/// <summary>
/// The duplicate-download guard (R4 #133): a new download is a likely
/// duplicate when a COMPLETED history entry has the same file name and
/// — where both sizes are known — the same size. The hash comparison
/// exists too but is deliberately NOT run per download (hashing every
/// finished file on every new download is real disk work); it runs when
/// the user asks via Verify SHA-256 (#136).
/// </summary>
public static class DuplicateDownloadGuard
{
    public static DownloadRecord? FindInHistory(
        string? fileName, long? sizeBytes, IEnumerable<DownloadRecord> history)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        foreach (var record in history)
        {
            if (record.State != DownloadRecordState.Completed)
                continue;
            if (!string.Equals(record.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (sizeBytes is { } size && size > 0 && record.SizeBytes > 0 && record.SizeBytes != size)
                continue;
            return record;
        }
        return null;
    }

    /// <summary>True when two files have identical content: cheap size check first, SHA-256 only on a size match.</summary>
    public static bool SameContent(string pathA, string pathB)
    {
        try
        {
            var a = new FileInfo(pathA);
            var b = new FileInfo(pathB);
            if (!a.Exists || !b.Exists || a.Length != b.Length)
                return false;
            return string.Equals(
                DownloadHash.ComputeSha256(pathA),
                DownloadHash.ComputeSha256(pathB),
                StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>The outcome of a verify-after-download check (R4 #136).</summary>
public sealed record HashCheckResult(bool ExpectedValid, bool Matches, string ActualSha256, string? Error);

/// <summary>
/// SHA-256 helpers for verify-after-download (R4 #136): normalize a
/// user-pasted hash (spaces and colons from grouped formats are fine),
/// compute a file's hash, and produce one clear pass/fail result.
/// </summary>
public static class DownloadHash
{
    public const int Sha256HexLength = 64;

    public static bool TryNormalizeSha256(string? text, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var cleaned = new string(text.Where(c => !char.IsWhiteSpace(c) && c != ':').ToArray())
            .ToLowerInvariant();
        if (cleaned.Length != Sha256HexLength)
            return false;
        if (cleaned.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return false;
        normalized = cleaned;
        return true;
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static HashCheckResult Verify(string path, string? expectedText)
    {
        if (!TryNormalizeSha256(expectedText, out var expected))
            return new HashCheckResult(false, false, "", "That isn't a SHA-256 hash — it should be 64 hex characters.");
        try
        {
            var actual = ComputeSha256(path);
            return new HashCheckResult(true, string.Equals(actual, expected, StringComparison.Ordinal), actual, null);
        }
        catch (Exception ex)
        {
            return new HashCheckResult(true, false, "", ex.Message);
        }
    }
}

// ───────────────────────────── batch capture (#134) ─────────────────────────────

/// <summary>One validated batch link: the URL, plus the file name it implies (R4 #134).</summary>
public sealed record BatchLink(string Url, string FileName);

/// <summary>The parse outcome: the links to confirm, and the raw lines that weren't usable (R4 #134).</summary>
public sealed class BatchLinkParseResult
{
    public List<BatchLink> Links { get; } = new();
    public List<string> Rejected { get; } = new();
}

/// <summary>
/// Batch link capture parsing (R4 #134): one link per pasted line;
/// blank lines and <c>#</c> comments are skipped; only absolute
/// http/https links with a host count; duplicates (same address,
/// compared case-insensitively and ignoring a trailing slash) collapse
/// to the first occurrence. The list is capped — a paste is a convenience,
/// not a download manager queue. Links are validated here, but nothing
/// is fetched: the confirm step and the actual downloads are the shell's.
/// </summary>
public static class BatchLinkParser
{
    public const int MaxLinks = 100;

    public static BatchLinkParseResult Parse(string? text)
    {
        var result = new BatchLinkParseResult();
        if (string.IsNullOrWhiteSpace(text))
            return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                result.Rejected.Add(line);
                continue;
            }
            var key = uri.AbsoluteUri.TrimEnd('/');
            if (!seen.Add(key))
                continue;
            if (result.Links.Count >= MaxLinks)
            {
                result.Rejected.Add(line);
                continue;
            }
            result.Links.Add(new BatchLink(uri.AbsoluteUri, FileNameFor(uri)));
        }
        return result;
    }

    private static string FileNameFor(Uri uri)
    {
        var segment = uri.Segments.LastOrDefault() ?? "";
        var name = Uri.UnescapeDataString(segment).Trim();
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        // A bare host or folder link ("https://example.com/") leaves
        // nothing but separators behind — that sanitizes to "_"s, which
        // is no name at all.
        if (name.Length == 0 || name.All(c => c == '_') || name is "." or "..")
            name = "download";
        if (name.Length > 120)
            name = name[..120];
        return name;
    }
}

/// <summary>
/// The in-flight cap batch downloads respect (R4 #134): at most
/// <see cref="MaxConcurrentDownloads"/> transfers run at once, whatever
/// their source (page, retry, batch, schedule).
/// </summary>
public static class DownloadConcurrency
{
    public const int MaxConcurrentDownloads = 4;

    /// <summary>How many queued downloads may start now, given how many are already running.</summary>
    public static int StartableNow(int activeCount, int queuedCount, int maxConcurrent = MaxConcurrentDownloads)
    {
        if (maxConcurrent < 1)
            maxConcurrent = 1;
        if (activeCount < 0)
            activeCount = 0;
        if (queuedCount <= 0)
            return 0;
        return Math.Clamp(maxConcurrent - activeCount, 0, queuedCount);
    }
}

// ───────────────────────────── speed graph (#135) ─────────────────────────────

/// <summary>
/// The Downloads window's speed history (R4 #135): a bounded ring of
/// aggregate-speed samples, kept in memory and on this PC only — it is
/// never persisted or sent anywhere. The window polls the tracker's
/// total speed; this class owns the samples, the peak, and the
/// downsampling that turns them into chart points.
/// </summary>
public sealed class DownloadSpeedHistory
{
    public const int MaxSamples = 240;

    private readonly Queue<(DateTimeOffset At, double BytesPerSecond)> _samples = new();

    public int Count => _samples.Count;

    public IReadOnlyList<(DateTimeOffset At, double BytesPerSecond)> Samples => _samples.ToList();

    public double Peak => _samples.Count == 0 ? 0 : _samples.Max(s => s.BytesPerSecond);

    public void Add(DateTimeOffset at, double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond < 0)
            bytesPerSecond = 0;
        _samples.Enqueue((at, bytesPerSecond));
        while (_samples.Count > MaxSamples)
            _samples.Dequeue();
    }

    public void Clear() => _samples.Clear();

    /// <summary>
    /// Chart points normalized to the unit square: X spreads the
    /// samples evenly oldest→newest, Y is the speed relative to the
    /// peak (0 when nothing has moved). When there are more samples
    /// than <paramref name="maxPoints"/>, buckets are averaged so the
    /// line stays honest instead of aliased.
    /// </summary>
    public List<(double X, double Y)> NormalizedPoints(int maxPoints)
    {
        var points = new List<(double X, double Y)>();
        if (_samples.Count == 0 || maxPoints < 1)
            return points;
        var all = _samples.ToList();
        var peak = Peak;
        List<double> values;
        if (all.Count <= maxPoints)
        {
            values = all.Select(s => s.BytesPerSecond).ToList();
        }
        else
        {
            values = new List<double>();
            var bucketSize = (double)all.Count / maxPoints;
            for (var i = 0; i < maxPoints; i++)
            {
                var start = (int)(i * bucketSize);
                var end = Math.Min((int)((i + 1) * bucketSize), all.Count);
                if (end <= start)
                    end = start + 1;
                values.Add(all.Skip(start).Take(end - start).Average(s => s.BytesPerSecond));
            }
        }
        for (var i = 0; i < values.Count; i++)
        {
            var x = values.Count == 1 ? 0.5 : (double)i / (values.Count - 1);
            var y = peak > 0 ? Math.Clamp(values[i] / peak, 0, 1) : 0;
            points.Add((x, y));
        }
        return points;
    }
}

// ───────────────────────────── auto-retry (#139) ─────────────────────────────

/// <summary>
/// Download failure auto-retry (R4 #139): bounded, backing off, and
/// only for failures that plausibly fix themselves — network and
/// server-side interrupt reasons. Disk, permission, and user reasons
/// never retry (they would fail identically and spam). Attempt
/// counting is the shell's; this policy owns the bounds, the delay
/// curve, and the reason allowlist so all three are unit-tested.
/// The interrupt reason arrives as the WebView2 enum's NAME (Core
/// never references the WebView2 SDK).
/// </summary>
public static class DownloadRetryPolicy
{
    public const int DefaultMaxAttempts = 3;
    public const int MinMaxAttempts = 1;
    public const int MaxMaxAttempts = 5;

    private static readonly HashSet<string> RetryableReasons = new(StringComparer.Ordinal)
    {
        "NetworkFailed",
        "NetworkTimeout",
        "NetworkDisconnected",
        "ServerFailed",
        "ServerNoRange",
        "ServerContentLengthMismatch",
    };

    public static int SanitizeMaxAttempts(int attempts) =>
        Math.Clamp(attempts, MinMaxAttempts, MaxMaxAttempts);

    public static bool IsRetryableReason(string? reason) =>
        reason is not null && RetryableReasons.Contains(reason);

    /// <summary>
    /// The wait before retry number <paramref name="attempt"/> (1-based):
    /// 10 s, 30 s, 90 s, 270 s, capped at 5 minutes. Deterministic on
    /// purpose — jitter would make the behavior (and its tests) fuzzy.
    /// </summary>
    public static TimeSpan DelayForAttempt(int attempt)
    {
        if (attempt < 1)
            attempt = 1;
        var seconds = 10d;
        for (var i = 1; i < attempt; i++)
            seconds *= 3;
        return TimeSpan.FromSeconds(Math.Min(seconds, 300));
    }

    /// <summary>May this failure be retried automatically right now?</summary>
    public static bool ShouldRetry(bool enabled, string? reason, int attemptsSoFar, int maxAttempts) =>
        enabled
        && attemptsSoFar < SanitizeMaxAttempts(maxAttempts)
        && IsRetryableReason(reason);
}

// ───────────────────────────── per-profile quotas (#140) ─────────────────────────────

/// <summary>Where a profile's downloads folder stands against its soft cap (R4 #140).</summary>
public enum DownloadQuotaLevel
{
    NoCap,
    Ok,
    Warning,
    Full,
    Over,
}

/// <summary>A quota reading: the level, and the numbers behind it (R4 #140).</summary>
public sealed record DownloadQuotaStatus(DownloadQuotaLevel Level, long UsedBytes, long QuotaBytes)
{
    public double FractionUsed => QuotaBytes > 0 ? (double)UsedBytes / QuotaBytes : 0;
}

/// <summary>
/// Per-profile download quotas (R4 #140): a SOFT cap on a profile's
/// downloads folder — a warning at 80%, another at 100% — that never
/// blocks a download and never deletes anything by itself. Usage is
/// the profile-data walk (<see cref="ProfileDataInfo.MeasureFolderBytes"/>),
/// so a vanished or locked file mid-measure is skipped, never fatal.
/// </summary>
public static class DownloadQuota
{
    public const double WarningFraction = 0.8;

    public static DownloadQuotaStatus Evaluate(long usedBytes, long quotaBytes)
    {
        if (quotaBytes <= 0)
            return new DownloadQuotaStatus(DownloadQuotaLevel.NoCap, Math.Max(0, usedBytes), 0);
        if (usedBytes < 0)
            usedBytes = 0;
        var fraction = (double)usedBytes / quotaBytes;
        var level = fraction > 1
            ? DownloadQuotaLevel.Over
            : fraction >= 1
                ? DownloadQuotaLevel.Full
                : fraction >= WarningFraction
                    ? DownloadQuotaLevel.Warning
                    : DownloadQuotaLevel.Ok;
        return new DownloadQuotaStatus(level, usedBytes, quotaBytes);
    }

    public static DownloadQuotaStatus ForFolder(string folder, long quotaBytes) =>
        Evaluate(ProfileDataInfo.MeasureFolderBytes(folder) ?? 0, quotaBytes);

    /// <summary>The user-facing one-liner for a status worth surfacing; null for Ok/NoCap.</summary>
    public static string? Describe(DownloadQuotaStatus status) => status.Level switch
    {
        DownloadQuotaLevel.Warning =>
            $"Downloads for this profile are at {(int)(status.FractionUsed * 100)}% of its {DownloadText.FormatBytes(status.QuotaBytes)} soft cap ({DownloadText.FormatBytes(status.UsedBytes)} used). Nothing is blocked or deleted — this is a heads-up only.",
        DownloadQuotaLevel.Full or DownloadQuotaLevel.Over =>
            $"Downloads for this profile have reached its {DownloadText.FormatBytes(status.QuotaBytes)} soft cap ({DownloadText.FormatBytes(status.UsedBytes)} used). Downloads still work and nothing was deleted — archive or move files when you're ready.",
        _ => null,
    };
}
