using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The scheduled-command editor (R5 #147): pick a built-in action,
/// pick a trigger, give the time. Validation is Core's
/// (CommandScheduler.TryCreate) — this dialog only turns its answers
/// into that call and shows the error Core hands back. Editing an
/// existing rule pre-fills from it; the result keeps the rule's id,
/// and Settings keeps its firing bookkeeping when the command and
/// trigger are unchanged.
/// </summary>
public partial class ScheduleRuleWindow : Window
{
    private readonly ScheduledCommand? _existing;

    public ScheduleRuleWindow(ScheduledCommand? existing = null)
    {
        _existing = existing;
        InitializeComponent();
        ActionCombo.ItemsSource = CommandRegistry.BuiltinActions;
        OnceDateText.Text = DateTime.Now.ToString("yyyy-MM-dd");
        if (existing is not null)
        {
            ActionCombo.SelectedItem = CommandRegistry.BuiltinActions
                .FirstOrDefault(a => a.Id == existing.CommandId);
            TriggerCombo.SelectedIndex = existing.Trigger switch
            {
                ScheduleTriggerKind.DailyAt => 1,
                ScheduleTriggerKind.OnceAt => 2,
                _ => 0,
            };
            DailyTimeText.Text = $"{existing.DailyMinutes / 60:00}:{existing.DailyMinutes % 60:00}";
            if (existing.OnceAtUtc is { } at)
            {
                OnceDateText.Text = at.LocalDateTime.ToString("yyyy-MM-dd");
                OnceTimeText.Text = at.LocalDateTime.ToString("HH:mm");
            }
            EnabledCheck.IsChecked = existing.Enabled;
        }
        else
        {
            TriggerCombo.SelectedIndex = 0;
        }
        UpdatePanels();
    }

    /// <summary>The rule the user built (valid when the dialog closed with OK).</summary>
    public ScheduledCommand? Rule { get; private set; }

    private void OnTriggerChanged(object sender, SelectionChangedEventArgs e) => UpdatePanels();

    private void UpdatePanels()
    {
        if (DailyPanel is null || OncePanel is null)
            return;
        DailyPanel.Visibility = TriggerCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        OncePanel.Visibility = TriggerCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var action = ActionCombo.SelectedItem as PaletteCommand;
        var trigger = TriggerCombo.SelectedIndex switch
        {
            1 => ScheduleTriggerKind.DailyAt,
            2 => ScheduleTriggerKind.OnceAt,
            _ => ScheduleTriggerKind.OnStartup,
        };
        var dailyMinutes = 0;
        if (trigger == ScheduleTriggerKind.DailyAt
            && !TimeOfDayText.TryParseMinutes(DailyTimeText.Text, out dailyMinutes))
        {
            ErrorText.Text = "The daily time needs to be hours and minutes, like 09:30.";
            return;
        }
        DateTimeOffset? onceAt = null;
        if (trigger == ScheduleTriggerKind.OnceAt)
        {
            if (!TimeOfDayText.TryParseMinutes(OnceTimeText.Text, out var minutes)
                || !DateTime.TryParseExact(OnceDateText.Text.Trim(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                ErrorText.Text = "The one-off schedule needs a date like 2026-10-27 and a time like 09:30.";
                return;
            }
            var local = date.AddMinutes(minutes);
            onceAt = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }
        if (!CommandScheduler.TryCreate(action?.Id, trigger, dailyMinutes, onceAt,
                out var rule, out var error) || rule is null)
        {
            ErrorText.Text = error ?? "That schedule can't be saved.";
            return;
        }
        rule.Enabled = EnabledCheck.IsChecked == true;
        if (_existing is not null)
        {
            // Editing keeps the rule's identity — and its firing
            // bookkeeping, when what it runs and when didn't change.
            var sameRun = _existing.CommandId == rule.CommandId && _existing.Trigger == rule.Trigger;
            rule.Id = _existing.Id;
            rule.LastRunLocalDate = sameRun ? _existing.LastRunLocalDate : null;
            rule.HasFired = sameRun && _existing.HasFired;
        }
        Rule = rule;
        DialogResult = true;
    }
}
