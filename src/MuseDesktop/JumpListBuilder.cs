using System.Windows.Shell;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The taskbar Jump List (S4 #33): right-click the taskbar button for
/// Open Muse, Settings, Quick dictate (S11 #97), and one task per
/// profile (most recently used first). Tasks launch this exe with the
/// command-line flags, so they ride the one intent surface (S11
/// #100): on a cold start --profile persists as the active profile;
/// on a running instance the flags arrive as a LaunchIntent — a
/// profile task asks before switching (the usual confirm + restart),
/// never switches silently, and while the app is locked every task
/// waits behind the lock. Rebuilt whenever the profile list or
/// active profile changes; every failure is a log line.
/// </summary>
public static class JumpListBuilder
{
    /// <summary>Jump Lists get crowded fast; the most recent handful of profiles is plenty.</summary>
    private const int MaxProfileTasks = 8;

    public static void Apply(AppSettings settings, DiagnosticLog log)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return;

            var list = new JumpList
            {
                ShowFrequentCategory = false,
                ShowRecentCategory = false,
            };

            list.JumpItems.Add(new JumpTask
            {
                Title = Strings.Get("ui.open_muse"),
                Description = Strings.Get("ui.jump_open_desc"),
                ApplicationPath = exe,
                Arguments = "",
                IconResourcePath = exe,
            });
            list.JumpItems.Add(new JumpTask
            {
                Title = Strings.Get("ui.settings"),
                Description = Strings.Get("ui.jump_settings_desc"),
                ApplicationPath = exe,
                Arguments = "--settings",
                IconResourcePath = exe,
            });
            list.JumpItems.Add(new JumpTask
            {
                Title = Strings.Get("ui.quick_dictate"),
                Description = Strings.Get("ui.jump_dictate_desc"),
                ApplicationPath = exe,
                Arguments = "--dictate",
                IconResourcePath = exe,
            });

            // Profiles: most recently used first, never-launched last
            // (stable within each group by list order).
            var ordered = settings.Profiles
                .OrderByDescending(p => p.LastUsedUtc.HasValue)
                .ThenByDescending(p => p.LastUsedUtc ?? DateTimeOffset.MinValue)
                .Take(MaxProfileTasks);
            foreach (var profile in ordered)
            {
                list.JumpItems.Add(new JumpTask
                {
                    Title = profile.Id == settings.ActiveProfileId
                        ? Strings.Format("ui.profile_current_suffix", profile.Name)
                        : profile.Name,
                    Description = Strings.Format("ui.jump_profile_desc", profile.Name),
                    ApplicationPath = exe,
                    Arguments = $"--profile {profile.Id}",
                    IconResourcePath = exe,
                    CustomCategory = Strings.Get("jump.profiles"),
                });
            }

            JumpList.SetJumpList(System.Windows.Application.Current, list);
            list.Apply();
        }
        catch (Exception ex)
        {
            log.Warn($"Jump List update failed: {ex.Message}");
        }
    }
}
