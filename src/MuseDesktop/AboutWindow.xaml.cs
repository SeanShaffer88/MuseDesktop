using System.IO;
using System.Windows;

namespace MuseDesktop;

/// <summary>The facts the About window shows (v0.14.0, S10 #94), collected by the main window.</summary>
public sealed record AboutFacts(
    string Version,
    string ChannelLabel,
    string Architecture,
    string WindowsVersion,
    string DotNetVersion,
    string WebViewVersion,
    string SettingsPath,
    string LogPath,
    string ProfileDir);

/// <summary>
/// The About window (v0.14.0, S10 #94): version, release channel,
/// architecture, Windows / .NET / WebView2 versions, the data paths,
/// a Check-for-updates button routed into the S9 update flow, and the
/// licenses view — MuseDesktop's own MIT license plus the
/// third-party notices for the WebView2 SDK and .NET, both shipped as
/// content files beside the exe so what's installed is what's shown.
/// Replaces the pre-S10 MessageBox, which showed a subset of this in
/// text nobody could copy.
/// </summary>
public partial class AboutWindow : Window
{
    private readonly Action _checkForUpdates;
    private bool _licensesShown;

    public AboutWindow(AboutFacts facts, Action checkForUpdates)
    {
        _checkForUpdates = checkForUpdates;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        FactsText.Text =
            $"Version: {facts.Version}\n" +
            $"Release channel: {facts.ChannelLabel}\n" +
            $"Architecture: {facts.Architecture}\n" +
            $"Windows: {facts.WindowsVersion}\n" +
            $".NET: {facts.DotNetVersion}\n" +
            $"WebView2 runtime: {facts.WebViewVersion}\n" +
            "\n" +
            $"Settings: {facts.SettingsPath}\n" +
            $"Log: {facts.LogPath}\n" +
            $"Web profile: {facts.ProfileDir}";
        LicensesText.Text = LoadLicensesText();
    }

    /// <summary>
    /// The licenses view content: this app's LICENSE plus
    /// THIRD-PARTY-NOTICES.txt, both copied beside the exe at publish.
    /// A missing file degrades to a pointer at the repo copies instead
    /// of an empty pane.
    /// </summary>
    private static string LoadLicensesText()
    {
        var parts = new List<string>();
        foreach (var name in new[] { "LICENSE", "THIRD-PARTY-NOTICES.txt" })
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, name);
                parts.Add(File.Exists(path)
                    ? File.ReadAllText(path).Trim()
                    : $"({name} isn't beside the app in this install — the same file is in the MuseDesktop repository.)");
            }
            catch (Exception ex)
            {
                parts.Add($"({name} couldn't be read: {ex.Message})");
            }
        }
        return string.Join("\n\n----------------------------------------\n\n", parts);
    }

    private void OnToggleLicenses(object sender, RoutedEventArgs e)
    {
        _licensesShown = !_licensesShown;
        FactsText.Visibility = _licensesShown ? Visibility.Collapsed : Visibility.Visible;
        LicensesText.Visibility = _licensesShown ? Visibility.Visible : Visibility.Collapsed;
        LicensesButton.Content = _licensesShown ? "Back to About" : "Licenses & notices";
    }

    private void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        // Close first so the update window (if one is offered) isn't
        // stacked behind this dialog; the owner runs the S9 flow.
        Close();
        _checkForUpdates();
    }
}
