using System.Text.Json;

namespace MuseDesktop.Core;

// ───────────────────── focus & productivity (R5 #141–#150) ─────────────────────
//
// Round 2's last slice. Everything here is LOCAL-ONLY by construction:
// the timers count minutes on this PC, the reading list stores app
// ROUTES (never a full page address — the AppRoute discipline from
// R2), notes and weekly tallies live in their own small files beside
// the settings, and nothing in this file can reach the network. Every
// clock reading arrives as a parameter (the AppLockThrottle pattern),
// so each state machine is deterministic and unit-tested; the shell
// passes DateTimeOffset.Now / UtcNow from its one-second timer.

// ─────────────────────── shared timer text ───────────────────────

/// <summary>The one countdown format both timers show (R5 #141/#142): "12:34" under an hour, "1:02:34" at or past it. Negative spans read as zero — a countdown never shows a minus.</summary>
public static class TimerText
{
    public static string Format(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;
        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    /// <summary>"2h 05m" / "45m" for totals in the digest and stats (R5 #146/#150).</summary>
    public static string FormatMinutes(int totalMinutes)
    {
        if (totalMinutes < 0)
            totalMinutes = 0;
        return totalMinutes >= 60
            ? $"{totalMinutes / 60}h {totalMinutes % 60:00}m"
            : $"{totalMinutes}m";
    }
}

// ─────────────────────── the focus timer (#141) ───────────────────────

/// <summary>Where the focus timer stands (R5 #141).</summary>
public enum FocusTimerState
{
    /// <summary>No block running.</summary>
    Idle,

    /// <summary>A focus block is counting down.</summary>
    Focusing,

    /// <summary>The block finished and the break reminder is showing. It is a state, not a dialog: dismissing it is one click and nothing is blocked meanwhile.</summary>
    BreakSuggested,

    /// <summary>The suggested break is counting down.</summary>
    OnBreak,
}

/// <summary>What one <see cref="FocusTimer.Tick"/> observed (R5 #141): completions are reported exactly once, on the tick that crosses the boundary.</summary>
public readonly record struct FocusTickResult(bool FocusJustCompleted, bool BreakJustCompleted);

/// <summary>
/// The focus-mode timer (R5 #141): one focus block, then a GENTLE
/// break suggestion. The reminder is state the shell renders as a
/// tray balloon + status line — dismissible, auto-expiring, never a
/// modal dialog, and gated by the ordinary notification policy
/// (quiet hours hold it back like every MuseDesktop balloon).
/// Completed blocks bank their full minutes; stopping early banks
/// the whole minutes actually focused, so the stats (R5 #150) count
/// real time, not intentions.
/// </summary>
public sealed class FocusTimer
{
    public const int MinFocusMinutes = 1;
    public const int MaxFocusMinutes = 240;
    public const int MinBreakMinutes = 1;
    public const int MaxBreakMinutes = 120;

    private DateTimeOffset _phaseEndsUtc;
    private DateTimeOffset _phaseStartedUtc;
    private int _phaseMinutes;

    public FocusTimerState State { get; private set; } = FocusTimerState.Idle;

    /// <summary>Full focus minutes banked by completed (and stopped) blocks since this timer was created. The shell drains this into the weekly stats.</summary>
    public int FocusMinutesBanked { get; private set; }

    public bool IsRunning => State is FocusTimerState.Focusing or FocusTimerState.OnBreak;

    /// <summary>The reminder is up (R5 #141): the focus block ended and the user hasn't taken or dismissed the break yet.</summary>
    public bool ReminderPending => State == FocusTimerState.BreakSuggested;

    /// <summary>Start a focus block of <paramref name="minutes"/> (clamped to 1–240). Starting again replaces any running block — the replaced block banks nothing.</summary>
    public void Start(DateTimeOffset now, int minutes)
    {
        _phaseMinutes = Math.Clamp(minutes, MinFocusMinutes, MaxFocusMinutes);
        _phaseStartedUtc = now;
        _phaseEndsUtc = now.AddMinutes(_phaseMinutes);
        State = FocusTimerState.Focusing;
    }

    /// <summary>Take the suggested break (from <see cref="FocusTimerState.BreakSuggested"/> only); its length clamps to 1–120 minutes.</summary>
    public bool StartBreak(DateTimeOffset now, int minutes)
    {
        if (State != FocusTimerState.BreakSuggested)
            return false;
        _phaseMinutes = Math.Clamp(minutes, MinBreakMinutes, MaxBreakMinutes);
        _phaseStartedUtc = now;
        _phaseEndsUtc = now.AddMinutes(_phaseMinutes);
        State = FocusTimerState.OnBreak;
        return true;
    }

    /// <summary>Dismiss the break suggestion (R5 #141): the reminder goes away and nothing else happens. No-op in any other state.</summary>
    public void DismissReminder()
    {
        if (State == FocusTimerState.BreakSuggested)
            State = FocusTimerState.Idle;
    }

    /// <summary>
    /// Stop whatever is running and return to Idle. A stopped focus
    /// block banks its whole elapsed minutes (and returns them); a
    /// stopped break banks nothing. Returns the minutes banked by
    /// THIS stop (0 when idle or on a break).
    /// </summary>
    public int Stop(DateTimeOffset now)
    {
        var banked = 0;
        if (State == FocusTimerState.Focusing)
        {
            banked = WholeMinutesBetween(_phaseStartedUtc, now);
            FocusMinutesBanked += banked;
        }
        State = FocusTimerState.Idle;
        return banked;
    }

    /// <summary>Advance the clock: cross a phase boundary exactly once, reporting what completed.</summary>
    public FocusTickResult Tick(DateTimeOffset now)
    {
        if (State == FocusTimerState.Focusing && now >= _phaseEndsUtc)
        {
            FocusMinutesBanked += _phaseMinutes;
            State = FocusTimerState.BreakSuggested;
            return new FocusTickResult(FocusJustCompleted: true, BreakJustCompleted: false);
        }
        if (State == FocusTimerState.OnBreak && now >= _phaseEndsUtc)
        {
            State = FocusTimerState.Idle;
            return new FocusTickResult(FocusJustCompleted: false, BreakJustCompleted: true);
        }
        return new FocusTickResult(false, false);
    }

    /// <summary>Time left in the running phase; zero when idle or when the reminder is up.</summary>
    public TimeSpan Remaining(DateTimeOffset now) =>
        IsRunning ? _phaseEndsUtc - now : TimeSpan.Zero;

    /// <summary>The status-bar line for the current state (R5 #141).</summary>
    public string StatusLine(DateTimeOffset now) => State switch
    {
        FocusTimerState.Focusing => $"Focus · {TimerText.Format(Remaining(now))} left",
        FocusTimerState.BreakSuggested => "Focus block done — time for a break?",
        FocusTimerState.OnBreak => $"Break · {TimerText.Format(Remaining(now))} left",
        _ => "",
    };

