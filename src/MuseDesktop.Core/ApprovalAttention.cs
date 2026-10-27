namespace MuseDesktop.Core;

// ─────────────────────── approval attention (R7C) ───────────────────────

/// <summary>
/// The beep policy for page approval cards (R7C, SEAN-directed):
/// when the hosted Muse page shows an approval card — a
/// permission prompt waiting on the user's click — while the user
/// is looking elsewhere, the shell beeps once and flashes the
/// taskbar button. The watcher that feeds this policy only ever
/// OBSERVES the page; nothing in this feature can approve, deny,
/// or dismiss anything, and the click always remains the user's.
///
/// The policy is a small state machine fed with presence reports
/// from the page bridge:
///
///   card gone ──ObserveAppeared──▶ the appearance is judged once ──▶ beep or not
///   card present ──ObserveGone──▶ re-armed for the next card
///
/// An appearance is judged at the moment it is first reported:
/// the setting must be on, the window must not be the active one,
/// and the global cooldown since the last beep must have elapsed.
/// Whatever the verdict, that card never beeps again — a card that
/// arrived while the user was already looking at the window does
/// not start beeping when they tab away, and a card the cooldown
/// suppressed does not queue a late beep. Only a disappearance
/// re-arms the policy.
/// </summary>
public sealed class ApprovalAttentionPolicy
{
    /// <summary>Short global cooldown between beeps, however the cards arrive: an alert, never a machine gun.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

    private bool _cardPresent;
    private DateTimeOffset? _lastBeepAt;

    /// <summary>Whether a card is currently reported present (diagnostics/tests).</summary>
    public bool CardPresent => _cardPresent;

    /// <summary>
    /// Report that an approval card has appeared. Returns true
    /// exactly once per card — at its first report — and only when
    /// the setting is on, the window is not active, and the
    /// cooldown since the last beep has elapsed. Repeat reports
    /// for the same card always return false.
    /// </summary>
    public bool ObserveAppeared(bool settingOn, bool windowActive, DateTimeOffset now)
    {
        if (_cardPresent)
            return false; // same card — its appearance was already judged
        _cardPresent = true;
        if (!settingOn || windowActive)
            return false;
        if (_lastBeepAt is { } last && now - last < Cooldown)
            return false;
        _lastBeepAt = now;
        return true;
    }

    /// <summary>Report that the card is gone: re-arm, so the NEXT card's appearance is judged fresh.</summary>
    public void ObserveGone() => _cardPresent = false;
}
