using System.Text.Json;

namespace MuseDesktop.Core;

// ───────────────────────────── avatars (#121) ─────────────────────────────

/// <summary>
/// Profile avatars (R3 #121): a fixed text glyph (the workspace glyph
/// palette — one palette for the whole app, so a mark renders the same
/// in the switcher, menus, tray, and palette) over the profile's accent
/// color. There are no avatar images and nothing to upload: the mark is
/// text, the background is the color, and the desktop-shortcut icon is
/// drawn from exactly this spec.
/// </summary>
public static class ProfileAvatar
{
    /// <summary>The background the shell paints when a profile has no accent color (the app's own red).</summary>
    public const string DefaultBackgroundHex = "#E50914";

    /// <summary>The avatar's symbol: the profile's glyph symbol, or null when it has no glyph.</summary>
    public static string? SymbolFor(AccountProfile profile) =>
        WorkspaceGlyphs.SymbolFor(profile.AvatarGlyph);

    /// <summary>
    /// Everything the shell needs to draw the avatar (and the shortcut
    /// icon): the background hex (the profile color, else the app red)
    /// and the mark — the glyph symbol when the profile has one, else
    /// the first letter of its name.
    /// </summary>
    public static (string BackgroundHex, string Mark) IconSpec(AccountProfile profile)
    {
        var hex = ProfileColors.HexFor(profile.DisplayColor) ?? DefaultBackgroundHex;
        if (SymbolFor(profile) is { } symbol)
            return (hex, symbol);
        var name = (profile.Name ?? "").Trim();
        return (hex, name.Length > 0 ? name[..1].ToUpperInvariant() : "?");
    }
}

// ───────────────────────── per-profile lock (#122) ─────────────────────────

/// <summary>
/// Per-profile lock (R3 #122): an optional separate PIN per profile,
/// using exactly the app lock's design — the same salted PBKDF2 record
/// (<see cref="AppLockRecord"/>), the same cost, the same verification,
/// the same fail-open repair — stored on the profile instead of the
/// settings root. The GLOBAL app lock still gates first; a profile PIN
/// gates launching that one profile. Like the app lock this is a
/// privacy screen, not encryption: it stops a casual glance, not a
/// determined reader of the data folder (see KNOWN_LIMITATIONS).
/// </summary>
public static class ProfileLock
{
    /// <summary>The profile's parsed PIN record, when its stored fields form a valid one.</summary>
    public static bool TryGetRecord(AccountProfile profile, out AppLockRecord? record) =>
        AppLock.TryParseRecord(profile.LockSalt, profile.LockHash, profile.LockIterations, out record);

    /// <summary>True when the profile carries a stored record that parses (whatever the on/off flag says).</summary>
    public static bool HasValidRecord(AccountProfile profile) => TryGetRecord(profile, out _);

    /// <summary>The lock the shell enforces: the switch is on AND a valid record exists.</summary>
    public static bool IsLockActive(AccountProfile profile) =>
        profile.LockEnabled && HasValidRecord(profile);

    /// <summary>Check a PIN attempt against the profile's record. No valid record = never verifies.</summary>
    public static bool Verify(string? pin, AccountProfile profile) =>
        TryGetRecord(profile, out var record) && record is not null && AppLock.Verify(pin, record);

    /// <summary>Set (or replace) the profile's PIN and turn its lock on. Caller validates length first (AppLock.IsCredentialLengthValid).</summary>
    public static void SetCredential(AccountProfile profile, string pin)
    {
        var record = AppLock.CreateRecord(pin);
        profile.LockSalt = record.SaltBase64;
        profile.LockHash = record.HashBase64;
        profile.LockIterations = record.Iterations;
        profile.LockEnabled = true;
    }

    /// <summary>Remove the profile's PIN entirely (record and switch).</summary>
    public static void RemoveCredential(AccountProfile profile)
    {
        profile.LockEnabled = false;
        profile.LockSalt = null;
        profile.LockHash = null;
        profile.LockIterations = 0;
    }

    /// <summary>
    /// Fail-open repair (called from Profiles.EnsureValid): an enabled
    /// lock whose record no longer parses turns OFF. Returns true when
    /// a repair happened. Mirrors the app lock's rule — a corrupt
    /// record must never lock the owner out of their own profile.
    /// </summary>
    public static bool Repair(AccountProfile profile)
    {
        if (!profile.LockEnabled || HasValidRecord(profile))
            return false;
        RemoveCredential(profile);
        return true;
    }
}

