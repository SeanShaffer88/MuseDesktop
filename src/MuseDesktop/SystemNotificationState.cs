using Microsoft.Win32;

namespace MuseDesktop;

/// <summary>
/// Best-effort read of Windows' "toasts are off" state (S4 #38):
/// HKCU\...\Notifications\Settings, NOC_GLOBAL_SETTING_TOASTS_ENABLED
/// = 0. That value is what Focus Assist's "alarms only" mode and the
/// old Quiet Hours set, and what Do Not Disturb restores to 1 when it
/// ends. Honest limits: it is an undocumented-but-long-stable value,
/// Focus Assist's "priority only" mode does NOT flip it (priority
/// toasts still arrive), and any read failure simply means "not
/// detected" — the in-app quiet-hours schedule remains the precise,
/// always-available control.
/// </summary>
public static class SystemNotificationState
{
    private const string SettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string ToastsEnabledValue = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

    /// <summary>True only when the value is present and 0. Any doubt → false (don't suppress on a guess).</summary>
    public static bool DoNotDisturbActive()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: false);
            return key?.GetValue(ToastsEnabledValue) is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
