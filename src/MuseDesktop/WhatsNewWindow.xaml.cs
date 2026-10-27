using System.Windows;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The What's New window (v0.13.0, S9 #82): shown once on the first
/// launch of a new version, with that version's notes (the matching
/// CHANGELOG section, shipped as WhatsNew.md beside the exe). The
/// caller owns the decision (Core WhatsNew.Decide) and the settings
/// writes; this window just shows text and reports the opt-out.
/// </summary>
public partial class WhatsNewWindow : Window
{
    public WhatsNewWindow(string version, string bodyMarkdown)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        HeadlineText.Text = Strings.Format("ui.whats_new_headline", version);
        BodyText.Text = bodyMarkdown;
    }

    /// <summary>True when the user ticked "Don't show What's New again" before closing.</summary>
    public bool NeverShowAgain => NeverShowCheck.IsChecked == true;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