// ───────────────────────────── groups (#127) ─────────────────────────────

/// <summary>A named profile group (R3 #127): pure organization for the switchers — a name and the profiles that carry its id.</summary>
public sealed class ProfileGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// Profile groups (R3 #127): named folders in the switcher for users
/// near the 32-profile cap. A group owns nothing — deleting one just
/// ungroups its members — and "switch to group" means "switch to the
/// group's most recently used profile", the one the user most likely
/// means.
/// </summary>
public static class ProfileGroups
{
    public const int MaxGroups = 16;
    public const int MaxNameLength = 40;

    /// <summary>A fresh unique-ish id for a new group.</summary>
    public static string NewId() => "g" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>
    /// Repair the group list and memberships in place: valid unique
    /// ids, non-empty capped names, the cap enforced, and every
    /// profile's GroupId pointing at a group that exists (a dangling
    /// id clears to ungrouped). Called from AppSettings.Sanitized.
    /// </summary>
    public static void EnsureValid(AppSettings settings)
    {
        settings.ProfileGroups ??= new List<ProfileGroup>();
        var seen = new HashSet<string>();
        settings.ProfileGroups.RemoveAll(g =>
            g is null || !Profiles.IsValidId(g.Id) || !seen.Add(g.Id));
        foreach (var g in settings.ProfileGroups)
        {
            g.Name = (g.Name ?? "").Trim();
            if (g.Name.Length == 0)
                g.Name = "Group";
            if (g.Name.Length > MaxNameLength)
                g.Name = g.Name[..MaxNameLength];
        }
        while (settings.ProfileGroups.Count > MaxGroups)
            settings.ProfileGroups.RemoveAt(settings.ProfileGroups.Count - 1);
        var live = new HashSet<string>(settings.ProfileGroups.Select(g => g.Id));
        foreach (var p in settings.Profiles)
            if (p.GroupId is not null && !live.Contains(p.GroupId))
                p.GroupId = null;
    }

    /// <summary>The next free default group name: "Group", "Group 2", …</summary>
    public static string DefaultName(AppSettings settings)
    {
        var taken = new HashSet<string>(settings.ProfileGroups.Select(g => g.Name),
            StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains("Group"))
            return "Group";
        for (var n = 2; ; n++)
            if (!taken.Contains($"Group {n}"))
                return $"Group {n}";
    }

    /// <summary>Add a group; null name/whitespace takes the next default name. Returns null at the group cap.</summary>
    public static ProfileGroup? Add(AppSettings settings, string? name)
    {
        if (settings.ProfileGroups.Count >= MaxGroups)
            return null;
        var group = new ProfileGroup
        {
            Id = NewId(),
            Name = string.IsNullOrWhiteSpace(name) ? DefaultName(settings) : name.Trim(),
        };
        if (group.Name.Length > MaxNameLength)
            group.Name = group.Name[..MaxNameLength];
        settings.ProfileGroups.Add(group);
        return group;
    }

    /// <summary>Remove a group; its members become ungrouped (nothing else about them changes). False when no such group.</summary>
    public static bool Remove(AppSettings settings, string groupId)
    {
        var group = settings.ProfileGroups.FirstOrDefault(g => g.Id == groupId);
        if (group is null)
            return false;
        settings.ProfileGroups.Remove(group);
        foreach (var p in settings.Profiles)
            if (p.GroupId == groupId)
                p.GroupId = null;
        return true;
    }

    /// <summary>Every member of the group, in profile order (archived members included — Settings manages them too).</summary>
    public static IReadOnlyList<AccountProfile> Members(AppSettings settings, string groupId) =>
        settings.Profiles.Where(p => p.GroupId == groupId).ToList();

    /// <summary>The members switchers may show: the group's non-archived profiles, in profile order.</summary>
    public static IReadOnlyList<AccountProfile> VisibleMembers(AppSettings settings, string groupId) =>
        settings.Profiles.Where(p => p.GroupId == groupId && !p.Archived).ToList();

    /// <summary>
    /// The profile a "switch to this group" action opens: the group's
    /// most recently used VISIBLE member, else its first visible
    /// member, else null (an empty or fully archived group switches
    /// nowhere).
    /// </summary>
    public static AccountProfile? SwitchTarget(AppSettings settings, string groupId)
    {
        var members = VisibleMembers(settings, groupId);
        if (members.Count == 0)
            return null;
        return members
            .OrderByDescending(p => p.LastUsedUtc ?? DateTimeOffset.MinValue)
            .First();
    }
}

