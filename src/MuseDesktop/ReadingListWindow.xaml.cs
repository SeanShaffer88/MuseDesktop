using System.Windows;
using System.Windows.Input;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>One reading-list row: the entry plus its display strings (state word + route + when it was saved).</summary>
public sealed class ReadingListRow
{
    public ReadingListRow(ReadingListEntry entry)
    {
        Entry = entry;
        Title = entry.Title;
        Detail = Strings.Format("reading.detail", Strings.Get(entry.IsRead ? "reading.read" : "reading.unread"), entry.Route, entry.AddedUtc.LocalDateTime);
    }

    public ReadingListEntry Entry { get; }
    public string Title { get; }
    public string Detail { get; }
    public string AutomationName => $"{Title}, {Detail}";
}

/// <summary>
/// The reading list (R5 #143): the active profile's saved pages.
/// All store work rides the delegates the main window passes in —
/// this window owns no paths of its own, so the list it shows is
/// always the store's current truth (it re-reads after every change).
/// </summary>
public partial class ReadingListWindow : Window
{
    private readonly Func<IReadOnlyList<ReadingListEntry>> _refresh;
    private readonly Action<string> _openRoute;
    private readonly Action<string, bool> _setRead;
    private readonly Action<string> _remove;

    public ReadingListWindow(
        Func<IReadOnlyList<ReadingListEntry>> refresh,
        Action<string> openRoute,
        Action<string, bool> setRead,
        Action<string> remove)
    {
        _refresh = refresh;
        _openRoute = openRoute;
        _setRead = setRead;
        _remove = remove;
        InitializeComponent();
        Reload();
    }

    private void Reload(string? keepId = null)
    {
        var rows = _refresh().Select(e => new ReadingListRow(e)).ToList();
        EntryList.ItemsSource = rows;
        EmptyStateText.Text = Strings.Get("pr20.your_reading_list_is_empty_save_a_page_from_the_menu_or_command_p");
        EmptyStateText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (keepId is not null)
            EntryList.SelectedItem = rows.FirstOrDefault(r => r.Entry.Id == keepId);
    }

    private ReadingListRow? Selected => EntryList.SelectedItem as ReadingListRow;

    private void OnOpen(object sender, RoutedEventArgs e) => OpenSelected();

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (Selected is not { } row)
            return;
        _setRead(row.Entry.Id, true); // opening a saved page reads it
        Close();
        _openRoute(row.Entry.Route);
    }

    private void OnToggleRead(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        _setRead(row.Entry.Id, !row.Entry.IsRead);
        Reload(row.Entry.Id);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        _remove(row.Entry.Id);
        Reload();
    }
}
