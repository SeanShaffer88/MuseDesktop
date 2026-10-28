using System.Text.Json;

namespace MuseDesktop.Core;

// ─────────────────────── approval toast (R8) ───────────────────────

/// <summary>
/// The fixed texts of the approval toast (R8): when Core's
/// <see cref="ApprovalAttentionPolicy"/> returns a beep verdict and
/// the toast channel is on, the shell shows a real Windows toast
/// through the vendored NotifyKit DLL. Everything the DLL is told
/// is built here — the tray configuration (which app identity the
/// toast carries) and the show request — so the strings are pinned
/// by tests and the shell stays a thin P/Invoke wrapper. The toast
/// is display-only, exactly like the beep it rides with: noticing
/// the card is all it does, and no toast can answer one.
/// </summary>
public static class ApprovalToastMessages
{
    /// <summary>
    /// The app identity (AppUserModelID) the toasts are shown under.
    /// Windows only delivers toasts for an identity it knows, so the
    /// shell stamps this id onto the app's Start Menu shortcut (see
    /// the shell's AppUserModelId). NotifyKit is told the same id in
    /// the tray configuration.
    /// </summary>
    public const string AppId = "SeanShaffer88.MuseDesktop";

    /// <summary>The tray configuration JSON handed to NotifyKit when the toast channel first fires: no tray icon of its own (hwnd 0), this app's identity, and the app name as the tray tooltip.</summary>
    public static string BuildTrayConfig() =>
        JsonSerializer.Serialize(new
        {
            hwnd = 0,
            app_id = AppId,
            tooltip = "MuseDesktop",
        });

    /// <summary>The show-request JSON handed to NotifyKit for one approval alert: a fixed title and body — what the card said never crosses into the notification.</summary>
    public static string BuildShowJson() =>
        JsonSerializer.Serialize(new
        {
            title = "Approval needed",
            body = "Muse is waiting for your approval in MuseDesktop.",
        });
}