    internal static int WholeMinutesBetween(DateTimeOffset from, DateTimeOffset to) =>
        Math.Max(0, (int)(to - from).TotalMinutes);
}

// ─────────────────────── the session timer (#142) ───────────────────────

/// <summary>One phase of a session-timer cycle (R5 #142).</summary>
public enum SessionPhase
{
    Work,
    ShortBreak,
    LongBreak,
}

/// <summary>
/// A profile's session-timer shape (R5 #142): Pomodoro-style work /
/// break cycles. Sanitized to sane bounds; the defaults are the
/// classic 25 / 5 / 15 with a long break every 4 work blocks.
/// </summary>
public sealed record SessionTimerConfig(
    int WorkMinutes, int BreakMinutes, int LongBreakMinutes, int CyclesBeforeLongBreak)
{
    public static readonly SessionTimerConfig Default = new(25, 5, 15, 4);

    public SessionTimerConfig Sanitized() => new(
        Math.Clamp(WorkMinutes, 1, 240),
        Math.Clamp(BreakMinutes, 1, 120),
        Math.Clamp(LongBreakMinutes, 1, 240),
        Math.Clamp(CyclesBeforeLongBreak, 1, 12));
}

/// <summary>What one <see cref="SessionTimer.Tick"/> observed (R5 #142): the phase just entered, when one began on this tick, plus any focus minutes banked by a work phase that ended.</summary>
public readonly record struct SessionTickResult(bool PhaseChanged, SessionPhase? EnteredPhase, int FocusMinutesBanked);

/// <summary>
/// The session timer (R5 #142): repeating work/break cycles for the
/// ACTIVE profile. Pure state machine — the shell owns the one-second
/// ticker and renders <see cref="StatusLine"/> in the status bar and
/// the tray tooltip. Completed work phases bank their minutes for
/// the weekly stats (R5 #150); stopping mid-work banks the whole
/// minutes actually worked. Pausing freezes the countdown honestly:
/// the phase end moves by exactly the paused span.
/// </summary>
public sealed class SessionTimer
{
    private readonly SessionTimerConfig _config;
    private DateTimeOffset _phaseEndsUtc;
    private DateTimeOffset _phaseStartedUtc;
    private DateTimeOffset _pausedAtUtc;
    private TimeSpan _pausedTotal;

    public SessionTimer(SessionTimerConfig config) => _config = config.Sanitized();

    public SessionTimerConfig Config => _config;

    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }
    public SessionPhase Phase { get; private set; } = SessionPhase.Work;

    /// <summary>Work blocks completed in the current set (resets after a long break).</summary>
    public int CycleInSet { get; private set; }

    /// <summary>Work blocks completed since Start, across all sets.</summary>
    public int TotalWorkBlocks { get; private set; }

    /// <summary>Focus minutes banked by ended work phases since this timer was created (the shell drains it into the weekly stats).</summary>
    public int FocusMinutesBanked { get; private set; }

    /// <summary>Begin a fresh session: work phase, cycle 1.</summary>
    public void Start(DateTimeOffset now)
    {
        IsRunning = true;
        IsPaused = false;
        Phase = SessionPhase.Work;
        CycleInSet = 0;
        TotalWorkBlocks = 0;
        BeginPhase(now, _config.WorkMinutes);
    }

    /// <summary>End the session. A work phase in progress banks its whole elapsed minutes (returned); a break banks nothing.</summary>
    public int Stop(DateTimeOffset now)
    {
        var banked = 0;
        if (IsRunning && Phase == SessionPhase.Work)
        {
            banked = FocusTimer.WholeMinutesBetween(_phaseStartedUtc, EffectiveNow(now));
            FocusMinutesBanked += banked;
        }
        IsRunning = false;
        IsPaused = false;
        return banked;
    }

    /// <summary>Freeze the countdown. No-op unless running and unpaused.</summary>
    public void Pause(DateTimeOffset now)
    {
        if (!IsRunning || IsPaused)
            return;
        IsPaused = true;
        _pausedAtUtc = now;
    }

    /// <summary>Resume: the phase end (and the banked-time baseline) move by the paused span, so paused minutes never count as work.</summary>
    public void Resume(DateTimeOffset now)
    {
        if (!IsRunning || !IsPaused)
            return;
        IsPaused = false;
        var paused = now - _pausedAtUtc;
        if (paused < TimeSpan.Zero)
            paused = TimeSpan.Zero;
        _pausedTotal += paused;
        _phaseEndsUtc += paused;
    }

    /// <summary>Advance the clock. Crosses at most one boundary per tick — a machine asleep through three phases lands in the NEXT phase with a fresh full length, rather than fast-forwarding through cycles nobody lived.</summary>
    public SessionTickResult Tick(DateTimeOffset now)
    {
        if (!IsRunning || IsPaused || now < _phaseEndsUtc)
            return new SessionTickResult(false, null, 0);
        var banked = 0;
        SessionPhase next;
        if (Phase == SessionPhase.Work)
        {
            banked = _config.WorkMinutes;
            FocusMinutesBanked += banked;
            TotalWorkBlocks++;
            CycleInSet++;
            next = CycleInSet >= _config.CyclesBeforeLongBreak
                ? SessionPhase.LongBreak
                : SessionPhase.ShortBreak;
        }
        else
        {
            if (Phase == SessionPhase.LongBreak)
                CycleInSet = 0;
            next = SessionPhase.Work;
        }
        Phase = next;
        BeginPhase(now, MinutesFor(next));
        return new SessionTickResult(true, next, banked);
    }

    /// <summary>Time left in the current phase; zero when the timer isn't running.</summary>
    public TimeSpan Remaining(DateTimeOffset now) =>
        IsRunning ? _phaseEndsUtc - EffectiveNow(now) : TimeSpan.Zero;

    /// <summary>The tray / status-bar line (R5 #142): phase, countdown, cycle position. Empty when off.</summary>
    public string StatusLine(DateTimeOffset now)
    {
        if (!IsRunning)
            return "";
        var remaining = TimerText.Format(Remaining(now));
        var paused = IsPaused ? " · paused" : "";
        return Phase == SessionPhase.Work
            ? $"Focus {remaining} · cycle {CycleInSet + 1} of {_config.CyclesBeforeLongBreak}{paused}"
            : $"Break {remaining}{paused}";
    }

    private int MinutesFor(SessionPhase phase) => phase switch
    {
        SessionPhase.Work => _config.WorkMinutes,
        SessionPhase.ShortBreak => _config.BreakMinutes,
        _ => _config.LongBreakMinutes,
    };

    private void BeginPhase(DateTimeOffset now, int minutes)
    {
        _phaseStartedUtc = now;
        _phaseEndsUtc = now.AddMinutes(minutes);
        _pausedTotal = TimeSpan.Zero;
    }

    /// <summary>While paused, the effective "now" for elapsed math is the moment of pausing.</summary>
    private DateTimeOffset EffectiveNow(DateTimeOffset now) => IsPaused ? _pausedAtUtc : now;
}

// ─────────────────────── window tiling presets (#149) ───────────────────────

/// <summary>A named placement within a monitor's work area (R5 #149). The halves keep R1's #104 geometry exactly; quarters and thirds extend the same split discipline.</summary>
public enum TilePreset
{
    HalfLeft,
    HalfRight,
    QuarterTopLeft,
    QuarterTopRight,
    QuarterBottomLeft,
    QuarterBottomRight,
    ThirdLeft,
    ThirdCenter,
    ThirdRight,
    TwoThirdsLeft,
    TwoThirdsRight,
}

