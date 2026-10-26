using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MuseDesktop.Core;
using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MuseDesktop;

/// <summary>One switcher row: a window descriptor plus its profile color dot, resolved here from the Core palette key.</summary>
public sealed class SwitcherRow
{
    public SwitcherRow(WindowDescriptor window)
    {
        Window = window;
        if (ProfileColors.HexFor(window.ProfileColorKey) is { } hex)
        {
            try
            {
                DotBrush = new SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            }
            catch { /* an unparsable hex just means no dot */ }
        }
    }

    public WindowDescriptor Window { get; }
    public string Title => string.IsNullOrWhiteSpace(Window.Title) ? "Muse window" : Window.Title;
    // R3 #121: the profile's avatar glyph rides the descriptor and
    // leads the profile name here, like every other switcher surface.
    public string ProfileLabel => Window.ProfileName is null
        ? (Window.IsMain ? "Main window" : "Second window")
        : $"{(Window.IsMain ? "Main window" : "Second window")} · profile {(WorkspaceGlyphs.SymbolFor(Window.ProfileGlyphKey) is { } symbol ? symbol + " " : "")}{Window.ProfileName}";
    public Brush? DotBrush { get; }
    public Visibility DotVisibility => DotBrush is null ? Visibility.Collapsed : Visibility.Visible;
    public string AutomationName => $"{Title}, {ProfileLabel}";
}

/// <summary>
/// The window switcher overlay (R1 #103): every open window in
/// most-recently-used order (Core's <see cref="WindowMruList"/> did
/// the sorting before this window existed), each with its profile
/// color dot and page title. The previous window is preselected —
/// the overwhelmingly common switch is "back to the one I was just
/// in". Arrow keys + Enter switch; Esc cancels; the list and every
/// row carry screen-reader names like the palette's.
/// </summary>
public partial class SwitcherWindow : Window
{
    private readonly Action<string> _onChosen;

    public SwitcherWindow(IReadOnlyList<WindowDescriptor> windowsMru, Action<string> onChosen)
    {
        _onChosen = onChosen;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        WindowList.ItemsSource = windowsMru.Select(w => new SwitcherRow(w)).ToList();
        WindowList.SelectedIndex = WindowMruList.DefaultSelectionIndex(windowsMru.Count);
        Loaded += (_, _) =>
        {
            WindowList.Focus();
            if (WindowList.SelectedItem is not null)
                WindowList.ScrollIntoView(WindowList.SelectedItem);
        };
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ChooseSelected();
            e.Handled = true;
        }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => ChooseSelected();

    private void ChooseSelected()
    {
        if (WindowList.SelectedItem is not SwitcherRow row)
            return;
        var id = row.Window.Id;
        Close();
        _onChosen(id);
    }
}
