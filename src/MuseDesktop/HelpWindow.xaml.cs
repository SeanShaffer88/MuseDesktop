using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The offline Help window (v0.14.0, S10 #91): the troubleshooting
/// pages from Core's <see cref="HelpCatalog"/>, with bodies read from
/// the shipped Help.md (the same content-file pattern as What's New —
/// a Core test pins catalog and file together). Each page's action
/// button is a callback into the main window (open Settings at a
/// section, open Diagnostics, open the log viewer): this window owns
/// no app state of its own. The single online element is the GitHub
/// issues link, labeled as such; the pages themselves never phone out.
/// </summary>
public partial class HelpWindow : Window
{
    private readonly string _markdown;
    private readonly Action<HelpPage> _onAction;
    private HelpPage? _current;

    public HelpWindow(string helpMarkdown, Action<HelpPage> onAction, string? initialPageId = null)
    {
        _markdown = helpMarkdown;
        _onAction = onAction;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        RefillList(SearchText.Text);
        // R1: a caller (the palette) can land on a specific page —
        // the commands page most often — instead of page one.
        if (initialPageId is not null && PageList.ItemsSource is IEnumerable<HelpPage> pages)
        {
            var index = pages.ToList().FindIndex(p => p.Id == initialPageId);
            if (index >= 0)
                PageList.SelectedIndex = index;
        }
    }

    private void RefillList(string? query)
    {
        var pages = HelpCatalog.Search(query);
        PageList.ItemsSource = pages;
        SummaryText.Text = string.IsNullOrWhiteSpace(query)
            ? Strings.Get("these_pages_live_inside_the_app_no_internet")
            : pages.Count == 0
                ? Strings.Get("help.no_match")
                : Strings.Format("help.matching_pages", pages.Count);
        if (pages.Count > 0)
            PageList.SelectedIndex = 0;
        else
        {
            _current = null;
            BodyText.Text = "";
            ActionButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => RefillList(SearchText.Text);

    private void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        _current = PageList.SelectedItem as HelpPage;
        if (_current is null)
            return;
        var body = HelpCatalog.ExtractBody(_markdown, _current)
                   ?? Strings.Get("help.body_missing");
        // R1 #109: the commands page's list is GENERATED from the
        // palette registry at the moment it's shown — the registry is
        // the single source, so the page can never drift from what
        // Ctrl+K actually offers.
        if (_current.Id == HelpCatalog.CommandsPageId)
            body += "\n\n" + CommandsHelp.Render(CommandRegistry.HelpListing());
        BodyText.Text = body;
        if (_current.Action != HelpAction.None && _current.ActionLabel is not null)
        {
            ActionButton.Content = _current.ActionLabel;
            ActionButton.Visibility = Visibility.Visible;
        }
        else
        {
            ActionButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnAction(object sender, RoutedEventArgs e)
    {
        if (_current is not null)
            _onAction(_current);
    }

    private void OnReportLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* no default browser — the address is visible in the text either way */ }
        e.Handled = true;
    }
}