/// <summary>
/// The preset geometry (R5 #149): pure math over the work area, in
/// DIPs like <see cref="WindowTiling"/>. Thirds split at floored
/// third boundaries (so the three columns always tile the area
/// exactly, the middle column taking the rounding); quarters reuse
/// the half split on both axes (the extra pixel goes left/top,
/// matching <see cref="WindowTiling.Half"/>).
/// </summary>
public static class WindowTilingPresets
{
    public static readonly IReadOnlyList<TilePreset> All = Enum.GetValues<TilePreset>();

    public static ScreenRect Rect(ScreenRect workArea, TilePreset preset)
    {
        var leftHalf = WindowTiling.Half(workArea, TileSide.Left);
        var rightHalf = WindowTiling.Half(workArea, TileSide.Right);
        var topHeight = workArea.Height - Math.Floor(workArea.Height / 2);
        var x1 = workArea.Left + Math.Floor(workArea.Width / 3);
        var x2 = workArea.Left + Math.Floor(workArea.Width * 2 / 3);
        var right = workArea.Left + workArea.Width;
        var bottom = workArea.Top + workArea.Height;
        return preset switch
        {
            TilePreset.HalfLeft => leftHalf,
            TilePreset.HalfRight => rightHalf,
            TilePreset.QuarterTopLeft =>
                new ScreenRect(workArea.Left, workArea.Top, leftHalf.Width, topHeight),
            TilePreset.QuarterTopRight =>
                new ScreenRect(rightHalf.Left, workArea.Top, rightHalf.Width, topHeight),
            TilePreset.QuarterBottomLeft =>
                new ScreenRect(workArea.Left, workArea.Top + topHeight, leftHalf.Width, workArea.Height - topHeight),
            TilePreset.QuarterBottomRight =>
                new ScreenRect(rightHalf.Left, workArea.Top + topHeight, rightHalf.Width, workArea.Height - topHeight),
            TilePreset.ThirdLeft =>
                new ScreenRect(workArea.Left, workArea.Top, x1 - workArea.Left, workArea.Height),
            TilePreset.ThirdCenter =>
                new ScreenRect(x1, workArea.Top, x2 - x1, workArea.Height),
            TilePreset.ThirdRight =>
                new ScreenRect(x2, workArea.Top, right - x2, workArea.Height),
            TilePreset.TwoThirdsLeft =>
                new ScreenRect(workArea.Left, workArea.Top, x2 - workArea.Left, workArea.Height),
            TilePreset.TwoThirdsRight =>
                new ScreenRect(x1, workArea.Top, right - x1, workArea.Height),
            _ => workArea,
        };
    }

    /// <summary>The palette/switcher label for a preset (R5 #149).</summary>
    public static string Title(TilePreset preset) => preset switch
    {
        TilePreset.HalfLeft => "Half — left",
        TilePreset.HalfRight => "Half — right",
        TilePreset.QuarterTopLeft => "Quarter — top left",
        TilePreset.QuarterTopRight => "Quarter — top right",
        TilePreset.QuarterBottomLeft => "Quarter — bottom left",
        TilePreset.QuarterBottomRight => "Quarter — bottom right",
        TilePreset.ThirdLeft => "Third — left",
        TilePreset.ThirdCenter => "Third — center",
        TilePreset.ThirdRight => "Third — right",
        TilePreset.TwoThirdsLeft => "Two thirds — left",
        TilePreset.TwoThirdsRight => "Two thirds — right",
        _ => preset.ToString(),
    };

    /// <summary>Parse the palette command's preset segment; false for anything that isn't a preset name.</summary>
    public static bool TryParse(string? text, out TilePreset preset) =>
        Enum.TryParse(text, ignoreCase: false, out preset) && Enum.IsDefined(preset);
}

// ─────────────────────── window groups-lite (#144) ───────────────────────

/// <summary>One named group of open windows (R5 #144). Session-only by design: it names WINDOW IDS, and window ids die with the windows.</summary>
public sealed class WindowGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> WindowIds { get; set; } = new();
}

/// <summary>
/// Window groups-lite (R5 #144): named sets of the windows that exist
/// RIGHT NOW — the switcher groups them and can raise a whole group
/// at once. Deliberately distinct from R2 workspaces: a workspace is
/// a saved layout you apply later; a group is a label on live windows
/// and is forgotten when the app exits (nothing here is persisted).
/// A window belongs to at most one group; pruning against the live
/// window list drops dead members and any group left empty.
/// </summary>
public sealed class WindowGroupSet
{
    public const int MaxGroups = 16;
    public const int MaxNameLength = 40;

    private readonly List<WindowGroup> _groups = new();

    public IReadOnlyList<WindowGroup> Groups => _groups;

    /// <summary>Create a group (optionally with its first members). Null when the name is unusable or the cap is reached.</summary>
    public WindowGroup? Create(string? name, IEnumerable<string>? windowIds = null)
    {
        var clean = SanitizeName(name);
        if (clean is null || _groups.Count >= MaxGroups)
            return null;
        var group = new WindowGroup { Id = Guid.NewGuid().ToString("N"), Name = clean };
        _groups.Add(group);
        if (windowIds is not null)
            foreach (var id in windowIds)
                Assign(id, group.Id);
        return group;
    }

    public bool Rename(string groupId, string? name)
    {
        var clean = SanitizeName(name);
        if (clean is null || Find(groupId) is not { } group)
            return false;
        group.Name = clean;
        return true;
    }

    /// <summary>Delete a group; its windows simply become ungrouped. Nothing is closed.</summary>
    public bool Delete(string groupId) => _groups.RemoveAll(g => g.Id == groupId) > 0;

    /// <summary>Put a window in a group (removing it from any other — one group per window). False when either id names nothing.</summary>
    public bool Assign(string windowId, string groupId)
    {
        if (string.IsNullOrWhiteSpace(windowId) || Find(groupId) is not { } group)
            return false;
        Unassign(windowId);
        if (!group.WindowIds.Contains(windowId))
            group.WindowIds.Add(windowId);
        return true;
    }

    /// <summary>Remove a window from whichever group holds it.</summary>
    public bool Unassign(string windowId) =>
        _groups.Any(g => g.WindowIds.Remove(windowId));

    /// <summary>The group a window belongs to, or null.</summary>
    public WindowGroup? GroupOf(string windowId) =>
        _groups.FirstOrDefault(g => g.WindowIds.Contains(windowId));

    /// <summary>The member ids of a group that are still live, in membership order. Empty for an unknown group.</summary>
    public IReadOnlyList<string> LiveMembers(string groupId, IEnumerable<string> liveWindowIds)
    {
        if (Find(groupId) is not { } group)
            return Array.Empty<string>();
        var live = new HashSet<string>(liveWindowIds, StringComparer.Ordinal);
        return group.WindowIds.Where(live.Contains).ToList();
    }

    /// <summary>Drop members that no longer exist, and groups left with nobody in them (R5 #144: a group of closed windows is not a group).</summary>
    public void Prune(IEnumerable<string> liveWindowIds)
    {
        var live = new HashSet<string>(liveWindowIds, StringComparer.Ordinal);
        foreach (var group in _groups)
            group.WindowIds.RemoveAll(id => !live.Contains(id));
        _groups.RemoveAll(g => g.WindowIds.Count == 0);
    }

    /// <summary>A group name worth keeping: trimmed, single-spaced, capped. Null when nothing usable remains.</summary>
    public static string? SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var clean = string.Join(" ", name.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (clean.Length == 0)
            return null;
        return clean.Length <= MaxNameLength ? clean : clean[..MaxNameLength].TrimEnd();
    }