// ──────────────────────────── templates (#128) ────────────────────────────

/// <summary>A starter template for quick profile creation (R3 #128): a name, a look, and a start-page choice — nothing else is preset.</summary>
public sealed record ProfileTemplate(
    string Key, string Name, string Description,
    string? DisplayColor, string? AvatarGlyph,
    StartPageMode StartPage, double? ZoomFactor);

/// <summary>
/// The built-in profile templates (R3 #128): Work, Personal, Reading.
/// A template is a create-time shortcut only — the profile it makes is
/// an ordinary profile with ordinary settings, fully editable after.
/// </summary>
public static class ProfileTemplates
{
    public static readonly IReadOnlyList<ProfileTemplate> All = new[]
    {
        new ProfileTemplate("work", "Work",
            "Blue flag, starts at the Muse home page.",
            "Blue", "Flag", StartPageMode.Home, null),
        new ProfileTemplate("personal", "Personal",
            "Purple heart, reopens the last page.",
            "Purple", "Heart", StartPageMode.LastPage, null),
        new ProfileTemplate("reading", "Reading",
            "Orange moon, reopens the last page at 125% zoom.",
            "Orange", "Moon", StartPageMode.LastPage, 1.25),
    };

    /// <summary>The template for a key (case-insensitive), or null.</summary>
    public static ProfileTemplate? Find(string? key) =>
        key is null ? null
        : All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Create a profile from a template, appended to the list, under
    /// the template's name (or an override) deduplicated like a clone
    /// name ("Work", "Work 2", …). Null for an unknown key or at the
    /// profile cap.
    /// </summary>
    public static AccountProfile? Create(AppSettings settings, string key, string? nameOverride = null)
    {
        var template = Find(key);
        if (template is null || settings.Profiles.Count >= Profiles.MaxProfiles)
            return null;
        var wanted = string.IsNullOrWhiteSpace(nameOverride) ? template.Name : nameOverride.Trim();
        var taken = new HashSet<string>(settings.Profiles.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);
        var stem = wanted.Length > 36 ? wanted[..36].TrimEnd() : wanted;
        var name = wanted;
        for (var n = 2; taken.Contains(name); n++)
            name = $"{stem} {n}";
        var profile = new AccountProfile
        {
            Id = Profiles.NewId(),
            Name = name,
            DisplayColor = template.DisplayColor,
            AvatarGlyph = template.AvatarGlyph,
            StartPage = template.StartPage,
            StartAtHome = template.StartPage == StartPageMode.Home,
            ZoomFactor = template.ZoomFactor,
        };
        settings.Profiles.Add(profile);
        return profile;
    }
}

// ─────────────────────── scheduled switching (#123) ───────────────────────

/// <summary>
/// One time-of-day switch rule (R3 #123): during the window, the
/// schedule wants this profile active. Days are a bit mask (Mon=1 …
/// Sun=64) of the days the window STARTS on; a window whose end is at
/// or before its start runs past midnight into the next day. Times are
/// minutes after midnight, local wall clock.
/// </summary>
public sealed class ProfileSwitchRule
{
    public string ProfileId { get; set; } = "";
    public int DaysMask { get; set; }
    public int StartMinutes { get; set; }
    public int EndMinutes { get; set; }
}

/// <summary>What the schedule thinks should happen right now (R3 #123).</summary>
public sealed record ScheduleDecision(bool ShouldSwitch, string? ProfileId)
{
    public static readonly ScheduleDecision None = new(false, null);
}

/// <summary>
/// Scheduled profile switching (R3 #123). The rules are evaluated
/// against a caller-supplied "now" (the AppLockThrottle pattern — the
/// shell passes the local clock; tests pass fixed times), so every
/// decision here is deterministic. Two principles own the design:
/// ASK FIRST is the default (the shell proposes; the user decides),
/// and MANUAL OVERRIDE WINS — choosing a profile by hand, or declining
/// a suggestion, records a hold the schedule honors until the current
/// window ends or the next one begins.
/// </summary>
public static class ProfileSchedule
{
    public const int WeekdaysMask = 1 | 2 | 4 | 8 | 16;
    public const int WeekendMask = 32 | 64;
    public const int AllDaysMask = 127;

    /// <summary>The mask bit for a day of the week (Mon=1 … Sun=64).</summary>
    public static int MaskFor(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => 1,
        DayOfWeek.Tuesday => 2,
        DayOfWeek.Wednesday => 4,
        DayOfWeek.Thursday => 8,
        DayOfWeek.Friday => 16,
        DayOfWeek.Saturday => 32,
        _ => 64, // Sunday
    };

