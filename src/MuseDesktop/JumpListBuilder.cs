using System.Windows.Shell;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The taskbar Jump List (S4 #33): right-click the taskbar button for
/// Open Muse, Settings, and one task per profile (most recently used
/// first). Tasks launch this exe with the S3/S4 command-line flags, so
/// they inherit the standing semantics: --profile persists as the
/// active profile, and while the app is already running a task only
/// wakes the running window (single-instance) — it cannot switch a
/// running instance's profile. Rebuilt whenever the profile list or
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
                Title = "Open Muse",
                Description = "Open MuseDesktop at your start page",
                ApplicationPath = exe,
                Arguments = "",
                IconResourcePath = exe,
            });
            list.JumpItems.Add(new JumpTask
            {
                Title = "Settings",
                Description = "Open MuseDesktop and its Settings window",
                ApplicationPath = exe,
                Arguments = "--settings",
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
                        ? $"{profile.Name} (current)"
                        : profile.Name,
                    Description = $"Open MuseDesktop with the {profile.Name} profile",
                    ApplicationPath = exe,
                    Arguments = $"--profile {profile.Id}",
                    IconResourcePath = exe,
                    CustomCategory = "Profiles",
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
