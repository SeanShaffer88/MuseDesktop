using System.Windows;

namespace MuseDesktop;

/// <summary>
/// Quick note capture (R5 #148): one multiline box, Save files the
/// note under the active profile (Core's ProfileNotes owns the
/// store). Reached from the ⋯ menu, the command palette, and the
/// optional global hotkey — the same dialog in all three, so a note
/// taken from another app lands exactly where an in-app note does.
/// </summary>
public partial class QuickNoteWindow : Window
{
    public QuickNoteWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NoteText.Focus();
    }

    /// <summary>The captured text (valid when the dialog closed with Save).</summary>
    public string Text => NoteText.Text;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NoteText.Text))
        {
            NoteText.Focus();
            return;
        }
        DialogResult = true;
    }
}