    private static bool IsValidTime(int minutes) => minutes is >= 0 and <= 1439;

    /// <summary>A rule that can ever fire: real days, real times, a non-empty window.</summary>
    public static bool IsValid(ProfileSwitchRule rule) =>
        Profiles.IsValidId(rule.ProfileId)
        && rule.DaysMask is >= 1 and <= AllDaysMask
        && IsValidTime(rule.StartMinutes) && IsValidTime(rule.EndMinutes)
        && rule.StartMinutes != rule.EndMinutes;

    /// <summary>
    /// Repair the rule list and the hold: invalid rules, rules naming
    /// missing or archived profiles, and duplicates (first per profile
    /// wins) drop; a hold naming a missing profile (or missing its end
    /// time) clears. Called from AppSettings.Sanitized.
    /// </summary>
    public static void Sanitize(AppSettings settings)
    {
        settings.ProfileSwitchRules ??= new List<ProfileSwitchRule>();
        var live = new HashSet<string>(
            settings.Profiles.Where(p => !p.Archived).Select(p => p.Id));
        var seen = new HashSet<string>();
        settings.ProfileSwitchRules.RemoveAll(r =>
            r is null || !IsValid(r) || !live.Contains(r.ProfileId) || !seen.Add(r.ProfileId));
        if (settings.ScheduleHoldProfileId is { } holdId
            && (!Profiles.IsValidId(holdId)
                || settings.Profiles.All(p => p.Id != holdId)
                || settings.ScheduleHoldUntilUtc is null))
        {
            settings.ScheduleHoldProfileId = null;
            settings.ScheduleHoldUntilUtc = null;
        }
        if (settings.ScheduleHoldUntilUtc is not null && settings.ScheduleHoldProfileId is null)
            settings.ScheduleHoldUntilUtc = null;
    }

    /// <summary>Set (or replace) the rule for a profile. Caller sanitizes after.</summary>
    public static void SetRule(AppSettings settings, ProfileSwitchRule rule)
    {
        settings.ProfileSwitchRules.RemoveAll(r => r.ProfileId == rule.ProfileId);
        settings.ProfileSwitchRules.Add(rule);
    }

    /// <summary>Remove a profile's rule, if it has one.</summary>
    public static void RemoveRule(AppSettings settings, string profileId) =>
        settings.ProfileSwitchRules.RemoveAll(r => r.ProfileId == profileId);

    /// <summary>The rule for a profile, or null.</summary>
    public static ProfileSwitchRule? RuleFor(AppSettings settings, string profileId) =>
        settings.ProfileSwitchRules.FirstOrDefault(r => r.ProfileId == profileId);

    /// <summary>
    /// Is <paramref name="localNow"/> inside the rule's window? The
    /// day mask names the day the window STARTS: a 22:00–06:00 rule
    /// masked for Friday covers Friday night into Saturday morning.
    /// </summary>
    public static bool Contains(ProfileSwitchRule rule, DateTimeOffset localNow)
    {
        var minutes = localNow.Hour * 60 + localNow.Minute;
        if (rule.EndMinutes > rule.StartMinutes)
            return (rule.DaysMask & MaskFor(localNow.DayOfWeek)) != 0
                && minutes >= rule.StartMinutes && minutes < rule.EndMinutes;
        // Wrapping window (end at/before start; equal is invalid and
        // never reaches here from a sanitized list).
        if ((rule.DaysMask & MaskFor(localNow.DayOfWeek)) != 0 && minutes >= rule.StartMinutes)
            return true;
        var yesterday = localNow.AddDays(-1).DayOfWeek;
        return (rule.DaysMask & MaskFor(yesterday)) != 0 && minutes < rule.EndMinutes;
    }

    /// <summary>When the window containing <paramref name="localNow"/> ends (call only when Contains is true).</summary>
    public static DateTimeOffset WindowEnd(ProfileSwitchRule rule, DateTimeOffset localNow)
    {
        var minutes = localNow.Hour * 60 + localNow.Minute;
        if (rule.EndMinutes > rule.StartMinutes)
            return AtMinutes(localNow, 0, rule.EndMinutes);
        // Wrapping: the evening part ends tomorrow; the small-hours
        // part (started yesterday) ends today.
        return minutes >= rule.StartMinutes
            ? AtMinutes(localNow, 1, rule.EndMinutes)
            : AtMinutes(localNow, 0, rule.EndMinutes);
    }