    private WindowGroup? Find(string groupId) =>
        _groups.FirstOrDefault(g => g.Id == groupId);
}

// ─────────────────────── the reading list (#143) ───────────────────────

/// <summary>
/// One saved page (R5 #143): a title and an app ROUTE — the muse.ai
/// path only, produced by <see cref="AppRoute.FromUrl"/>, which
/// refuses every other host. A reading-list entry can therefore never
/// hold a full page address, a query string, or a token.
/// </summary>
public sealed class ReadingListEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Route { get; set; } = AppRoute.Home;
    public DateTimeOffset AddedUtc { get; set; }
    public bool IsRead { get; set; }
}

/// <summary>
/// The reading lists (R5 #143): per profile, newest first, in their
/// own file beside the settings — local-only, riding no export (the
/// profile-usage discipline from R3 #129). Adding a page that's
/// already listed refreshes its title and moves it to the top,
/// keeping its read state: saving twice is a re-save, not a dupe.
/// </summary>
public sealed class ReadingLists
{
    public const int MaxPerProfile = 200;
    public const int MaxTitleLength = 200;

    public Dictionary<string, List<ReadingListEntry>> ByProfile { get; set; } = new();

    /// <summary>
    /// Save a page for a profile. The URL must be a muse.ai app page
    /// (<see cref="AppRoute.FromUrl"/> decides); anything else returns
    /// null — the list holds Muse pages, not arbitrary links.
    /// </summary>
    public ReadingListEntry? Add(string profileId, string? title, string? url, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(profileId) || AppRoute.FromUrl(url) is not { } route)
            return null;
        var list = ListFor(profileId);
        var existing = list.FirstOrDefault(e => e.Route == route);
        if (existing is not null)
        {
            existing.Title = CleanTitle(title, route);
            existing.AddedUtc = now;
            list.Remove(existing);
            list.Insert(0, existing);
            return existing;
        }
        var entry = new ReadingListEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = CleanTitle(title, route),
            Route = route,
            AddedUtc = now,
        };
        list.Insert(0, entry);
        Prune(list);
        return entry;
    }

    public bool SetRead(string profileId, string entryId, bool read)
    {
        if (Find(profileId, entryId) is not { } entry)
            return false;
        entry.IsRead = read;
        return true;
    }

    public bool Remove(string profileId, string entryId) =>
        ByProfile.TryGetValue(profileId, out var list)
        && list.RemoveAll(e => e.Id == entryId) > 0;

    /// <summary>The profile's list, newest first; empty when the profile has none.</summary>
    public IReadOnlyList<ReadingListEntry> EntriesFor(string profileId) =>
        ByProfile.TryGetValue(profileId, out var list) ? list : Array.Empty<ReadingListEntry>();

    public int UnreadCount(string profileId) =>
        EntriesFor(profileId).Count(e => !e.IsRead);

    /// <summary>Entries added at or after <paramref name="sinceUtc"/> — the digest's "added this week" (R5 #146).</summary>
    public int AddedSince(string profileId, DateTimeOffset sinceUtc) =>
        EntriesFor(profileId).Count(e => e.AddedUtc >= sinceUtc);

    /// <summary>Repair pass after a load (and for hand-edited files): drop unusable entries, re-sort newest first, cap each list.</summary>
    public void Sanitize()
    {
        foreach (var (profileId, list) in ByProfile.ToList())
        {
            if (string.IsNullOrWhiteSpace(profileId) || list is null)
            {
                ByProfile.Remove(profileId);
                continue;
            }
            list.RemoveAll(e => e is null
                || string.IsNullOrWhiteSpace(e.Id)
                || AppRoute.Sanitize(e.Route) is null);
            foreach (var entry in list)
            {
                entry.Route = AppRoute.Sanitize(entry.Route)!;
                entry.Title = CleanTitle(entry.Title, entry.Route);
            }
            var sorted = list.OrderByDescending(e => e.AddedUtc).ToList();
            list.Clear();
            list.AddRange(sorted);
            Prune(list);
            if (list.Count == 0)
                ByProfile.Remove(profileId);
        }
    }

    private List<ReadingListEntry> ListFor(string profileId)
    {
        if (!ByProfile.TryGetValue(profileId, out var list) || list is null)
        {
            list = new List<ReadingListEntry>();
            ByProfile[profileId] = list;
        }
        return list;
    }

    private ReadingListEntry? Find(string profileId, string entryId) =>
        EntriesFor(profileId).FirstOrDefault(e => e.Id == entryId);

    /// <summary>Over the cap, the oldest READ entries go first — unread pages are the ones the list exists for — then the oldest overall.</summary>
    private static void Prune(List<ReadingListEntry> list)
    {
        while (list.Count > MaxPerProfile)
        {
            var victim = list.FindLastIndex(e => e.IsRead);
            list.RemoveAt(victim >= 0 ? victim : list.Count - 1);
        }
    }

    private static string CleanTitle(string? title, string route)
    {
        var clean = string.IsNullOrWhiteSpace(title)
            ? ""
            : string.Join(" ", title.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (clean.Length == 0)
            clean = route == AppRoute.Home ? "Muse home" : route;
        return clean.Length <= MaxTitleLength ? clean : clean[..MaxTitleLength].TrimEnd();
    }
}

/// <summary>The reading-list store: same atomic-save, fail-soft-load pattern as the profile-usage store.</summary>
public static class ReadingListStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ReadingLists Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ReadingLists();
            var lists = JsonSerializer.Deserialize<ReadingLists>(File.ReadAllText(path), Options)
                ?? new ReadingLists();
            lists.ByProfile ??= new Dictionary<string, List<ReadingListEntry>>();
            lists.Sanitize();
            return lists;
        }
        catch (Exception)
        {
            return new ReadingLists();
        }
    }

    public static void Save(string path, ReadingLists lists)
    {
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(lists, Options));
    }
}

/// <summary>One palette command per reading-list entry (R5 #143): open it (payload = the ROUTE — the shell navigates within the app), unread first, then newest.</summary>
public sealed class ReadingListPaletteProvider : IPaletteProvider
{
    private readonly IReadOnlyList<ReadingListEntry> _entries;

    public ReadingListPaletteProvider(IReadOnlyList<ReadingListEntry> entries) => _entries = entries;

    public string Name => "Reading list";

    public IEnumerable<PaletteCommand> GetCommands()
    {
        foreach (var entry in _entries.OrderBy(e => e.IsRead).ThenByDescending(e => e.AddedUtc))
        {
            yield return new PaletteCommand(
                Id: $"reading.open.{entry.Id}",
                Title: entry.Title,
                Detail: entry.IsRead ? "Reading list · read" : "Reading list · unread",
                Category: "Reading list",
                Keywords: "reading list saved page read later open",
                Source: PaletteCommandSource.ReadingList,
                Payload: entry.Route);
        }
    }
}

// ─────────────────────── history search (#145) ───────────────────────

/// <summary>One history-search hit (R5 #145): the entry, its rank, and the two display strings the surfaces show — a title and a PATH (never a full address).</summary>
public sealed record HistorySearchHit(NavigationEntry Entry, int Score, string DisplayTitle, string Path);

