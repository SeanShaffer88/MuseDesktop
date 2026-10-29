namespace MuseDesktop.Core;

/// <summary>
/// What Muse's page is telling us about needing attention (v0.8.0,
/// S4 #31). The ONLY signal trusted is the document title's leading
/// "(N)" prefix — the long-standing web convention (Gmail, Slack,
/// Discord) for an unread count in the title. Anything else about a
/// title is prose, not data: if the prefix is absent the signal is
/// None and the app shows nothing, because a guessed badge count is
/// worse than no badge. If muse.ai never uses the convention, this
/// feature simply never fires — by design.
/// </summary>
public readonly record struct AttentionSignal(AttentionKind Kind, int Count)
{
    public static readonly AttentionSignal None = new(AttentionKind.None, 0);

    public bool HasAttention => Kind != AttentionKind.None;

    /// <summary>
    /// Parse a document title. Recognizes exactly one shape: optional
    /// leading whitespace, then "(digits)" followed by whitespace or
    /// the end of the title. "(0)" is no attention; non-numeric
    /// parentheses ("(Official) Muse") are prose and parse to None;
    /// a parenthesized number anywhere else in the title is ignored.
    /// </summary>
    public static AttentionSignal Parse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return None;
        var t = title.TrimStart();
        if (t.Length < 3 || t[0] != '(')
            return None;
        var close = t.IndexOf(')');
        if (close <= 1 || close > 5) // "(1)" .. "(1234)" at the widest
            return None;
        var digits = t.AsSpan(1, close - 1);
        foreach (var c in digits)
            if (!char.IsDigit(c))
                return None;
        // Must be followed by whitespace or be the whole title —
        // "(3)Muse" is a name, not a count badge.
        if (close + 1 < t.Length && !char.IsWhiteSpace(t[close + 1]))
            return None;
        if (!int.TryParse(digits, out var count) || count <= 0)
            return None;
        return new AttentionSignal(AttentionKind.Count, count);
    }
}

public enum AttentionKind
{
    None,

    /// <summary>Attention exists but no trustworthy count (reserved; the title parser never produces this today).</summary>
    Dot,

    /// <summary>A parsed "(N)" title count.</summary>
    Count,
}

/// <summary>
/// What the tray icon should paint (v0.8.0, S4 #31/#37): the decision
/// lives in Core so it is testable; the pixel geometry lives in the
/// shell renderer. The profile dot sits bottom-left, the attention
/// badge top-right — they never overlap, so both can show at once.
/// </summary>
public sealed record TrayIconPlan(bool ShowProfileDot, bool ShowBadge, string? BadgeText);

public static partial class TrayIconPlanner
{
    /// <summary>Badge label for a count: the number itself, capped at "9+" so it fits a 32px icon.</summary>
    public static string BadgeTextFor(int count) => count > 9 ? "9+" : count.ToString();

    public static TrayIconPlan Plan(bool hasProfileColor, AttentionSignal attention) =>
        attention.Kind switch
        {
            AttentionKind.Count => new TrayIconPlan(hasProfileColor, true, BadgeTextFor(attention.Count)),
            AttentionKind.Dot => new TrayIconPlan(hasProfileColor, true, null),
            _ => new TrayIconPlan(hasProfileColor, false, null),
        };

    /// <summary>
    /// Tooltip suffix for attention: a count may be named (it came from
    /// the page's own title), an uncounted dot is just "attention" —
    /// never an invented number.
    /// </summary>
    public static string TooltipSuffix(AttentionSignal attention) =>
        attention.Kind switch
        {
            AttentionKind.Count => $" · {attention.Count} unread",
            AttentionKind.Dot => " · attention",
            _ => "",
        };
}

/// <summary>How long the window may sit hidden in the tray before the web view suspends (v0.8.0, S4 #34).</summary>
public enum SuspendTimeout
{
    /// <summary>Never suspend from the tray timer.</summary>
    Never = 0,

    OneMinute = 1,
    FiveMinutes = 5,
    FifteenMinutes = 15,
}

public static class SuspendTimeouts
{
    /// <summary>The timer delay for an option; null = no timer ever (Never).</summary>
    public static TimeSpan? Delay(SuspendTimeout timeout) => timeout switch
    {
        SuspendTimeout.OneMinute => TimeSpan.FromMinutes(1),
        SuspendTimeout.FiveMinutes => TimeSpan.FromMinutes(5),
        SuspendTimeout.FifteenMinutes => TimeSpan.FromMinutes(15),
        _ => null,
    };

    /// <summary>Short label for menus/status ("5 min", "never").</summary>
    public static string Label(SuspendTimeout timeout) => timeout switch
    {
        SuspendTimeout.OneMinute => "1 min",
        SuspendTimeout.FiveMinutes => "5 min",
        SuspendTimeout.FifteenMinutes => "15 min",
        _ => "never",
    };
}

