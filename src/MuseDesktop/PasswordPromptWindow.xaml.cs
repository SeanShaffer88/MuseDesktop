using System.Windows;

namespace MuseDesktop;

/// <summary>
/// The password prompt for encrypted settings exports (v0.11.0,
/// S7 #65): a password (typed twice) when encrypting, a single
/// password when opening an encrypted import. The password itself is
/// never stored anywhere — it lives in this window until OK, and in
/// the caller's local variable just long enough to run the crypto.
/// </summary>
public partial class PasswordPromptWindow : Window
{
    private readonly bool _confirm;
    private readonly int _minLength;
    private readonly int _maxLength;

    /// <param name="minLength">Shortest accepted password (1 for export/decrypt prompts; the app-lock prompt passes AppLock's 4).</param>
    /// <param name="maxLength">Longest accepted password (0 = no limit).</param>
    public PasswordPromptWindow(string prompt, bool confirm, int minLength = 1, int maxLength = 0)
    {
        _confirm = confirm;
        _minLength = Math.Max(1, minLength);
        _maxLength = maxLength;
        InitializeComponent();
        PromptText.Text = prompt;
        ConfirmPanel.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) => PasswordEntry.Focus();
    }

    /// <summary>The typed password (valid only when the dialog returned true).</summary>
    public string Password => PasswordEntry.Password;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordEntry.Password))
        {
            StatusText.Text = "Type a password first.";
            return;
        }
        if (PasswordEntry.Password.Length < _minLength)
        {
            StatusText.Text = $"Use at least {_minLength} characters.";
            return;
        }
        if (_maxLength > 0 && PasswordEntry.Password.Length > _maxLength)
        {
            StatusText.Text = $"Use at most {_maxLength} characters.";
            return;
        }
        if (_confirm && ConfirmEntry.Password != PasswordEntry.Password)
        {
            StatusText.Text = "The two passwords don't match. Type them again.";
            ConfirmEntry.Clear();
            return;
        }
        DialogResult = true;
    }
}