/// <summary>
/// Full-text search over the session's navigation history (R5 #145).
/// The app keeps no cross-session browsing history — by privacy
/// design there is nothing on disk to search — so the corpus is the
/// windows' session histories plus their titles, and the search is
/// honest about that scope everywhere it appears (see
/// KNOWN_LIMITATIONS). Matching is full-text in the ordinary sense:
/// EVERY query term must appear in the title or the page path (as a
/// word-fuzzy match or a plain substring); ranking reuses the
/// palette's fuzzy scorer so the two searches feel the same.
/// Duplicate visits to one page collapse to the most recent.
/// </summary>
public static class HistorySearch
{
    public const int DefaultMaxResults = 25;

    public static IReadOnlyList<HistorySearchHit> Search(
        string? query, IEnumerable<NavigationEntry> entries, int maxResults = DefaultMaxResults)
    {
        if (maxResults < 1)
            maxResults = 1;
        // Dedupe by URL, keeping the LAST occurrence (the freshest
        // title for that page), remembering recency for tie-breaks.
        var byUrl = new Dictionary<string, (NavigationEntry Entry, int Index)>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in entries)
        {
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.Url))
                byUrl[entry.Url] = (entry, index);
            index++;
        }
        var deduped = byUrl.Values.OrderBy(v => v.Index).ToList();

        if (string.IsNullOrWhiteSpace(query))
        {
            // No query: the most recent pages, newest first.
            return deduped
                .OrderByDescending(v => v.Index)
                .Take(maxResults)
                .Select(v => Hit(v.Entry, 0))
                .ToList();
        }

        var terms = query.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .ToList();
        if (terms.Count == 0)
            return Array.Empty<HistorySearchHit>();

        var hits = new List<(HistorySearchHit Hit, int Index)>();
        foreach (var (entry, i) in deduped)
        {
            var title = entry.Title ?? "";
            var path = PathOf(entry.Url);
            var allMatch = terms.All(term =>
                FuzzyMatch.WordScore(term, title).IsMatch
                || FuzzyMatch.WordScore(term, path).IsMatch
                || title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || path.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (!allMatch)
                continue;
            var titleScore = FuzzyMatch.Score(query, title);
            var score = titleScore.IsMatch
                ? titleScore.Score
                : FuzzyMatch.Score(query, path).Score - 20;
            hits.Add((Hit(entry, score), i));
        }
        return hits
            .OrderByDescending(h => h.Hit.Score)
            .ThenByDescending(h => h.Index)
            .Take(maxResults)
            .Select(h => h.Hit)
            .ToList();
    }

    /// <summary>The display title for an entry: its page title, or the path when the page never gave one.</summary>
    public static string DisplayTitle(NavigationEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.Title) ? entry.Title.Trim() : PathOf(entry.Url);

    /// <summary>The path portion of a URL for display and matching — query and fragment never reach a search surface.</summary>
    public static string PathOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";
        if (AppRoute.FromUrl(url) is { } route)
            return route;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : "";
    }

    private static HistorySearchHit Hit(NavigationEntry entry, int score) =>
        new(entry, score, DisplayTitle(entry), PathOf(entry.Url));
}

/// <summary>One palette command per recent history entry (R5 #145). The payload is the page URL — in-process only, exactly like the downloads provider's file paths: never logged, never in the action history (Core refuses non-action commands there).</summary>
public sealed class HistoryPaletteProvider : IPaletteProvider
{
    private readonly IReadOnlyList<NavigationEntry> _entries;

    public HistoryPaletteProvider(IReadOnlyList<NavigationEntry> entries, int max = HistorySearch.DefaultMaxResults)
    {
        // Most recent unique pages, newest first — the blank-query order.
        _entries = HistorySearch.Search(null, entries, max).Select(h => h.Entry).ToList();
    }

    public string Name => "History";

    public IEnumerable<PaletteCommand> GetCommands()
    {
        var n = 0;
        foreach (var entry in _entries)
        {
            yield return new PaletteCommand(
                Id: $"history.open.{n++}",
                Title: HistorySearch.DisplayTitle(entry),
                Detail: $"History · {HistorySearch.PathOf(entry.Url)}",
                Category: "History",
                Keywords: "history visited page earlier back",
                Source: PaletteCommandSource.History,
                Payload: entry.Url);
        }
    }
}

// ─────────────────────── quick notes (#148) ───────────────────────

/// <summary>One quick note (R5 #148): the text and when it was taken. Nothing else — a note is the user's words, kept on this PC.</summary>
public sealed class NoteEntry
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>
/// Per-profile notes (R5 #148), newest first, in their own file
/// beside the settings — local-only like the usage tallies, riding
/// no export. Any profile's notes are that profile's: switching
/// profiles switches the notebook.
/// </summary>
public sealed class ProfileNotes
{
    public const int MaxPerProfile = 500;
    public const int MaxTextLength = 4000;

    public Dictionary<string, List<NoteEntry>> ByProfile { get; set; } = new();

    /// <summary>File a note under a profile. Blank text files nothing (null); over-long text is capped, not refused — a capture must never fail loudly.</summary>
    public NoteEntry? Add(string profileId, string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(text))
            return null;
        var clean = text.Trim();
        if (clean.Length > MaxTextLength)
            clean = clean[..MaxTextLength];
        if (!ByProfile.TryGetValue(profileId, out var list) || list is null)
        {
            list = new List<NoteEntry>();
            ByProfile[profileId] = list;
        }
        var note = new NoteEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = clean,
            CreatedUtc = now,
        };
        list.Insert(0, note);
        while (list.Count > MaxPerProfile)
            list.RemoveAt(list.Count - 1);
        return note;
    }

    public bool Delete(string profileId, string noteId) =>
        ByProfile.TryGetValue(profileId, out var list)
        && list.RemoveAll(n => n.Id == noteId) > 0;

    /// <summary>The profile's notes, newest first.</summary>
    public IReadOnlyList<NoteEntry> NotesFor(string profileId) =>
        ByProfile.TryGetValue(profileId, out var list) ? list : Array.Empty<NoteEntry>();

    /// <summary>Notes taken at or after <paramref name="sinceUtc"/> across ALL profiles — the digest's count (R5 #146).</summary>
    public int CountSince(DateTimeOffset sinceUtc) =>
        ByProfile.Values.Sum(list => list?.Count(n => n.CreatedUtc >= sinceUtc) ?? 0);

    /// <summary>Load repair: drop unusable notes, keep newest-first order, cap each notebook.</summary>
    public void Sanitize()
    {
        foreach (var (profileId, list) in ByProfile.ToList())
        {
            if (string.IsNullOrWhiteSpace(profileId) || list is null)
            {
                ByProfile.Remove(profileId);
                continue;
            }
            list.RemoveAll(n => n is null || string.IsNullOrWhiteSpace(n.Id) || string.IsNullOrWhiteSpace(n.Text));
            var sorted = list.OrderByDescending(n => n.CreatedUtc).Take(MaxPerProfile).ToList();
            list.Clear();
            list.AddRange(sorted);
            if (list.Count == 0)
                ByProfile.Remove(profileId);
        }
    }
}

