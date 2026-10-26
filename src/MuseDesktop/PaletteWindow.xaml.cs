using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MuseDesktop.Core;
using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MuseDesktop;

/// <summary>
/// One palette row: the command plus its group ("Recent" for
/// history-pinned results, else the command's category) and its
/// profile color dot, resolved by the main window — this window
/// knows no profile state of its own.
/// </summary>
public sealed class PaletteRow
{
    public PaletteRow(PaletteResult result, string? colorHex)
    {
        Command = result.Command;
        Group = result.IsRecent ? PaletteText.RecentGroup : result.Command.Category;
        if (colorHex is not null)
        {
            try
            {
                DotBrush = new SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex));
            }
            catch { /* an unparsable hex just means no dot */ }
        }
    }

    public PaletteCommand Command { get; }
    public string Group { get; }
    public string Title => Command.Title;
    public string? Detail => Command.Detail;
    public string? Gesture => Command.GestureText;
    public Brush? DotBrush { get; }
    public Visibility DotVisibility => DotBrush is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>What a screen reader calls this row (R1 #107): the command and its context, never just the bare title.</summary>
    public string AutomationName => Detail is null ? Title : $"{Title}, {Detail}";
}

/// <summary>
/// The command palette (R1 #101): Ctrl+K, keyboard-first. The search
/// box owns the keyboard — arrows move the selection without leaving
/// it, Enter runs, Esc closes — and every keystroke re-ranks through
/// Core's one fuzzy matcher. Recent actions pin to the top on an
/// empty query (the "Recent" group). Accessibility (#107): every
/// control and row carries a screen-reader name (#107), selection
/// changes are announced through the app's live regions with Core's
/// wording, the colors are the shared theme brushes (so the window
/// follows the app theme like every other shell window), and the
/// window has NO animations at all — reduce-motion is honored by
/// construction rather than by another setting to keep in sync.
/// The compact form (tray middle-click, #110) is this same window,
/// smaller.
/// </summary>
public partial class PaletteWindow : Window
{
    private readonly CommandPalette _palette;
    private readonly ActionHistory _history;
    private readonly Action<PaletteCommand> _execute;
    private readonly Func<PaletteCommand, string?> _colorFor;

    public PaletteWindow(
        CommandPalette palette,
        ActionHistory history,
        Action<PaletteCommand> execute,
        Func<PaletteCommand, string?>? colorFor = null,
        bool compact = false)
    {
        _palette = palette;
        _history = history;
        _execute = execute;
        _colorFor = colorFor ?? (_ => null);
        InitializeComponent();
        if (compact)
        {
            Width = 500;
            Height = 360;
        }
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) =>
        {
            Refill();
            SearchText.Focus();
        };
    }

    private void Refill()
    {
        var results = _palette.Search(SearchText.Text, _history);
        var rows = results.Select(r => new PaletteRow(r, _colorFor(r.Command))).ToList();
        var view = CollectionViewSource.GetDefaultView(rows);
        view.GroupDescriptions.Clear();
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PaletteRow.Group)));
        ResultList.ItemsSource = view;
        SummaryText.Text = PaletteText.ResultSummary(rows.Count, SearchText.Text);
        if (rows.Count > 0)
            ResultList.SelectedIndex = 0;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refill();

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveSelection(5);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveSelection(-5);
                e.Handled = true;
                break;
            case Key.Enter:
                ExecuteSelected();
                e.Handled = true;
                break;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ExecuteSelected();
            e.Handled = true;
        }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => ExecuteSelected();

    private void MoveSelection(int delta)
    {
        var count = ResultList.Items.Count;
        if (count == 0)
            return;
        var next = Math.Clamp(ResultList.SelectedIndex + delta, 0, count - 1);
        ResultList.SelectedIndex = next;
        ResultList.ScrollIntoView(ResultList.SelectedItem);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Screen readers hear the selection move (R1 #107) — the main
        // window's live regions carry it, in Core's exact wording.
        if (ResultList.SelectedItem is PaletteRow row && ResultList.Items.Count > 0)
            Announcer.Polite(PaletteText.SelectionAnnouncement(
                row.Command, ResultList.SelectedIndex, ResultList.Items.Count));
    }

    private void ExecuteSelected()
    {
        if (ResultList.SelectedItem is not PaletteRow row)
            return;
        var command = row.Command;
        Close();
        _execute(command);
    }
}
