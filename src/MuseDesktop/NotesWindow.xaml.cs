using System.Windows;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>One notes row: the note text plus its local timestamp.</summary>
public sealed class NoteRow
{
    public NoteRow(NoteEntry note)
    {
        Note = note;
        Text = note.Text;
        When = note.CreatedUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    }

    public NoteEntry Note { get; }
    public string Text { get; }
    public string When { get; }
    public string AutomationName => $"{Text}, {When}";
}

/// <summary>
/// The notebook (R5 #148): the active profile's quick notes.
/// Store work rides delegates from the main window, like the
/// reading list — the window re-reads after every change.
/// </summary>
public partial class NotesWindow : Window
{
    private readonly Func<IReadOnlyList<NoteEntry>> _refresh;
    private readonly Action _newNote;
    private readonly Action<string> _delete;

    public NotesWindow(
        Func<IReadOnlyList<NoteEntry>> refresh,
        Action newNote,
        Action<string> delete,
        string? selectedNoteId = null)
    {
        _refresh = refresh;
        _newNote = newNote;
        _delete = delete;
        InitializeComponent();
        Reload(selectedNoteId);
    }

    /// <summary>Re-read the notebook (the main window calls this after a capture it hosted).</summary>
    public void Reload(string? selectedNoteId = null)
    {
        var rows = _refresh().Select(n => new NoteRow(n)).ToList();
        NoteList.ItemsSource = rows;
        EmptyStateText.Text = Strings.Get("pr20.no_notes_yet_choose_new_note_or_use_the_quick_note_shortcut_to_ca");
        EmptyStateText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selectedNoteId is not null
            && rows.FirstOrDefault(row => row.Note.Id == selectedNoteId) is { } selected)
        {
            NoteList.SelectedItem = selected;
            NoteList.ScrollIntoView(selected);
        }
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        _newNote();
        Reload();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (NoteList.SelectedItem is not NoteRow row)
            return;
        _delete(row.Note.Id);
        Reload();
    }
}