/// <summary>The notes store: same atomic-save, fail-soft-load pattern as the other R5 stores.</summary>
public static class ProfileNotesStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ProfileNotes Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ProfileNotes();
            var notes = JsonSerializer.Deserialize<ProfileNotes>(File.ReadAllText(path), Options)
                ?? new ProfileNotes();
            notes.ByProfile ??= new Dictionary<string, List<NoteEntry>>();
            notes.Sanitize();
            return notes;
        }
        catch (Exception)
        {
            return new ProfileNotes();
        }
    }

    public static void Save(string path, ProfileNotes notes)
    {
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(notes, Options));
    }
}

/// <summary>
/// The quick-note global hotkey (R5 #148): the S11 pattern reused —
/// a second RegisterHotKey gesture with its OWN settings fields,
/// resolved under the same rules as the show/hide hotkey (Ctrl or
/// Alt required; never equal to an in-app gesture) PLUS one more:
/// never equal to the show/hide gesture itself, or one press would
/// try to do both. Off by default — a second system-wide key is an
/// opt-in, and the in-app surfaces (menu, palette) always work.
/// </summary>
public static class QuickNoteHotkey
{
    /// <summary>The factory gesture: Ctrl+Alt+N — free in Windows, the shell, and the in-app catalog.</summary>
    public const string DefaultGestureText = "Ctrl+Alt+N";

    public static ShortcutGesture DefaultGesture =>
        ShortcutGesture.TryParse(DefaultGestureText, out var gesture)
            ? gesture
            : throw new InvalidOperationException("The default quick-note gesture doesn't parse.");

    /// <summary>Resolve the saved state into a registration decision, mirroring <see cref="GlobalHotkey.Resolve"/> plus the show/hide equality rule.</summary>
    public static HotkeyResolution Resolve(
        bool enabled, string? storedText,
        IReadOnlyDictionary<string, string>? inAppOverrides, string? showHideGestureText)
    {
        var gesture = DefaultGesture;
        if (ShortcutGesture.TryParse(storedText, out var parsed) && HasCtrlOrAlt(parsed))
            gesture = parsed;
        if (!enabled)
            return new HotkeyResolution(HotkeyResolutionKind.Disabled, gesture, null);
        if (ShortcutGesture.TryParse(showHideGestureText, out var showHide) && gesture == showHide)
            return new HotkeyResolution(HotkeyResolutionKind.BlockedByConflict, gesture,
                "the global show/hide hotkey");
        var owner = GlobalHotkey.ConflictOwner(gesture, inAppOverrides);
        return owner is null
            ? new HotkeyResolution(HotkeyResolutionKind.Ready, gesture, null)
            : new HotkeyResolution(HotkeyResolutionKind.BlockedByConflict, gesture, owner);
    }

    /// <summary>Validate a gesture captured in Settings for the quick-note hotkey (the <see cref="GlobalHotkey.TrySetGesture"/> bar, plus the show/hide rule).</summary>
    public static bool TrySetGesture(
        IReadOnlyDictionary<string, string>? inAppOverrides,
        string? showHideGestureText,
        ShortcutGesture gesture,
        out string? error)
    {
        error = null;
        if (!ShortcutGesture.IsAllowedKey(gesture.Key))
        {
            error = "That key can't be used as a shortcut.";
            return false;
        }
        if (!HasCtrlOrAlt(gesture))
        {
            error = $"A global shortcut needs Ctrl or Alt as well — plain {ShortcutGesture.KeyDisplayName(gesture.Key)} would be captured in every app, including while you type.";
            return false;
        }
        if (ShortcutGesture.TryParse(showHideGestureText, out var showHide) && gesture == showHide)
        {
            error = $"{gesture.DisplayText()} is the global show/hide hotkey — one press can't do both. Pick a different combination.";
            return false;
        }
        if (GlobalHotkey.ConflictOwner(gesture, inAppOverrides) is { } owner)
        {
            error = $"{gesture.DisplayText()} is already used by {owner} inside MuseDesktop — the global key would override it everywhere. Pick a different combination.";
            return false;
        }
        return true;
    }

    private static bool HasCtrlOrAlt(ShortcutGesture gesture) =>
        gesture.Modifiers.HasFlag(GestureModifiers.Control)
        || gesture.Modifiers.HasFlag(GestureModifiers.Alt);
}

// ─────────────────────── weekly stats (#150) ───────────────────────

/// <summary>One profile's counts for one week (R5 #150): launches, downloads completed, focus minutes. Counts only — no titles, no paths, no page data.</summary>
public sealed class WeekCounts
{
    public int Launches { get; set; }
    public int DownloadsCompleted { get; set; }
    public int FocusMinutes { get; set; }
}

/// <summary>
/// The weekly tallies behind the stats dashboard (R5 #150) and the
/// digest (R5 #146): per profile, per week (Monday 00:00 UTC), in
/// their own file — the same local-only, rides-no-export discipline
/// as the R3 usage tallies, extended with the two series #129 didn't
/// have (downloads and focus minutes, recorded as they happen).
/// Weeks older than 26 are pruned on record: this is a dashboard,
/// not an archive. Clearing the stats (the dashboard's button)
/// deletes the file; nothing else reads it.
/// </summary>
public sealed class WeeklyStats
{
    public const int WeeksKept = 26;

    /// <summary>Profile id → week key ("yyyy-MM-dd" of the week's Monday, UTC) → counts.</summary>
    public Dictionary<string, Dictionary<string, WeekCounts>> ByProfile { get; set; } = new();

    /// <summary>The Monday (00:00 UTC) of the week containing <paramref name="now"/>.</summary>
    public static DateTimeOffset WeekStartUtc(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        var daysSinceMonday = ((int)utc.DayOfWeek + 6) % 7;
        var monday = utc.Date.AddDays(-daysSinceMonday);
        return new DateTimeOffset(monday, TimeSpan.Zero);
    }

    public static string WeekKey(DateTimeOffset anyTimeInWeek) =>
        WeekStartUtc(anyTimeInWeek).ToString("yyyy-MM-dd");

    public void RecordLaunch(string profileId, DateTimeOffset now) =>
        Mutate(profileId, now, c => c.Launches++);

    public void RecordDownload(string profileId, DateTimeOffset now) =>
        Mutate(profileId, now, c => c.DownloadsCompleted++);

    /// <summary>Bank focus minutes; zero or negative banks nothing.</summary>
    public void RecordFocusMinutes(string profileId, int minutes, DateTimeOffset now)
    {
        if (minutes > 0)
            Mutate(profileId, now, c => c.FocusMinutes += minutes);
    }

    /// <summary>The counts for one profile in one week (zeros when nothing was recorded).</summary>
    public WeekCounts CountsFor(string profileId, DateTimeOffset anyTimeInWeek)
    {
        if (ByProfile.TryGetValue(profileId, out var weeks)
            && weeks.TryGetValue(WeekKey(anyTimeInWeek), out var counts)
            && counts is not null)
            return counts;
        return new WeekCounts();
    }

    /// <summary>Every profile id that has any counts at all.</summary>
    public IReadOnlyList<string> ProfileIds => ByProfile.Keys.ToList();

