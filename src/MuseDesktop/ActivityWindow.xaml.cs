using System.Windows;
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
            "Clear the recent-activity list on this PC? New notifications will still be recorded.",
            "Clear recent activity", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;
        if (ActivityHistoryStore.Clear(AppPaths.ActivityHistoryPath))
            Reload();
        else
            MessageBox.Show(this, "The recent-activity list couldn't be cleared.",
                "Clear recent activity", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
