using Microsoft.Win32;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The <c>musedesktop://</c> registration (S11 #99): HKCU
/// <see cref="ProtocolLinks.ClassesKeyPath"/>, per-user, no admin.
/// Written by the installer at install time; this class keeps it in
/// sync with the Settings toggle — at startup and when Settings is
/// OK'd — following Core's <see cref="ProtocolLinks.DecideSync"/>:
/// register when missing, remove only our OWN registration, and never
/// touch one that points at a different program. The registered
/// command value itself is never logged (it embeds the install path);
/// states and actions are.
/// </summary>
public static class ProtocolRegistration
{
    private const string CommandSubKey = @"shell\open\command";

    /// <summary>What's actually registered for the scheme right now.</summary>
    public static ProtocolLinkState CurrentState()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                ProtocolLinks.ClassesKeyPath + "\\" + CommandSubKey);
            var command = key?.GetValue(null) as string;
            return ProtocolLinks.Classify(command, Environment.ProcessPath ?? "");
        }
        catch (Exception)
        {
            return ProtocolLinkState.NotRegistered;
        }
    }

    /// <summary>The Settings status line: Core's wording over the live state and the toggle.</summary>
    public static string StatusText(AppSettings settings) =>
        ProtocolLinks.StatusText(settings.RegisterProtocolLinks, CurrentState());

    /// <summary>
    /// Bring the registration in line with the toggle. Quiet when
    /// everything already agrees (the common startup); state changes
    /// log one line. Failures are logged, never fatal — links are a
    /// convenience, the app is not.
    /// </summary>
    public static void Sync(AppSettings settings, DiagnosticLog log)
    {
        try
        {
            var state = CurrentState();
            switch (ProtocolLinks.DecideSync(settings.RegisterProtocolLinks, state))
            {
                case ProtocolSyncAction.Register:
                    Register();
                    log.Info("musedesktop:// links registered to MuseDesktop (per-user)");
                    break;
                case ProtocolSyncAction.Unregister:
                    Registry.CurrentUser.DeleteSubKeyTree(ProtocolLinks.ClassesKeyPath, throwOnMissingSubKey: false);
                    log.Info("musedesktop:// link registration removed (links off in Settings)");
                    break;
                case ProtocolSyncAction.None:
                    if (state == ProtocolLinkState.RegisteredToAnotherApp)
                        log.Info("musedesktop:// links point at a different program; leaving that registration alone");
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warn($"musedesktop:// registration sync failed: {ex.Message}");
        }
    }

    private static void Register()
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("No process path to register.");
        using var root = Registry.CurrentUser.CreateSubKey(ProtocolLinks.ClassesKeyPath);
        root.SetValue(null, "URL:MuseDesktop Link");
        root.SetValue("URL Protocol", "");
        using var command = root.CreateSubKey(CommandSubKey);
        command.SetValue(null, ProtocolLinks.CommandValue(exe));
    }
}