    /// <summary>The next time the rule's window starts, strictly after <paramref name="localNow"/> (within 8 days; null only for an invalid mask).</summary>
    public static DateTimeOffset? NextWindowStart(ProfileSwitchRule rule, DateTimeOffset localNow)
    {
        for (var d = 0; d <= 7; d++)
        {
            var day = localNow.AddDays(d);
            if ((rule.DaysMask & MaskFor(day.DayOfWeek)) == 0)
                continue;
            var start = AtMinutes(localNow, d, rule.StartMinutes);
            if (start > localNow)
                return start;
        }
        return null;
    }

    private static DateTimeOffset AtMinutes(DateTimeOffset anchor, int addDays, int minutes) =>
        new(anchor.Date.AddDays(addDays).AddMinutes(minutes), anchor.Offset);

    /// <summary>The first rule (list order) whose window contains now and whose profile exists and is not archived; null when no window is running.</summary>
    public static ProfileSwitchRule? ActiveRule(AppSettings settings, DateTimeOffset localNow) =>
        settings.ProfileSwitchRules.FirstOrDefault(r =>
            IsValid(r)
            && settings.Profiles.Any(p => p.Id == r.ProfileId && !p.Archived)
            && Contains(r, localNow));

    /// <summary>
    /// The schedule's verdict for right now: switch to the active
    /// window's profile — unless that profile is already active, no
    /// window is running, or the manual hold covers the active
    /// profile (the user chose it by hand; their choice wins until
    /// the hold ends).
    /// </summary>
    public static ScheduleDecision Evaluate(AppSettings settings, DateTimeOffset localNow, string activeProfileId)
    {
        if (settings.ScheduleHoldProfileId == activeProfileId
            && settings.ScheduleHoldUntilUtc is { } until
            && localNow < until)
            return ScheduleDecision.None;
        var rule = ActiveRule(settings, localNow);
        if (rule is null || rule.ProfileId == activeProfileId)
            return ScheduleDecision.None;
        return new ScheduleDecision(true, rule.ProfileId);
    }

    /// <summary>
    /// Record a manual choice (R3 #123): the user switched to — or
    /// declined a suggestion in favor of — <paramref name="chosenProfileId"/>.
    /// The hold lasts until the currently running window ends, or
    /// until the next window starts when none is running (so one
    /// manual choice silences the schedule for the rest of this
    /// window, not forever), capped at a week.
    /// </summary>
    public static void RecordManualSwitch(AppSettings settings, string chosenProfileId, DateTimeOffset localNow)
    {
        if (settings.Profiles.All(p => p.Id != chosenProfileId))
        {
            ClearHold(settings);
            return;
        }
        DateTimeOffset until;
        if (ActiveRule(settings, localNow) is { } active)
        {
            until = WindowEnd(active, localNow);
        }
        else
        {
            DateTimeOffset? next = null;
            foreach (var rule in settings.ProfileSwitchRules)
            {
                if (!IsValid(rule)
                    || settings.Profiles.All(p => p.Id != rule.ProfileId || p.Archived))
                    continue;
                if (NextWindowStart(rule, localNow) is { } start && (next is null || start < next))
                    next = start;
            }
            until = next ?? localNow.AddHours(1);
        }
        var cap = localNow.AddDays(7);
        settings.ScheduleHoldProfileId = chosenProfileId;
        settings.ScheduleHoldUntilUtc = until > cap ? cap : until;
    }

    /// <summary>Drop the manual hold (the schedule rules again).</summary>
    public static void ClearHold(AppSettings settings)
    {
        settings.ScheduleHoldProfileId = null;
        settings.ScheduleHoldUntilUtc = null;
    }

    /// <summary>"HH:mm" for a minutes-of-day value.</summary>
    public static string FormatMinutes(int minutes) => $"{minutes / 60:D2}:{minutes % 60:D2}";

