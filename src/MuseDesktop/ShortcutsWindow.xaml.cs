using System.Windows;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The keyboard shortcuts window (v0.10.0, S6 #52): Ctrl+/ (or "?")
/// opens this searchable list of every shortcut the shell binds. The
/// rows come from Core's <see cref="ShortcutCatalog"/> — the single
/// source of truth — with the user's remappings (#53) applied, so what
/// the window shows is what the keys actually do. Remappable rows say
/// where they can be changed (Settings → Keyboard &amp; accessibility).
/// </summary>
public partial class ShortcutsWindow : Window
{
    /// <summary>One display row.</summary>
    public sealed record Row(string Name, string Keys, string Kind);

    private readonly List<Row> _allRows = new();

    public ShortcutsWindow(AppSettings settings)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        ShellTextScaling.Apply(this, settings.ShellFontScale);

        var effective = ShortcutBindings.Effective(settings.ShortcutOverrides);
        foreach (var action in ShortcutCatalog.All)
        {
            if (action.Remappable && effective.TryGetValue(action.Id, out var gesture))
            {
                _allRows.Add(new Row(
                    action.Name,
                    gesture.DisplayText(),
                    "Can be changed in Settings"));
            }
            else
            {
                _allRows.Add(new Row(action.Name, action.DisplayGestures, "Fixed"));
            }
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchText.Text.Trim();
        var rows = string.IsNullOrEmpty(query)
            ? _allRows
            : _allRows.Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                  || r.Keys.Contains(query, StringComparison.OrdinalIgnoreCase))
                      .ToList();
        ShortcutList.ItemsSource = rows;
        SummaryText.Text = string.IsNullOrEmpty(query)
            ? "Every shortcut MuseDesktop itself binds. (Keys pressed inside the Muse page — like Enter to send — are Muse's own.) Three of these can be changed, in Settings → Keyboard & accessibility."
            : rows.Count == 0
                ? "No shortcut matches that search."
                : $"{rows.Count} of {_allRows.Count} shortcuts match.";
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ApplyFilter();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
