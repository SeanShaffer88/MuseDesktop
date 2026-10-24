using System.IO;
using System.Windows;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The guided reset (S8 #72): "MuseDesktop won't start / won't sign
/// in" as a three-step walk — (1) an export backup of the current
/// settings, written before anything can change and required before
/// the settings step can run (the S5 #42 rule: no backup, no reset);
/// (2) the choices, each explained; (3) run, reusing the existing
/// machinery rather than duplicating it — settings go through
/// <see cref="SettingsReset.ApplyDefaults"/> on the Settings window's
/// working clone (OK there still adopts), site data through the
/// privacy gateway's granular clear (#61) for all profiles.
/// </summary>
public partial class ResetWizardWindow : Window
{
    private readonly AppSettings _working;
    private readonly WebPrivacyGateway? _privacy;
    private readonly DiagnosticLog _log;
    private bool _backupOk;

    /// <summary>True when the settings step ran — the Settings window reloads its controls after this dialog.</summary>
    public bool SettingsWereReset { get; private set; }

    public ResetWizardWindow(AppSettings working, WebPrivacyGateway? privacy, DiagnosticLog log)
    {
        _working = working;
        _privacy = privacy;
        _log = log;
        InitializeComponent();
        Loaded += OnLoadedWriteBackup;
    }

    /// <summary>Step 1 runs on open: the backup is written before the user can choose anything destructive.</summary>
    private void OnLoadedWriteBackup(object sender, RoutedEventArgs e)
    {
        var backupName = SettingsReset.BackupFileName(DateTimeOffset.Now);
        var backupPath = Path.Combine(AppPaths.RootDir, backupName);
        try
        {
            Directory.CreateDirectory(AppPaths.RootDir);
            File.WriteAllText(backupPath, SettingsStore.Export(_working));
            _backupOk = true;
            BackupStatusText.Text =
                $"Your current settings were saved to {backupName} in the settings folder. " +
                "If this reset makes things worse, Settings → Data → Import settings… brings them back.";
        }
        catch (Exception ex)
        {
            _backupOk = false;
            BackupStatusText.Text =
                $"The backup couldn't be written ({ex.Message}), so the settings reset is unavailable — " +
                "MuseDesktop never resets settings without a backup. Clearing site data below still works.";
            _log.Warn($"Reset wizard backup failed: {ex.Message}");
        }
        UpdateRunButton();
    }

    private void OnChoiceChanged(object sender, RoutedEventArgs e) => UpdateRunButton();

    private void UpdateRunButton()
    {
        if (RunButton is null)
            return;
        var anyChoice = (ResetSettingsCheck.IsChecked == true && _backupOk)
                        || ClearDataCheck.IsChecked == true;
        RunButton.IsEnabled = anyChoice;
        if (ResetSettingsCheck.IsChecked == true && !_backupOk)
            ResetSettingsCheck.IsEnabled = false;
    }

    private async void OnRunReset(object sender, RoutedEventArgs e)
    {
        var resetSettings = ResetSettingsCheck.IsChecked == true && _backupOk;
        var clearData = ClearDataCheck.IsChecked == true;
        var steps = new List<string>();
        if (resetSettings)
            steps.Add("• Reset your settings to defaults (backup already saved)");
        if (clearData)
            steps.Add("• Clear site data for ALL profiles — every profile is signed out of Muse");
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            "Run the reset now?\n\n" + string.Join("\n", steps),
            "Reset MuseDesktop", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        FocusReturn.Restore(invoker);
        if (confirm != MessageBoxResult.Yes)
            return;

        RunButton.IsEnabled = false;
        ResetSettingsCheck.IsEnabled = false;
        ClearDataCheck.IsEnabled = false;
        var results = new List<string>();
        if (resetSettings)
        {
            SettingsReset.ApplyDefaults(_working);
            SettingsWereReset = true;
            results.Add("Settings are back to defaults (choose OK in Settings to apply them).");
            _log.Info("Settings reset to defaults from the reset wizard (backup written first)");
        }
        if (clearData)
        {
            if (_privacy is null)
            {
                results.Add("Site data couldn't be cleared — the web view isn't running yet.");
            }
            else
            {
                var targets = _working.Profiles.Select(p => (p.Id, p.Name)).ToList();
                var result = await _privacy.ClearAsync(ClearDataChoice.AllSiteData, targets);
                results.Add(result.Summary);
            }
        }
        ResultText.Text = string.Join("\n", results) +
            "\nIf Muse still won't sign in, the backup guide in Settings → Data explains what else can be copied aside.";
    }
}