    private void Mutate(string profileId, DateTimeOffset now, Action<WeekCounts> apply)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return;
        if (!ByProfile.TryGetValue(profileId, out var weeks) || weeks is null)
        {
            weeks = new Dictionary<string, WeekCounts>();
            ByProfile[profileId] = weeks;
        }
        var key = WeekKey(now);
        if (!weeks.TryGetValue(key, out var counts) || counts is null)
        {
            counts = new WeekCounts();
            weeks[key] = counts;
        }
        apply(counts);
        // Prune: keep the current week and the 25 before it.
        var cutoff = WeekKey(WeekStartUtc(now).AddDays(-7 * (WeeksKept - 1)));
        foreach (var old in weeks.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
            weeks.Remove(old);
    }
}

/// <summary>The weekly-stats store: same atomic-save, fail-soft-load pattern as the other R5 stores.</summary>
public static class WeeklyStatsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static WeeklyStats Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new WeeklyStats();
            var stats = JsonSerializer.Deserialize<WeeklyStats>(File.ReadAllText(path), Options)
                ?? new WeeklyStats();
            stats.ByProfile ??= new Dictionary<string, Dictionary<string, WeekCounts>>();
            return stats;
        }
        catch (Exception)
        {
            return new WeeklyStats();
        }
    }

    public static void Save(string path, WeeklyStats stats)
    {
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(stats, Options));
    }
}

/// <summary>One profile's dashboard row (R5 #150): the counts plus each metric's fraction of the week's best, so the shell draws bars without doing any math of its own.</summary>
public sealed record StatsProfileRow(
    string ProfileId, string Name,
    int Launches, int Downloads, int FocusMinutes,
    double LaunchFraction, double DownloadFraction, double FocusFraction);

/// <summary>The stats dashboard model (R5 #150): one week, one row per profile that exists or has counts, totals across profiles.</summary>
public sealed record StatsReport(
    DateTimeOffset WeekStartUtc,
    IReadOnlyList<StatsProfileRow> Rows,
    int TotalLaunches, int TotalDownloads, int TotalFocusMinutes)
{
    public static StatsReport Build(WeeklyStats stats, AppSettings settings, DateTimeOffset anyTimeInWeek)
    {
        var weekStart = WeeklyStats.WeekStartUtc(anyTimeInWeek);
        var ids = new List<string>();
        foreach (var profile in settings.Profiles)
            if (!ids.Contains(profile.Id))
                ids.Add(profile.Id);
        foreach (var id in stats.ProfileIds)
            if (!ids.Contains(id))
                ids.Add(id);

        var raw = ids
            .Select(id => (Id: id, Counts: stats.CountsFor(id, weekStart)))
            .ToList();
        var maxLaunches = raw.Count == 0 ? 0 : raw.Max(r => r.Counts.Launches);
        var maxDownloads = raw.Count == 0 ? 0 : raw.Max(r => r.Counts.DownloadsCompleted);
        var maxFocus = raw.Count == 0 ? 0 : raw.Max(r => r.Counts.FocusMinutes);

        var rows = raw
            .Select(r => new StatsProfileRow(
                r.Id,
                settings.Profiles.FirstOrDefault(p => p.Id == r.Id)?.Name ?? "Profile",
                r.Counts.Launches, r.Counts.DownloadsCompleted, r.Counts.FocusMinutes,
                Fraction(r.Counts.Launches, maxLaunches),
                Fraction(r.Counts.DownloadsCompleted, maxDownloads),
                Fraction(r.Counts.FocusMinutes, maxFocus)))
            .OrderByDescending(r => r.Launches + r.Downloads + r.FocusMinutes)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new StatsReport(
            weekStart, rows,
            rows.Sum(r => r.Launches), rows.Sum(r => r.Downloads), rows.Sum(r => r.FocusMinutes));
    }

    private static double Fraction(int value, int max) =>
        max <= 0 ? 0 : Math.Clamp((double)value / max, 0, 1);
}

// ─────────────────────── the weekly digest (#146) ───────────────────────

/// <summary>
/// The weekly digest (R5 #146): an on-demand, local summary of the
/// week so far — launches, downloads completed, notes taken, reading
/// saved, focus time, windows open right now. Generated ONLY when
/// asked (the menu / palette action), rendered as plain lines on a
/// simple page, never scheduled, never sent anywhere. Sources: the
/// weekly stats for launches + focus, the download history for
/// downloads (its CompletedUtc stamps are the real record), the
/// notes + reading-list stores for their counts.
/// </summary>
public sealed record WeeklyDigest(
    DateTimeOffset WeekStartUtc, string Title, IReadOnlyList<string> Lines)
{
    public string RenderText() =>
        Title + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, Lines);

    public static WeeklyDigest Build(
        WeeklyStats stats, AppSettings settings,
        ReadingLists reading, ProfileNotes notes,
        IReadOnlyList<DownloadRecord> downloadHistory,
        int openWindowCount, DateTimeOffset now)
    {
        var weekStart = WeeklyStats.WeekStartUtc(now);
        var report = StatsReport.Build(stats, settings, now);
        var downloadsThisWeek = downloadHistory.Count(r =>
            r.State == DownloadRecordState.Completed && r.CompletedUtc >= weekStart);
        var notesThisWeek = notes.CountSince(weekStart);
        var addedThisWeek = settings.Profiles.Sum(p => reading.AddedSince(p.Id, weekStart));
        var unread = settings.Profiles.Sum(p => reading.UnreadCount(p.Id));

        var lines = new List<string>
        {
            $"Week of {weekStart:MMMM d, yyyy}",
            "",
            $"Launches: {report.TotalLaunches} across {report.Rows.Count(r => r.Launches > 0)} profile(s)",
            $"Downloads completed: {downloadsThisWeek}",
            $"Focus time: {TimerText.FormatMinutes(report.TotalFocusMinutes)}",
            $"Notes taken: {notesThisWeek}",
            $"Reading list: {addedThisWeek} saved this week, {unread} unread",
            $"Windows open right now: {openWindowCount}",
        };

        var busiest = report.Rows
            .Where(r => r.Launches + r.Downloads + r.FocusMinutes > 0)
            .OrderByDescending(r => r.Launches + r.Downloads + r.FocusMinutes)
            .FirstOrDefault();
        if (busiest is not null)
        {
            lines.Add("");
            lines.Add($"Most active profile: {busiest.Name} — {busiest.Launches} launches, " +
                      $"{busiest.Downloads} downloads, {TimerText.FormatMinutes(busiest.FocusMinutes)} focused");
        }
        lines.Add("");
        lines.Add("Everything on this page was counted on this PC. Nothing was sent anywhere.");

        return new WeeklyDigest(weekStart, "Weekly digest", lines);
    }
}

// ─────────────────────── command scheduling (#147) ───────────────────────

/// <summary>When a scheduled command runs (R5 #147). All three fire only while MuseDesktop itself is running — no Windows Task Scheduler registration exists or is implied anywhere.</summary>
public enum ScheduleTriggerKind
{
    /// <summary>Once per launch, shortly after startup.</summary>
    OnStartup,

    /// <summary>Every day at a local time.</summary>
    DailyAt,

    /// <summary>Once, at a chosen moment.</summary>
    OnceAt,
}

