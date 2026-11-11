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
    private readonly int _maxLength;
    public TextPromptWindow(string title, string prompt, string initialText = "", bool multiline = false,
        string? okText = null, int maxLength = 1_000_000)
    {
        _maxLength = Math.Clamp(maxLength, 1, 1_000_000);
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        OkButton.Content = okText ?? Strings.Get("ok");
        InputText.MaxLength = _maxLength;
        InputText.Text = initialText;
        RefreshCount();
        if (multiline)
        {
            InputText.AcceptsReturn = true;
            InputText.TextWrapping = TextWrapping.Wrap;
            InputText.MinHeight = 120;
            Height = 420; MinHeight = 280; SizeToContent = SizeToContent.Manual;
            InputText.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
            InputText.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter &&
                    System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
                { e.Handled = true; OnOk(this, new RoutedEventArgs()); }
            };
        }
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) =>
        {
            MaxHeight = Math.Max(220, SystemParameters.WorkArea.Height - 40);
            MinHeight = Math.Min(MinHeight, MaxHeight);
            InputText.Focus();
            InputText.SelectAll();
        };
    }

    /// <summary>The typed text (meaningful only when the dialog returned true).</summary>
    public string Text => InputText.Text;

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshCount();
    private void RefreshCount()
    {
        if (CharacterCount is null) return;
        CharacterCount.Text = Strings.Format("ui.input_count", InputText.Text.Length, _maxLength);
        System.Windows.Automation.AutomationProperties.SetHelpText(InputText, CharacterCount.Text);
    }
    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (InputText.Text.Length > _maxLength)
        { CharacterCount.Text = Strings.Get("ui.input_too_long"); InputText.Focus(); return; }
        DialogResult = true;
    }
}
