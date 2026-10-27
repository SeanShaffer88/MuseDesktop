using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The timers' status lines in the active shell language (R7 #161):
/// the localized twins of Core's FocusTimer/SessionTimer StatusLine
/// composers. The state and countdown math stay in Core (Remaining,
/// Phase, CycleInSet — the same inputs the Core lines use); only the
/// wording moves to the catalog. R7Tests pin the catalog's English
/// templates against the Core lines for representative states, so the
/// two can never drift apart silently.
/// </summary>
public static class TimerLines
{
    /// <summary>The focus timer's line (Core: FocusTimer.StatusLine).</summary>
    public static string Focus(FocusTimer timer, DateTimeOffset now) => timer.State switch
    {
        FocusTimerState.Focusing => Strings.Format("timer.focus_left", TimerText.Format(timer.Remaining(now))),
        FocusTimerState.BreakSuggested => Strings.Get("timer.focus_block_done"),
        FocusTimerState.OnBreak => Strings.Format("timer.break_left", TimerText.Format(timer.Remaining(now))),
        _ => "",
    };

    /// <summary>The session timer's line (Core: SessionTimer.StatusLine). Empty when the timer isn't running.</summary>
    public static string Session(SessionTimer timer, DateTimeOffset now)
    {
        if (!timer.IsRunning)
            return "";
        var remaining = TimerText.Format(timer.Remaining(now));
        if (timer.Phase == SessionPhase.Work)
            return timer.IsPaused
                ? Strings.Format("timer.session_work_paused", remaining, timer.CycleInSet + 1, timer.CyclesBeforeLongBreak)
                : Strings.Format("timer.session_work", remaining, timer.CycleInSet + 1, timer.CyclesBeforeLongBreak);
        return timer.IsPaused
            ? Strings.Format("timer.session_break_paused", remaining)
            : Strings.Format("timer.session_break", remaining);
    }
}
