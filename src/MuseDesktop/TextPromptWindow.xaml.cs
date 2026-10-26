using System.Windows;

namespace MuseDesktop;

/// <summary>
/// One generic text prompt (R4): a label, a text box (single-line or
/// multi-line), OK/Cancel. It exists because the downloads round-2
/// features each need exactly one piece of typed input — a history
/// note (#137), an expected SHA-256 (#136), a pasted list of links
/// (#134) — and a shared, themed, focus-friendly prompt beats three
/// one-off windows. The text is the caller's problem: this window
/// validates nothing beyond handing it back on OK.
/// </summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string prompt, string initialText = "", bool multiline = false, string okText = "OK")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        OkButton.Content = okText;
        InputText.Text = initialText;
        if (multiline)
        {
            InputText.AcceptsReturn = true;
            InputText.TextWrapping = TextWrapping.Wrap;
            InputText.Height = 150;
            InputText.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        }
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) =>
        {
            InputText.Focus();
            InputText.SelectAll();
        };
    }

    /// <summary>The typed text (meaningful only when the dialog returned true).</summary>
    public string Text => InputText.Text;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