    /// <summary>Parse an "HH:mm" (or "H:mm") time into minutes-of-day; null when it isn't one.</summary>
    public static int? ParseMinutes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var h)
            || !int.TryParse(parts[1], out var m)
            || h is < 0 or > 23 || m is < 0 or > 59)
            return null;
        return h * 60 + m;
    }

    /// <summary>The rule in words: "09:00–17:00 · Weekdays" (plus "past midnight" for a wrapping window).</summary>
    public static string Describe(ProfileSwitchRule rule)
    {
        var days = rule.DaysMask switch
        {
            AllDaysMask => "Every day",
            WeekdaysMask => "Weekdays",
            WeekendMask => "Weekends",
            _ => string.Join(", ", OrderedDays(rule.DaysMask)),
        };
        var text = $"{FormatMinutes(rule.StartMinutes)}–{FormatMinutes(rule.EndMinutes)} · {days}";
        return rule.EndMinutes < rule.StartMinutes ? text + " · past midnight" : text;
    }

    private static IEnumerable<string> OrderedDays(int mask)
    {
        (DayOfWeek Day, string Name)[] order =
        {
            (DayOfWeek.Monday, "Mon"), (DayOfWeek.Tuesday, "Tue"),
            (DayOfWeek.Wednesday, "Wed"), (DayOfWeek.Thursday, "Thu"),
            (DayOfWeek.Friday, "Fri"), (DayOfWeek.Saturday, "Sat"),
            (DayOfWeek.Sunday, "Sun"),
        };
        foreach (var (day, name) in order)
            if ((mask & MaskFor(day)) != 0)
                yield return name;
    }
}

// ─────────────────────────── health view (#125) ───────────────────────────

/// <summary>
/// The measured facts the health view is built from (R3 #125),
/// gathered by the shell from its providers (folder walk, cache plan,
/// cookie summary, backup catalog). A null fact means "couldn't be
/// measured" and renders as exactly that — never as zero.
/// </summary>
public sealed record ProfileHealthFacts(
    long? DataBytes, long? CacheBytes, int? CookieCount, DateTimeOffset? LastBackupUtc);

/// <summary>One labelled line of the health report.</summary>
public sealed record ProfileHealthLine(string Label, string Value);

/// <summary>The profile health report (R3 #125): labelled lines plus a one-line summary for the Settings text.</summary>
public sealed record ProfileHealthReport(IReadOnlyList<ProfileHealthLine> Lines, string Summary);

/// <summary>
/// The profile health view model (R3 #125): data size, disposable
/// cache size, cookie count, last-used, last settings backup, data
/// cap — COUNTS ONLY, by construction. This type never sees a cookie
/// name, a domain, a file path, or any page data; the shell's
/// providers reduce everything to numbers before it gets here.
/// </summary>
public static class ProfileHealth
{
    public static ProfileHealthReport Build(AccountProfile profile, ProfileHealthFacts facts)
    {
        var lines = new List<ProfileHealthLine>
        {
            new("Data on this PC",
                facts.DataBytes is { } data ? DownloadText.FormatBytes(data) : "No data on this PC yet"),
            new("Disposable cache",
                facts.CacheBytes is { } cache ? DownloadText.FormatBytes(cache) : "Couldn't be measured"),
            new("Cookies",
                facts.CookieCount is { } cookies ? cookies.ToString("N0") : "Couldn't be counted right now"),
            new("Last used",
                profile.LastUsedUtc is { } used ? used.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "Never"),
            new("Last settings backup",
                facts.LastBackupUtc is { } backup ? backup.ToLocalTime().ToString("yyyy-MM-dd") : "No backup yet"),
            new("Data cap",
                profile.DataCapBytes > 0 ? DownloadText.FormatBytes(profile.DataCapBytes) : "Off"),
        };
        var summaryParts = new List<string>
        {
            facts.DataBytes is { } d ? $"{DownloadText.FormatBytes(d)} on this PC" : "No data on this PC yet",
        };
        if (facts.CookieCount is { } c)
            summaryParts.Add($"{c.ToString("N0")} cookies");
        summaryParts.Add(profile.LastUsedUtc is { } u
            ? $"last used {u.ToLocalTime():yyyy-MM-dd}"
            : "never used");
        return new ProfileHealthReport(lines, string.Join(" · ", summaryParts));
    }
}

// ───────────────────────── export / import (#126) ─────────────────────────

/// <summary>
/// Single-profile export/import (R3 #126): one profile's SETTINGS +
/// APPEARANCE in a small self-describing file. What a bundle can never
/// contain, by construction: sign-in data (cookies, sessions — those
/// live in the profile's WebView2 folder, which this code never
/// reads), the profile's PIN record, its schedule rule, its group
/// membership, its usage counts, and its archived state. Import always
/// assigns a FRESH profile id — importing never overwrites or merges
/// into an existing profile.
///
/// The plain form opens with a magic line so the file identifies
/// itself — and so it can never be mistaken for a settings export:
/// the settings importer rejects it instead of importing defaults.
/// The encrypted form reuses the S7 envelope (EncryptedExport) around
/// that same plain text; the bundle kind lives INSIDE the payload,
/// and the settings importer still can't read it (its payload isn't
/// settings JSON).
/// </summary>
public static class ProfileBundle
{
    public const string Kind = "MuseDesktopProfile";
    public const int BundleVersion = 1;
    public const string MagicLine = "MUSEDESKTOP-PROFILE";

