using System.Windows;
using System.Windows.Controls;
using MuseDesktop.Core;
using Brush = System.Windows.Media.Brush;

namespace MuseDesktop;

/// <summary>
/// The weekly digest + stats dashboard (R5 #146/#150): one window,
/// two tabs. The digest is Core's rendered text verbatim. The
/// dashboard draws one bar per metric per profile — the lengths are
/// Core's fractions (each metric scaled to the week's best), so
/// this window does no math beyond turning a fraction into pixels.
/// Clearing the stats confirms first, deletes through the caller's
/// delegate, and re-renders from the (now empty) source.
/// </summary>
public partial class ProductivityWindow : Window
{
    private const double MaxBarWidth = 260;

    private readonly Func<StatsReport> _statsSource;
    private readonly Action _clearStats;

    public ProductivityWindow(bool showStats, string digestText,
        Func<StatsReport> statsSource, Action clearStats)
    {
        _statsSource = statsSource;
        _clearStats = clearStats;
        InitializeComponent();
        DigestText.Text = digestText;
        Tabs.SelectedIndex = showStats ? 1 : 0;
        BuildStats();
    }

    private void BuildStats()
    {
        StatsPanel.Children.Clear();
        var report = _statsSource();
        StatsPanel.Children.Add(new TextBlock
        {
            Text = $"Week of {report.WeekStartUtc.LocalDateTime:MMMM d, yyyy} — counted on this PC only.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        if (report.Rows.Count == 0)
        {
            StatsPanel.Children.Add(new TextBlock
            {
                Text = "No profiles yet — nothing to count.",
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        var accent = (Brush)FindResource("AccentBrush");
        var track = (Brush)FindResource("HairlineBrush");
        foreach (var row in report.Rows)
        {
            StatsPanel.Children.Add(new TextBlock
            {
                Text = row.Name,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(0, 8, 0, 4),
            });
            AddBar("Launches", row.Launches.ToString(), row.LaunchFraction, accent, track);
            AddBar("Downloads", row.Downloads.ToString(), row.DownloadFraction, accent, track);
            AddBar("Focus time", TimerText.FormatMinutes(row.FocusMinutes), row.FocusFraction, accent, track);
        }
        StatsPanel.Children.Add(new TextBlock
        {
            Text = $"Totals: {report.TotalLaunches} launches · {report.TotalDownloads} downloads · " +
                   $"{TimerText.FormatMinutes(report.TotalFocusMinutes)} focused. " +
                   "Clearing deletes the tally file — the counts are all it holds.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });
    }

    private void AddBar(string label, string value, double fraction, Brush fill, Brush track)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        var labelBlock = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);
        var barTrack = new Border
        {
            Background = track,
            Height = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Width = MaxBarWidth,
        };
        var barFill = new Border
        {
            Background = fill,
            Height = 12,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Width = Math.Max(0, Math.Min(1, fraction)) * MaxBarWidth,
        };
        var holder = new Grid();
        holder.Children.Add(barTrack);
        holder.Children.Add(barFill);
        Grid.SetColumn(holder, 1);
        grid.Children.Add(holder);
        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(valueBlock, 2);
        grid.Children.Add(valueBlock);
        StatsPanel.Children.Add(grid);
    }

    private void OnClearStats(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(this,
            "Delete the weekly stats tallies? The counts are gone for good; " +
            "your settings, notes, and reading list are not touched.",
            "Clear stats data", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
            return;
        _clearStats();
        BuildStats();
    }
}
