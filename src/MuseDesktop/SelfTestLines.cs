using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The guided self-test result lines in the active shell language
/// (R7 #161): the localized twins of Core's GuidedSelfTests lines.
/// Same inputs, same branches — only the wording moves to the
/// catalog. R7Tests pin the catalog texts against the Core lines for
/// every outcome, so the two can never drift apart silently.
/// </summary>
public static class SelfTestLines
{
    /// <summary>The microphone test's result line (Core: GuidedSelfTests.MicrophoneLine).</summary>
    public static string Microphone(MicTestOutcome outcome) => outcome switch
    {
        MicTestOutcome.HeardSound => Strings.Get("selftest.mic.heard"),
        MicTestOutcome.GrantedButSilent => Strings.Get("selftest.mic.silent"),
        MicTestOutcome.Denied => Strings.Get("selftest.mic.denied"),
        MicTestOutcome.NoDevice => Strings.Get("selftest.mic.nodevice"),
        MicTestOutcome.TimedOut => Strings.Get("selftest.mic.timeout"),
        _ => Strings.Get("selftest.mic.error"),
    };

    /// <summary>The saved-microphone-choice line (Core: GuidedSelfTests.SavedMicLine).</summary>
    public static string SavedMic(string? savedState) => savedState is null
        ? Strings.Get("selftest.mic.saved_none")
        : Strings.Format("selftest.mic.saved_choice", savedState);

    /// <summary>The test-notification result line (Core: GuidedSelfTests.NotificationLine).</summary>
    public static string Notification(NotificationSuppression suppression, bool shown)
    {
        if (!shown)
            return Strings.Get("selftest.notification.not_shown");
        if (suppression == NotificationSuppression.None)
            return Strings.Get("selftest.notification.sent");
        var reason = suppression == NotificationSuppression.WindowsDoNotDisturb
            ? Strings.Get("selftest.reason.dnd")
            : Strings.Get("selftest.reason.quiet_hours");
        return Strings.Format("selftest.notification.sent_despite", reason);
    }
}