    /// <summary>The honest one-liner the UI shows beside the export/import buttons.</summary>
    public const string ContentsNote =
        "A profile bundle carries this profile's settings and appearance only — never its sign-in data, cookies, PIN, schedule, or usage counts.";

    private sealed class Envelope
    {
        public string Kind { get; set; } = ProfileBundle.Kind;
        public int Version { get; set; } = ProfileBundle.BundleVersion;
        public AccountProfile? Profile { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>True when the text is an encrypted export (of anything) — a bundle import then needs its password.</summary>
    public static bool NeedsPassword(string? text) => EncryptedExport.IsEncrypted(text);

    /// <summary>Serialize a profile bundle; encrypts with the S7 envelope when a non-empty password is given.</summary>
    public static string Export(AccountProfile source, string? password)
    {
        // The carried copy is built field by field — the exclusion
        // list is visible in exactly one place, and a field added to
        // AccountProfile later does NOT start travelling by accident.
        var carried = new AccountProfile
        {
            Id = source.Id, // informational only — import assigns a fresh id
            Name = source.Name,
            StartAtHome = source.StartAtHome,
            StartPage = source.StartPage,
            CustomStartUrl = source.CustomStartUrl,
            DownloadsFolder = source.DownloadsFolder,
            DisplayColor = source.DisplayColor,
            AvatarGlyph = source.AvatarGlyph,
            ZoomFactor = source.ZoomFactor,
            DataCapBytes = source.DataCapBytes,
        };
        var envelope = new Envelope { Profile = carried };
        var text = MagicLine + "\n" + JsonSerializer.Serialize(envelope, Options);
        return string.IsNullOrEmpty(password) ? text : EncryptedExport.Encrypt(text, password);
    }

    /// <summary>
    /// Read a bundle back into a NEW profile (fresh id; fields
    /// sanitized by the same repairs a settings load applies). Errors
    /// are plain words for the user.
    /// </summary>
    public static bool TryImport(string text, string? password,
        out AccountProfile? profile, out string? error, Func<string>? idFactory = null)
    {
        profile = null;
        error = null;
        var plain = text;
        if (EncryptedExport.IsEncrypted(text))
        {
            if (string.IsNullOrEmpty(password))
            {
                error = "That bundle is encrypted — enter its password to import it.";
                return false;
            }
            if (!EncryptedExport.TryDecrypt(text, password, out var decrypted, out _, out var message)
                || decrypted is null)
            {
                error = message ?? "That bundle couldn't be decrypted.";
                return false;
            }
            plain = decrypted;
        }
        var trimmed = plain.TrimStart();
        if (!trimmed.StartsWith(MagicLine, StringComparison.Ordinal))
        {
            error = "That file isn't a MuseDesktop profile bundle.";
            return false;
        }
        var json = trimmed[MagicLine.Length..].Trim();
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(json, Options);
        }
        catch (Exception)
        {
            error = "That profile bundle is damaged and couldn't be read.";
            return false;
        }
        if (envelope is null || envelope.Profile is null)
        {
            error = "That profile bundle is damaged and couldn't be read.";
            return false;
        }
        if (!string.Equals(envelope.Kind, Kind, StringComparison.Ordinal))
        {
            error = "That file isn't a MuseDesktop profile bundle.";
            return false;
        }
        if (envelope.Version > BundleVersion)
        {
            error = "That bundle was made by a newer version of MuseDesktop.";
            return false;
        }
        if (envelope.Version < 1)
        {
            error = "That profile bundle is damaged and couldn't be read.";
            return false;
        }
        var source = envelope.Profile;
        var imported = new AccountProfile
        {
            Id = (idFactory ?? Profiles.NewId)(),
            Name = source.Name,
            StartAtHome = source.StartAtHome,
            StartPage = source.StartPage,
            CustomStartUrl = source.CustomStartUrl,
            DownloadsFolder = source.DownloadsFolder,
            DisplayColor = source.DisplayColor,
            AvatarGlyph = source.AvatarGlyph,
            ZoomFactor = source.ZoomFactor,
            DataCapBytes = source.DataCapBytes,
        };
        // Sanitize through the same repairs a settings load applies
        // (name cap, color/glyph must be palette keys, zoom clamp,
        // start-page sync, cap must be an offered size).
        var repair = new AppSettings
        {
            Profiles = new List<AccountProfile> { imported },
            ActiveProfileId = imported.Id,
        };
        Profiles.EnsureValid(repair);
        profile = imported;
        return true;
    }
}

// ──────────────────────────── usage view (#129) ───────────────────────────

/// <summary>Launch tallies for one profile (R3 #129): how many times the app launched with it, and when last.</summary>
public sealed class ProfileUsageEntry
{
    public int Launches { get; set; }
    public DateTimeOffset? LastLaunchUtc { get; set; }
}

/// <summary>
/// Profile usage tallies (R3 #129): LOCAL-ONLY counts in their own
/// file beside settings — never in settings itself, so they never
/// ride a settings export/import or a profile bundle. Recording is
/// best-effort (a failed tally never blocks a launch), and the whole
/// file deletes in one click from Settings.
/// </summary>
public sealed class ProfileUsage
{
    public Dictionary<string, ProfileUsageEntry> ByProfile { get; set; } = new();