/// <summary>
/// One scheduled command (R5 #147): a built-in palette ACTION (the
/// registry is the only command surface — a schedule can only ever
/// name an <c>action.*</c> id that exists) plus its trigger. The
/// bookkeeping fields (last-run date, has-fired) are written by the
/// evaluator when a rule fires, so a crash can't make a daily rule
/// fire twice in a day or a one-off fire twice ever.
/// </summary>
public sealed class ScheduledCommand
{
    public string Id { get; set; } = "";
    public string CommandId { get; set; } = "";
    public ScheduleTriggerKind Trigger { get; set; } = ScheduleTriggerKind.OnStartup;
    /// <summary>DailyAt: minutes after local midnight. Sanitized to 0–1439.</summary>
    public int DailyMinutes { get; set; }
    /// <summary>OnceAt: the moment to fire.</summary>
    public DateTimeOffset? OnceAtUtc { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>DailyAt bookkeeping: the local date ("yyyy-MM-dd") the rule last fired.</summary>
    public string? LastRunLocalDate { get; set; }
    /// <summary>OnceAt bookkeeping: the rule has had its one firing.</summary>
    public bool HasFired { get; set; }

    /// <summary>The Settings list line: what runs, and when.</summary>
    public string Describe()
    {
        var title = CommandScheduler.TitleFor(CommandId) ?? CommandId;
        var when = Trigger switch
        {
            ScheduleTriggerKind.OnStartup => "at startup",
            ScheduleTriggerKind.DailyAt =>
                $"daily at {DailyMinutes / 60:00}:{DailyMinutes % 60:00}",
            ScheduleTriggerKind.OnceAt => OnceAtUtc is { } at
                ? $"once, {at.LocalDateTime:yyyy-MM-dd HH:mm}"
                : "once (no time set)",
            _ => "",
        };
        return $"{title} — {when}{(Enabled ? "" : " (off)")}";
    }
}

/// <summary>
/// The scheduler (R5 #147): the shell ticks it about once a minute
/// with the local now; it answers the rules that are due and stamps
/// their bookkeeping in the same pass. Rules fire while the app
/// runs only — a DailyAt whose time passed while MuseDesktop was
/// closed fires once at the next launch (late, never multiplied),
/// because the stamp says "not yet today", not "missed".
/// </summary>
public static class CommandScheduler
{
    public const int MaxRules = 20;

    /// <summary>The commands a schedule may name: exactly the built-in action ids.</summary>
    public static bool IsKnownCommand(string? commandId) =>
        commandId is not null
        && CommandRegistry.BuiltinActions.Any(a => a.Id == commandId);

    /// <summary>The display title of a schedulable command, or null when it isn't one.</summary>
    public static string? TitleFor(string? commandId) =>
        CommandRegistry.BuiltinActions.FirstOrDefault(a => a.Id == commandId)?.Title;

    /// <summary>
    /// Find the next real command deadline after <paramref name="localNow"/>.
    /// Startup rules are due immediately only while startup is pending;
    /// daily rules use local wall-clock time and one-off rules retain their
    /// stored instant. The shell uses this to arm a one-shot wakeup rather
    /// than scanning every few seconds.
    /// </summary>
    public static DateTimeOffset? NextDue(
        IReadOnlyList<ScheduledCommand> rules, DateTimeOffset localNow, bool startupPending)
    {
        DateTimeOffset? next = startupPending && rules.Any(r =>
            r is not null && r.Enabled && IsKnownCommand(r.CommandId)
            && r.Trigger == ScheduleTriggerKind.OnStartup)
            ? localNow
            : null;
        var today = localNow.ToString("yyyy-MM-dd");
        foreach (var rule in rules)
        {
            if (rule is null || !rule.Enabled || !IsKnownCommand(rule.CommandId))
                continue;
            DateTimeOffset? due = rule.Trigger switch
            {
                ScheduleTriggerKind.OnceAt when !rule.HasFired && rule.OnceAtUtc is { } at && at > localNow => at,
                ScheduleTriggerKind.DailyAt => NextDailyDue(rule, localNow, today),
                _ => null,
            };
            if (due is not null && (next is null || due.Value < next.Value))
                next = due;
        }
        return next;
    }

    private static DateTimeOffset? NextDailyDue(
        ScheduledCommand rule, DateTimeOffset localNow, string today)
    {
        var dueToday = new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day,
            rule.DailyMinutes / 60, rule.DailyMinutes % 60, 0, localNow.Offset);
        if (rule.LastRunLocalDate == today || dueToday <= localNow)
            return dueToday.AddDays(1);
        return dueToday;
    }

    /// <summary>
    /// The rules due right now. Due rules are stamped BEFORE the
    /// caller runs them: a command that fails must not refire in a loop.
    /// </summary>
    public static IReadOnlyList<ScheduledCommand> Evaluate(
        List<ScheduledCommand> rules, DateTimeOffset localNow, bool startupPending)
    {
        var due = new List<ScheduledCommand>();
        var today = localNow.ToString("yyyy-MM-dd");
        var minutes = localNow.Hour * 60 + localNow.Minute;
        foreach (var rule in rules)
        {
            if (rule is null || !rule.Enabled || !IsKnownCommand(rule.CommandId))
                continue;
            switch (rule.Trigger)
            {
                case ScheduleTriggerKind.OnStartup:
                    if (startupPending)
                        due.Add(rule);
                    break;
                case ScheduleTriggerKind.DailyAt:
                    if (minutes >= rule.DailyMinutes && rule.LastRunLocalDate != today)
                    {
                        rule.LastRunLocalDate = today;
                        due.Add(rule);
                    }
                    break;
                case ScheduleTriggerKind.OnceAt:
                    if (!rule.HasFired && rule.OnceAtUtc is { } at && localNow >= at)
                    {
                        rule.HasFired = true;
                        due.Add(rule);
                    }
                    break;
            }
        }
        return due;
    }

    /// <summary>
    /// Repair one stored rule (settings sanitization): null when the
    /// rule names no real command or has no usable trigger time;
    /// otherwise the rule with its fields clamped and an id ensured.
    /// </summary>
    public static ScheduledCommand? Sanitize(ScheduledCommand? rule)
    {
        if (rule is null || !IsKnownCommand(rule.CommandId))
            return null;
        if (!Enum.IsDefined(rule.Trigger))
            return null;
        if (rule.Trigger == ScheduleTriggerKind.OnceAt && rule.OnceAtUtc is null)
            return null;
        if (string.IsNullOrWhiteSpace(rule.Id))
            rule.Id = Guid.NewGuid().ToString("N");
        rule.DailyMinutes = Math.Clamp(rule.DailyMinutes, 0, QuietHours.MinutesPerDay - 1);
        return rule;
    }

    /// <summary>Build a rule from the Settings dialog's answers, or explain (in <paramref name="error"/>) why those answers can't be a rule.</summary>
    public static bool TryCreate(
        string? commandId, ScheduleTriggerKind trigger, int dailyMinutes,
        DateTimeOffset? onceAtUtc, out ScheduledCommand? rule, out string? error)
    {
        rule = null;
        error = null;
        if (!IsKnownCommand(commandId))
        {
            error = "Pick a command from the list.";
            return false;
        }
        if (trigger == ScheduleTriggerKind.OnceAt && onceAtUtc is null)
        {
            error = "A one-off schedule needs a date and time.";
            return false;
        }
        rule = new ScheduledCommand
        {
            Id = Guid.NewGuid().ToString("N"),
            CommandId = commandId!,
            Trigger = trigger,
            DailyMinutes = Math.Clamp(dailyMinutes, 0, QuietHours.MinutesPerDay - 1),
            OnceAtUtc = onceAtUtc,
            Enabled = true,
        };
        return true;
    }
}
