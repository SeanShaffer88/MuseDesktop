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
                Strings.Format("reset.backup_saved", backupName);
        }
        catch (Exception ex)
        {
            _backupOk = false;
            BackupStatusText.Text =
                Strings.Format("reset.backup_failed", ex.Message);
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
            steps.Add(Strings.Get("ui.reset_step_settings"));
        if (clearData)
            steps.Add(Strings.Get("ui.reset_step_clear_data"));
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Format("msg.run_reset_body", string.Join("\n", steps)),
            Strings.Get("msg.reset_musedesktop"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
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
            results.Add(Strings.Get("ui.reset_result_settings"));
            _log.Info("Settings reset to defaults from the reset wizard (backup written first)");
        }
        if (clearData)
        {
            if (_privacy is null)
            {
                results.Add(Strings.Get("ui.reset_result_clear_failed"));
            }
            else
            {
                var targets = _working.Profiles.Select(p => (p.Id, p.Name)).ToList();
                var result = await _privacy.ClearAsync(ClearDataChoice.AllSiteData, targets);
                results.Add(result.Summary);
            }
        }
        ResultText.Text = string.Join("\n", results) +
            Strings.Get("ui.reset_result_suffix");
    }
}
