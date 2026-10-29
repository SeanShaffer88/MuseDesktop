using System.Windows;
using MessageBox = System.Windows.MessageBox;
using MuseDesktop.Core;

namespace MuseDesktop;

public sealed class ActivityRow
{
    public ActivityRow(ActivityRecord record)
    {
        Category = record.Category;
        Message = record.Message;
        When = record.When;
    }

    public string Category { get; }
    public string Message { get; }
    public string When { get; }
    public string AutomationName => $"{Category}: {Message}, {When}";
}

/// <summary>Revisitable summaries of recent app-owned tray notifications; content is fixed and privacy-safe in Core.</summary>
public partial class ActivityWindow : Window
{
    public ActivityWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Reload();
    }

    private void Reload()
    {
        var rows = ActivityHistoryStore.Load(AppPaths.ActivityHistoryPath)
            .Entries.Select(entry => new ActivityRow(entry)).ToList();
        ActivityList.ItemsSource = rows;
        EmptyStateText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            Strings.Get("pr20.clear_the_recent_activity_list_on_this_pc_new_notifications_will"),
            Strings.Get("pr20.clear_recent_activity"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;
        if (ActivityHistoryStore.Clear(AppPaths.ActivityHistoryPath))
            Reload();
        else
            MessageBox.Show(this, Strings.Get("pr20.the_recent_activity_list_couldn_t_be_cleared"),
                Strings.Get("pr20.clear_recent_activity"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
