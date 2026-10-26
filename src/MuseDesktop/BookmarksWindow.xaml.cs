using System.Windows;
using System.Windows.Input;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>One bookmark row: the nickname (that was the point of requiring one) plus where it lives and when it was marked.</summary>
public sealed class BookmarkRow
{
    public BookmarkRow(MessageBookmark bookmark)
    {
        Bookmark = bookmark;
        Title = bookmark.Nickname;
        Detail = string.IsNullOrWhiteSpace(bookmark.ConversationTitle)
            ? $"In a conversation · saved {bookmark.AddedUtc.LocalDateTime:yyyy-MM-dd}"
            : $"{bookmark.ConversationTitle} · saved {bookmark.AddedUtc.LocalDateTime:yyyy-MM-dd}";
    }

    public MessageBookmark Bookmark { get; }
    public string Title { get; }
    public string Detail { get; }
    public string AutomationName => $"{Title}, {Detail}";
}

/// <summary>
/// The bookmarks list (R5B #202): the active profile's message
/// bookmarks, reached from the left rail's Bookmarks button, the
/// ⋯ menu, and the palette. All store work rides the delegates the
/// main window passes in — this window owns no paths, so the list it
/// shows is always the store's current truth (it re-reads after every
/// change). Jumping closes the window and lets the main window drive
/// (navigate, then find + highlight).
/// </summary>
public partial class BookmarksWindow : Window
{
    private readonly Func<IReadOnlyList<MessageBookmark>> _refresh;
    private readonly Action<MessageBookmark> _jump;
    private readonly Func<string, string, bool> _rename;
    private readonly Action<string> _remove;

    public BookmarksWindow(
        Func<IReadOnlyList<MessageBookmark>> refresh,
        Action<MessageBookmark> jump,
        Func<string, string, bool> rename,
        Action<string> remove)
    {
        _refresh = refresh;
        _jump = jump;
        _rename = rename;
        _remove = remove;
        InitializeComponent();
        Reload();
    }

    private void Reload(string? keepId = null)
    {
        BookmarkList.ItemsSource = _refresh().Select(b => new BookmarkRow(b)).ToList();
        if (keepId is not null)
            BookmarkList.SelectedItem = ((List<BookmarkRow>)BookmarkList.ItemsSource)
                .FirstOrDefault(r => r.Bookmark.Id == keepId);
        var empty = BookmarkList.Items.Count == 0;
        JumpButton.IsEnabled = RenameButton.IsEnabled = DeleteButton.IsEnabled = !empty;
        if (empty)
            StatusText.Text = "No bookmarks yet — hover a message in a conversation and click its bookmark button.";
    }

    private BookmarkRow? Selected => BookmarkList.SelectedItem as BookmarkRow;

    private void OnJump(object sender, RoutedEventArgs e) => JumpSelected();

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => JumpSelected();

    private void JumpSelected()
    {
        if (Selected is not { } row)
            return;
        Close();
        _jump(row.Bookmark);
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        // The nickname rule applies to renames too: a bookmark
        // cannot be saved without a name, so an empty answer is
        // refused here with the reason, keeping the old name.
        while (true)
        {
            var prompt = new TextPromptWindow("Rename bookmark",
                "The bookmark's nickname — it can't be empty.",
                row.Bookmark.Nickname, okText: "Rename") { Owner = this };
            if (FocusReturn.ShowDialog(prompt) != true)
                return;
            if (MessageBookmarks.CleanNickname(prompt.Text) is null)
            {
                StatusText.Text = "A bookmark needs a nickname — the old name was kept.";
                continue;
            }
            if (_rename(row.Bookmark.Id, prompt.Text))
            {
                StatusText.Text = "";
                Reload(row.Bookmark.Id);
            }
            return;
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        _remove(row.Bookmark.Id);
        Reload();
    }
}