    /// <summary>Total launches across every recorded profile.</summary>
    public long TotalLaunches => ByProfile.Values.Sum(e => (long)e.Launches);

    public void RecordLaunch(string profileId, DateTimeOffset utcNow)
    {
        if (!ByProfile.TryGetValue(profileId, out var entry))
        {
            entry = new ProfileUsageEntry();
            ByProfile[profileId] = entry;
        }
        entry.Launches++;
        entry.LastLaunchUtc = utcNow;
    }

    public ProfileUsageEntry? EntryFor(string profileId) =>
        ByProfile.TryGetValue(profileId, out var entry) ? entry : null;
}

/// <summary>The profile-usage.json store: same atomic-save, fail-soft-load pattern as the session snapshot store.</summary>
public static class ProfileUsageStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Load the tallies; a missing or damaged file is simply "no counts yet".</summary>
    public static ProfileUsage Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ProfileUsage();
            var usage = JsonSerializer.Deserialize<ProfileUsage>(File.ReadAllText(path), Options)
                ?? new ProfileUsage();
            usage.ByProfile ??= new Dictionary<string, ProfileUsageEntry>();
            // Repair: entries for impossible ids or with negative
            // counts are dropped/clamped — a hand-edited file can
            // never produce a nonsense report.
            var bad = usage.ByProfile
                .Where(kv => !Profiles.IsValidId(kv.Key) || kv.Value is null)
                .Select(kv => kv.Key).ToList();
            foreach (var key in bad)
                usage.ByProfile.Remove(key);
            foreach (var entry in usage.ByProfile.Values)
                if (entry.Launches < 0)
                    entry.Launches = 0;
            return usage;
        }
        catch (Exception)
        {
            return new ProfileUsage();
        }
    }

    public static void Save(string path, ProfileUsage usage)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(usage, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Delete the tallies file ("clear usage counts"): gone is gone — there is no other copy.</summary>
    public static void Clear(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort — Settings reports counts from a fresh load,
            // so a failed delete shows up honestly as counts remaining.
        }
    }
}

/// <summary>One row of the usage view: a current profile, its launch count, and when it was last launched with.</summary>
public sealed record ProfileUsageRow(string ProfileId, string Name, int Launches, DateTimeOffset? LastUsedUtc);

/// <summary>The usage report (R3 #129): one row per CURRENT profile (archived included — history is history), in profile order, plus the total.</summary>
public sealed record ProfileUsageReport(IReadOnlyList<ProfileUsageRow> Rows, long TotalLaunches)
{
    public static ProfileUsageReport Build(AppSettings settings, ProfileUsage usage)
    {
        var rows = settings.Profiles.Select(p =>
        {
            var entry = usage.EntryFor(p.Id);
            return new ProfileUsageRow(
                p.Id,
                p.Name,
                entry?.Launches ?? 0,
                // The tally's own last-launch stamp is the fresher
                // fact; the profile's LastUsedUtc is the fallback for
                // launches from before tallies existed.
                entry?.LastLaunchUtc ?? p.LastUsedUtc);
        }).ToList();
        return new ProfileUsageReport(rows, rows.Sum(r => (long)r.Launches));
    }
}
