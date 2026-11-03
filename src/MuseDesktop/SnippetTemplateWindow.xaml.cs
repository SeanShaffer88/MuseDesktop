using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace MuseDesktop;

/// <summary>
/// Fill-in-the-blanks for a snippet template (v2.1.0): one labelled
/// single-line box per {{blank}} in the snippet's body (Core's
/// SnippetTemplate found them), and OK stays disabled until every
/// box holds non-whitespace text — so a confirmed dialog always
/// fills completely, and cancelling inserts nothing. The caller
/// applies the title-bar theme and shell text scaling, as with the
/// other small dialogs. The typed values are the user's own words:
/// they go to the composer via the caller and are never logged.
/// </summary>
public partial class SnippetTemplateWindow : Window
{
    private readonly List<(string Field, TextBox Box)> _boxes = new();

    public SnippetTemplateWindow(string snippetName, IReadOnlyList<string> fields)
    {
        InitializeComponent();
        IntroText.Text = Strings.Format("ui.snippet_blanks_intro", snippetName);
        var tabIndex = 1;
        foreach (var field in fields)
        {
            FieldsPanel.Children.Add(new TextBlock
            {
                Text = field,
                Margin = new Thickness(0, 12, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            });
            var box = new TextBox { TabIndex = tabIndex++ };
            box.SetResourceReference(TextBox.BackgroundProperty, "SurfaceBrush");
            box.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            box.SetResourceReference(TextBox.BorderBrushProperty, "HairlineBrush");
            AutomationProperties.SetName(box, field);
            box.TextChanged += (_, _) => RefreshOk();
            FieldsPanel.Children.Add(box);
            _boxes.Add((field, box));
        }
        // The XAML declares the buttons' tab positions (1, 2) for the
        // static audit; the real chain puts the field boxes first —
        // boxes 1..n above, then OK and Cancel after them.
        OkButton.TabIndex = _boxes.Count + 1;
        CancelButton.TabIndex = _boxes.Count + 2;
        Loaded += (_, _) => _boxes.FirstOrDefault().Box?.Focus();
    }

    /// <summary>The entered values, keyed by blank name (first-seen spelling), trimmed — meaningful only when the dialog returned true.</summary>
    public IReadOnlyDictionary<string, string> Values =>
        _boxes.ToDictionary(pair => pair.Field, pair => pair.Box.Text.Trim());

    private void RefreshOk() =>
        OkButton.IsEnabled = _boxes.All(pair => !string.IsNullOrWhiteSpace(pair.Box.Text));

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_boxes.Any(pair => string.IsNullOrWhiteSpace(pair.Box.Text)))
            return; // OK is disabled in this state; this is the belt to its braces
        DialogResult = true;
    }
}