/// <summary>One tray-suspend decision (v0.8.0, S4 #34/#35), made in Core so every input is testable.</summary>
public enum SuspendDecision
{
    SuspendNow,
    Disabled,
    NeverSuspend,
    BlockedByDownload,
    BlockedByMicrophone,
}

public static class SuspendPolicy
{
    /// <summary>
    /// Precedence: the master switch beats everything; "never" beats
    /// the transient blocks (they'd only defer a suspend that must not
    /// happen anyway); a live download beats the microphone heuristic
    /// because it is a certainty, not a guess.
    /// </summary>
    public static SuspendDecision Decide(
        bool suspendEnabled, SuspendTimeout timeout, bool downloadsActive, bool microphoneGrantedThisSession)
    {
        if (!suspendEnabled)
            return SuspendDecision.Disabled;
        if (timeout == SuspendTimeout.Never)
            return SuspendDecision.NeverSuspend;
        if (downloadsActive)
            return SuspendDecision.BlockedByDownload;
        if (microphoneGrantedThisSession)
            return SuspendDecision.BlockedByMicrophone;
        return SuspendDecision.SuspendNow;
    }
}

/// <summary>
/// The in-app quiet-hours window (v0.8.0, S4 #38): minutes after local
/// midnight, start inclusive / end exclusive, wrapping past midnight
/// (22:00–07:00 is the classic shape). Equal start and end is an empty
/// window — never active — rather than a surprising all-day mute.
/// </summary>
public static class QuietHours
{
    public const int MinutesPerDay = 24 * 60;

    public static bool IsValidMinutes(int minutes) => minutes is >= 0 and < MinutesPerDay;

    /// <summary>Minutes after local midnight for a local time-of-day.</summary>
    public static int MinutesOfDay(DateTimeOffset local) => local.Hour * 60 + local.Minute;

    public static bool IsActive(int startMinutes, int endMinutes, int nowMinutes)
    {
        if (!IsValidMinutes(startMinutes) || !IsValidMinutes(endMinutes) || !IsValidMinutes(nowMinutes))
            return false;
        if (startMinutes == endMinutes)
            return false;
        if (startMinutes < endMinutes)
            return nowMinutes >= startMinutes && nowMinutes < endMinutes;
        // Wraps midnight: [start, 24:00) ∪ [00:00, end).
        return nowMinutes >= startMinutes || nowMinutes < endMinutes;
    }
}

/// <summary>Why a notification surface is quiet right now (v0.8.0, S4 #38); None = show it.</summary>
public enum NotificationSuppression
{
    None,
    WindowsDoNotDisturb,
    QuietHours,
}

public static class NotificationPolicy
{
    /// <summary>
    /// The one gate every MuseDesktop-owned notification surface
    /// (tray balloons, the taskbar attention flash) passes through.
    /// Windows' own Do Not Disturb state wins first — it is the
    /// system's word — then the in-app schedule. WebView2's own page
    /// toasts are NOT gated here: intercepting them would change
    /// muse.ai's notification behavior, which this app doesn't own
    /// (see DECISIONS.md).
    /// </summary>
    public static NotificationSuppression Evaluate(
        bool windowsDoNotDisturb, bool quietHoursEnabled,
        int quietStartMinutes, int quietEndMinutes, DateTimeOffset localNow)
    {
        if (windowsDoNotDisturb)
            return NotificationSuppression.WindowsDoNotDisturb;
        if (quietHoursEnabled && QuietHours.IsActive(quietStartMinutes, quietEndMinutes, QuietHours.MinutesOfDay(localNow)))
            return NotificationSuppression.QuietHours;
        return NotificationSuppression.None;
    }

    /// <summary>The log wording for a suppression — suppression is always announced, never silent.</summary>
    public static string ReasonText(NotificationSuppression suppression) => suppression switch
    {
        NotificationSuppression.WindowsDoNotDisturb => "Windows Do Not Disturb is on",
        NotificationSuppression.QuietHours => "quiet hours",
        _ => "",
    };
}

/// <summary>
/// Which launches begin hidden in the tray (v0.8.0, S4 #40): only
/// background (autostart-style) launches, and only when the user wants
/// tray starts. A manual launch always shows the window — "start
/// minimized" must never swallow a double-click the user just made.
/// </summary>
public static class LaunchVisibility
{
    public static bool ShouldStartHidden(bool backgroundLaunch, bool startMinimizedToTray) =>
        backgroundLaunch && startMinimizedToTray;
}

/// <summary>The tray menu's web-view status line (v0.8.0, S4 #32): plain words, composed in Core.</summary>
public static class TrayStatusText
{
    public static string SuspendLine(bool hidden, bool suspended, bool suspendEnabled, SuspendTimeout timeout)
    {
        if (suspended)
            return "Web view: suspended (memory saved)";
        if (!suspendEnabled)
            return "Web view: suspend off";
        if (timeout == SuspendTimeout.Never)
            return "Web view: stays awake in the tray";
        return hidden
            ? $"Web view: suspends after {SuspendTimeouts.Label(timeout)} hidden"
            : "Web view: awake";
    }
}
