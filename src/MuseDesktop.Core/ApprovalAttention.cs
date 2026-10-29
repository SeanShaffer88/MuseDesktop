namespace MuseDesktop.Core;

// ─────────────────────── approval attention (R7C; rebuilt R7C2) ───────────────────────

/// <summary>
/// Which watcher rule recognised the approval card (R7C2). The
/// watcher reports the signal's NAME across the bridge so a field
/// report can be diagnosed from the log; no card text ever crosses.
/// </summary>
public enum ApprovalDetectionSignal
{
    /// <summary>No signal (no card, or a report that named none).</summary>
    Unknown,

    /// <summary>The primary rule (R7C2): a visible button whose whole label starts with "Allow" / "Always allow" — the rule from the user's own browser alert script, proven on the real page.</summary>
    AllowPrefix,

    /// <summary>The back-up rule (the R7C original): a visible approve-action + deny-action pair inside one small container.</summary>
    ButtonPair,
}

/// <summary>
/// What the beep policy decided (R7C2). Returned — instead of a bare
/// bool — so the shell can log every beep and every suppression
/// WITH its reason: the v1.8.3 failure was silent in the field
/// precisely because nothing recorded why no beep happened.
/// </summary>
public enum ApprovalBeepOutcome
{
    /// <summary>Sound the beep now (a first beep or a due reminder).</summary>
    Beep,

    /// <summary>No beep: the beep setting is off.</summary>
    SuppressedSettingOff,

    /// <summary>No beep: the user is looking at the window. During the reminder phase this also ends the cycle for the card.</summary>
    SuppressedWindowActive,

    /// <summary>No beep: inside the global cooldown since the last beep (appearance judgments only).</summary>
    SuppressedCooldown,

    /// <summary>No beep: this card's appearance was already reported and judged.</summary>
    AlreadyPresent,

    /// <summary>No beep: the card is waiting and alerting, but the reminder interval has not elapsed yet.</summary>
    RepeatNotDue,

    /// <summary>No beep: a card is present, but it never entered the alerting state (its appearance was suppressed, or its reminder cycle already ended), so it never reminds.</summary>
    NotAlerting,

    /// <summary>No beep: no card is present at all.</summary>
    NoCard,
}

/// <summary>
/// The beep policy for page approval cards (R7C, SEAN-directed;
/// reminders + outcome reporting added by R7C2): when the hosted
/// Muse page shows an approval card — a permission prompt waiting
/// on the user's click — while the user is looking elsewhere, the
/// shell beeps and flashes the taskbar button, and keeps reminding
/// every <see cref="RepeatInterval"/> while the card waits. The
/// watcher that feeds this policy only ever OBSERVES the page;
/// nothing in this feature can approve, deny, or dismiss anything,
/// and the click always remains the user's.
///
/// The policy is a small state machine fed with presence reports
/// from the page bridge:
///
///   card gone ──ObserveAppeared──▶ the appearance is judged once ──▶ beep or suppressed
///   card alerting ──ObserveStillPresent (shell tick)──▶ a reminder beep every 20 s
///   card present ──ObserveGone──▶ re-armed for the next card
///
/// An appearance is judged at the moment it is first reported:
/// the setting must be on, the window must not be the active one,
/// and the global cooldown since the last beep must have elapsed.
/// A card whose appearance was suppressed never makes a sound —
/// a card that arrived while the user was already looking at the
/// window does not start beeping when they tab away, and a card
/// the cooldown suppressed does not queue a late beep.
///
/// A card whose appearance DID beep enters the alerting state:
/// while it stays present, the shell's tick asks for reminders.
/// A reminder is due every <see cref="RepeatInterval"/> since the
/// last beep, and only while the setting is still on and the
/// window still isn't active. The window gaining focus — or the
/// setting being turned off — ends the cycle for that card for
/// good (the user has seen it, or has said stop); the card
/// clearing always ends it. Only a disappearance re-arms the
/// policy for the NEXT card.
/// </summary>
public sealed class ApprovalAttentionPolicy
{
    /// <summary>Short global cooldown between beeps, however the cards arrive: an alert, never a machine gun.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

    /// <summary>How often a waiting card re-beeps while the user is away (R7C2). The user's own browser script pings every 6 s; the app uses a politer 20 s — see DECISIONS.</summary>
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromSeconds(20);

    private bool _cardPresent;
    private bool _alerting;
    private ApprovalDetectionSignal _signal = ApprovalDetectionSignal.Unknown;
    private DateTimeOffset? _lastBeepAt;

    /// <summary>Whether a card is currently reported present (diagnostics/tests).</summary>
    public bool CardPresent => _cardPresent;

    /// <summary>Whether the present card beeped on arrival and is in its reminder cycle (diagnostics/tests).</summary>
    public bool IsAlerting => _alerting;

    /// <summary>Which watcher rule reported the present card (Unknown when no card is present).</summary>
    public ApprovalDetectionSignal CurrentSignal => _signal;

    /// <summary>
    /// Report that an approval card has appeared, with the signal
    /// that recognised it. The appearance is judged exactly once:
    /// <see cref="ApprovalBeepOutcome.Beep"/> only when the setting
    /// is on, the window is not active, and the cooldown since the
    /// last beep has elapsed — otherwise the matching suppression
    /// outcome. Repeat reports for the same card return
    /// <see cref="ApprovalBeepOutcome.AlreadyPresent"/>.
    /// </summary>
    public ApprovalBeepOutcome ObserveAppeared(
        ApprovalDetectionSignal signal, bool settingOn, bool windowActive, DateTimeOffset now)
    {
        if (_cardPresent)
            return ApprovalBeepOutcome.AlreadyPresent; // same card — its appearance was already judged
        _cardPresent = true;
        _signal = signal;
        _alerting = false;
        if (!settingOn)
            return ApprovalBeepOutcome.SuppressedSettingOff;
        if (windowActive)
            return ApprovalBeepOutcome.SuppressedWindowActive;
        if (_lastBeepAt is { } last && now - last < Cooldown)
            return ApprovalBeepOutcome.SuppressedCooldown;
        _lastBeepAt = now;
        _alerting = true;
        return ApprovalBeepOutcome.Beep;
    }

    /// <summary>
    /// The shell's reminder tick (R7C2): the card is still there —
    /// is a reminder beep due? Only a card in the alerting state
    /// can remind, only while the setting is on and the window is
    /// not active, and only once <see cref="RepeatInterval"/> has
    /// passed since the last beep. Observing the window active or
    /// the setting off ends the card's reminder cycle (the outcome
    /// says which); later ticks for that card return
    /// <see cref="ApprovalBeepOutcome.NotAlerting"/>.
    /// </summary>
    public ApprovalBeepOutcome ObserveStillPresent(bool settingOn, bool windowActive, DateTimeOffset now)
    {
        if (!_cardPresent)
            return ApprovalBeepOutcome.NoCard;
        if (!_alerting)
            return ApprovalBeepOutcome.NotAlerting;
        if (!settingOn)
        {
            _alerting = false;
            return ApprovalBeepOutcome.SuppressedSettingOff;
        }
        if (windowActive)
        {
            _alerting = false;
            return ApprovalBeepOutcome.SuppressedWindowActive;
        }
        if (_lastBeepAt is { } last && now - last < RepeatInterval)
            return ApprovalBeepOutcome.RepeatNotDue;
        _lastBeepAt = now;
        return ApprovalBeepOutcome.Beep;
    }

    /// <summary>Report that the card is gone: re-arm, so the NEXT card's appearance is judged fresh. The global cooldown clock keeps running — it spans cards by design.</summary>
    public void ObserveGone()
    {
        _cardPresent = false;
        _alerting = false;
        _signal = ApprovalDetectionSignal.Unknown;
    }
}
