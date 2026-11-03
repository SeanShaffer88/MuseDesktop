using System.Windows;
using System.Windows.Input;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>Which tab the Library window opens on.</summary>
public enum LibraryTab
{
    Snippets,
    Briefs,
    ReadingList,
    Pinned,
}

/// <summary>
/// The Library (v2.1.0, design Concept B): everything the user
/// keeps, in one window — the snippet library, the standing
/// briefs, the reading list, and the pinned conversations, one
/// tab each. The snippets, reading-list, and pinned tabs are
/// second doors onto the SAME stores their first doors use
/// (same files, same delegates discipline) — never copies that
/// could drift.
/// The briefs tab is its own store (Core <see cref="StandingBriefs"/>),
/// edited live like the snippets: each save lands as it's made.
///
/// Starting a conversation with a brief rides the delegate the
/// main window passes in — this window owns no web view; the main
/// window composes the brief, opens the fresh conversation, and
/// lands the text in its message box, unsent.
/// </summary>
public partial class LibraryWindow : Window
{
    private readonly string _profileId;
    private readonly Action<StandingBrief> _startConversationWithBrief;
    private readonly Func<IReadOnlyList<ReadingListEntry>> _readingRefresh;
    private readonly Action<string> _readingOpenRoute;
    private readonly Action<string, bool> _readingSetRead;
    private readonly Action<string> _readingRemove;
    private readonly Func<IReadOnlyList<PinnedConversation>> _pinsRefresh;
    private readonly Action<PinnedConversation> _pinOpen;
    private readonly Action<string> _pinUnpin;

    /// <summary>The snippet currently loaded in the editor, if any.</summary>
    private string? _selectedSnippetId;

    /// <summary>The brief currently loaded in the editor, if any.</summary>
    private string? _selectedBriefId;

    public LibraryWindow(
        string profileId,
        LibraryTab initialTab,
        Action<StandingBrief> startConversationWithBrief,
        Func<IReadOnlyList<ReadingListEntry>> readingRefresh,
        Action<string> readingOpenRoute,
        Action<string, bool> readingSetRead,
        Action<string> readingRemove,
        Func<IReadOnlyList<PinnedConversation>> pinsRefresh,
        Action<PinnedConversation> pinOpen,
        Action<string> pinUnpin)
    {
        _profileId = profileId;
        _startConversationWithBrief = startConversationWithBrief;
        _readingRefresh = readingRefresh;
        _readingOpenRoute = readingOpenRoute;
        _readingSetRead = readingSetRead;
        _readingRemove = readingRemove;
        _pinsRefresh = pinsRefresh;
        _pinOpen = pinOpen;
        _pinUnpin = pinUnpin;
        InitializeComponent();
        SnippetList.DisplayMemberPath = nameof(PromptSnippet.Name);
        BriefList.DisplayMemberPath = nameof(StandingBrief.Name);
        RefreshSnippetList();
        RefreshBriefList();
        ReloadReadingList();
        ReloadPins();
        LibraryTabs.SelectedIndex = initialTab switch
        {
            LibraryTab.Briefs => 1,
            LibraryTab.ReadingList => 2,
            LibraryTab.Pinned => 3,
            _ => 0,
        };
    }

    // ─────────────────────── snippets ───────────────────────
    // Mirrors the Settings editor (R5B #206) deliberately: same
    // store, same live-save semantics, same status wording — the
    // two doors can never disagree about what a save means.

    private void RefreshSnippetList()
    {
        var snippets = PromptSnippetStore.Load(AppPaths.SnippetsPath).EntriesFor(_profileId);
        SnippetList.ItemsSource = snippets;
        var keep = _selectedSnippetId is null
            ? null
            : snippets.FirstOrDefault(s => s.Id == _selectedSnippetId);
        SnippetList.SelectedItem = keep;
        if (keep is null && _selectedSnippetId is not null)
        {
            _selectedSnippetId = null;
            SnippetNameText.Text = "";
            SnippetBodyText.Text = "";
        }
        DeleteSnippetButton.IsEnabled = keep is not null;
    }

    private void OnSnippetSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SnippetList.SelectedItem is not PromptSnippet snippet)
            return; // losing the selection (a refresh) never wipes the editor
        _selectedSnippetId = snippet.Id;
        SnippetNameText.Text = snippet.Name;
        SnippetBodyText.Text = snippet.Body;
        DeleteSnippetButton.IsEnabled = true;
        SnippetStatus.Text = "";
    }

    private void OnSaveSnippet(object sender, RoutedEventArgs e)
    {
        var all = PromptSnippetStore.Load(AppPaths.SnippetsPath);
        var now = DateTimeOffset.UtcNow;
        PromptSnippet? added = null;
        var ok = _selectedSnippetId is { } id && all.Find(_profileId, id) is not null
            ? all.Update(_profileId, id, SnippetNameText.Text, SnippetBodyText.Text, now)
            : (added = all.Add(_profileId, SnippetNameText.Text, SnippetBodyText.Text, now)) is not null;
        if (!ok)
        {
            SnippetStatus.Text = PromptSnippets.CleanName(SnippetNameText.Text) is null
                ? Strings.Get("set.snippet_name")
                : Strings.Format("set.snippet_text", PromptSnippets.MaxBodyLength);
            return;
        }
        PromptSnippetStore.Save(AppPaths.SnippetsPath, all);
        if (added is not null)
            _selectedSnippetId = added.Id;
        SnippetStatus.Text = Strings.Get("ui.snippet_saved_it_s_in_ctrl_k_and_the");
        RefreshSnippetList();
    }

    private void OnNewSnippet(object sender, RoutedEventArgs e)
    {
        _selectedSnippetId = null;
        SnippetList.SelectedItem = null;
        SnippetNameText.Text = "";
        SnippetBodyText.Text = "";
        SnippetStatus.Text = "";
        SnippetNameText.Focus();
    }

    private void OnDeleteSnippet(object sender, RoutedEventArgs e)
    {
        if (_selectedSnippetId is not { } id)
            return;
        var all = PromptSnippetStore.Load(AppPaths.SnippetsPath);
        if (all.Remove(_profileId, id))
        {
            PromptSnippetStore.Save(AppPaths.SnippetsPath, all);
            _selectedSnippetId = null;
            SnippetNameText.Text = "";
            SnippetBodyText.Text = "";
            SnippetStatus.Text = Strings.Get("ui.snippet_deleted");
        }
        RefreshSnippetList();
    }

    // ─────────────────────── briefs ───────────────────────

    private void RefreshBriefList(string? keepId = null)
    {
        var briefs = StandingBriefStore.Load(AppPaths.BriefsPath).Entries;
        BriefList.ItemsSource = briefs;
        BriefEmptyText.Visibility = briefs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var keep = (keepId ?? _selectedBriefId) is { } id
            ? briefs.FirstOrDefault(b => b.Id == id)
            : null;
        BriefList.SelectedItem = keep;
        if (keep is null && _selectedBriefId is not null)
        {
            _selectedBriefId = null;
            ClearBriefEditor();
        }
        RefreshBriefButtons();
    }

    private void RefreshBriefButtons()
    {
        var hasSelection = _selectedBriefId is not null;
        NewConversationButton.IsEnabled = hasSelection;
        DuplicateBriefButton.IsEnabled = hasSelection;
        DeleteBriefButton.IsEnabled = hasSelection;
    }

    private void ClearBriefEditor()
    {
        BriefNameText.Text = "";
        BriefBackgroundText.Text = "";
        BriefRulesText.Text = "";
        BriefPreferencesText.Text = "";
    }

    private void OnBriefSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (BriefList.SelectedItem is not StandingBrief brief)
            return; // losing the selection (a refresh) never wipes the editor
        _selectedBriefId = brief.Id;
        BriefNameText.Text = brief.Name;
        BriefBackgroundText.Text = brief.Background;
        BriefRulesText.Text = brief.Rules;
        BriefPreferencesText.Text = brief.Preferences;
        BriefStatus.Text = "";
        RefreshBriefButtons();
    }

    private void OnSaveBrief(object sender, RoutedEventArgs e)
    {
        var all = StandingBriefStore.Load(AppPaths.BriefsPath);
        var now = DateTimeOffset.UtcNow;
        StandingBrief? added = null;
        var ok = _selectedBriefId is { } id && all.Find(id) is not null
            ? all.Update(id, BriefNameText.Text, BriefBackgroundText.Text,
                BriefRulesText.Text, BriefPreferencesText.Text, now)
            : (added = all.Add(BriefNameText.Text, BriefBackgroundText.Text,
                BriefRulesText.Text, BriefPreferencesText.Text, now)) is not null;
        if (!ok)
        {
            BriefStatus.Text = Strings.Format("library.brief_error", StandingBriefs.MaxSectionLength);
            return;
        }
        StandingBriefStore.Save(AppPaths.BriefsPath, all);
        if (added is not null)
            _selectedBriefId = added.Id;
        BriefStatus.Text = Strings.Get("library.brief_saved");
        RefreshBriefList();
    }

    private void OnNewBrief(object sender, RoutedEventArgs e)
    {
        _selectedBriefId = null;
        BriefList.SelectedItem = null;
        ClearBriefEditor();
        BriefStatus.Text = "";
        RefreshBriefButtons();
        BriefNameText.Focus();
    }

    private void OnDuplicateBrief(object sender, RoutedEventArgs e)
    {
        if (_selectedBriefId is not { } id)
            return;
        var all = StandingBriefStore.Load(AppPaths.BriefsPath);
        var copy = all.Duplicate(id, Strings.Get("brief.copy_suffix"), DateTimeOffset.UtcNow);
        if (copy is null)
        {
            BriefStatus.Text = Strings.Format("library.brief_error", StandingBriefs.MaxSectionLength);
            return;
        }
        StandingBriefStore.Save(AppPaths.BriefsPath, all);
        _selectedBriefId = copy.Id;
        BriefStatus.Text = Strings.Get("library.brief_saved");
        RefreshBriefList(copy.Id);
    }

    private void OnDeleteBrief(object sender, RoutedEventArgs e)
    {
        if (_selectedBriefId is not { } id)
            return;
        if (StandingBriefStore.Load(AppPaths.BriefsPath).Find(id) is not { } brief)
            return;
        var invoker = FocusReturn.Capture();
        var confirm = System.Windows.MessageBox.Show(this,
            Strings.Format("brief.delete_body", brief.Name),
            Strings.Get("brief.delete_title"),
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            FocusReturn.Restore(invoker);
            return;
        }
        var all = StandingBriefStore.Load(AppPaths.BriefsPath);
        if (all.Remove(id))
            StandingBriefStore.Save(AppPaths.BriefsPath, all);
        _selectedBriefId = null;
        ClearBriefEditor();
        BriefStatus.Text = "";
        RefreshBriefList();
    }

    private void OnNewConversationWithBrief(object sender, RoutedEventArgs e)
    {
        if (_selectedBriefId is not { } id)
            return;
        // The store's copy, not the editor's: unsaved edits are the
        // editor's business until Save — what starts the
        // conversation is the brief as it stands in the library.
        if (StandingBriefStore.Load(AppPaths.BriefsPath).Find(id) is not { } brief || brief.IsEmpty)
            return;
        Close();
        _startConversationWithBrief(brief);
    }

    // ─────────────────────── reading list ───────────────────────
    // The Reading list window's behavior, on the same delegates.

    private void ReloadReadingList(string? keepId = null)
    {
        var rows = _readingRefresh().Select(entry => new ReadingListRow(entry)).ToList();
        EntryList.ItemsSource = rows;
        ReadingEmptyText.Text = Strings.Get("pr20.your_reading_list_is_empty_save_a_page_from_the_menu_or_command_p");
        ReadingEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (keepId is not null)
            EntryList.SelectedItem = rows.FirstOrDefault(r => r.Entry.Id == keepId);
    }

    private ReadingListRow? SelectedReadingRow => EntryList.SelectedItem as ReadingListRow;

    private void OnReadingOpen(object sender, RoutedEventArgs e) => OpenSelectedReading();

    private void OnReadingDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedReading();

    private void OpenSelectedReading()
    {
        if (SelectedReadingRow is not { } row)
            return;
        _readingSetRead(row.Entry.Id, true); // opening a saved page reads it
        Close();
        _readingOpenRoute(row.Entry.Route);
    }

    private void OnReadingToggleRead(object sender, RoutedEventArgs e)
    {
        if (SelectedReadingRow is not { } row)
            return;
        _readingSetRead(row.Entry.Id, !row.Entry.IsRead);
        ReloadReadingList(row.Entry.Id);
    }

    private void OnReadingRemove(object sender, RoutedEventArgs e)
    {
        if (SelectedReadingRow is not { } row)
            return;
        _readingRemove(row.Entry.Id);
        ReloadReadingList();
    }

    // ─────────────────────── pinned conversations ───────────────────────
    // The ⋯ menu's pin list, on the same delegates discipline:
    // this tab is a second door onto the same store the menu's
    // toggle and submenu use — never a copy that could drift.

    private void ReloadPins(string? keepId = null)
    {
        var rows = _pinsRefresh().Select(pin => new PinnedConversationRow(pin)).ToList();
        PinList.ItemsSource = rows;
        PinnedEmptyText.Text = Strings.Get("library.pinned_empty");
        PinnedEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (keepId is not null)
            PinList.SelectedItem = rows.FirstOrDefault(r => r.Pin.Id == keepId);
    }

    private PinnedConversationRow? SelectedPinRow => PinList.SelectedItem as PinnedConversationRow;

    private void OnPinOpen(object sender, RoutedEventArgs e) => OpenSelectedPin();

    private void OnPinDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedPin();

    private void OpenSelectedPin()
    {
        if (SelectedPinRow is not { } row)
            return;
        Close();
        _pinOpen(row.Pin);
    }

    private void OnPinUnpin(object sender, RoutedEventArgs e)
    {
        if (SelectedPinRow is not { } row)
            return;
        _pinUnpin(row.Pin.Id);
        ReloadPins();
    }
}

/// <summary>One pinned-conversation row: the pin plus its display strings (title, address path + pinned date, automation name).</summary>
public sealed class PinnedConversationRow
{
    public PinnedConversationRow(PinnedConversation pin)
    {
        Pin = pin;
        Title = TitleFor(pin);
        Detail = Strings.Format("pin.detail", AppRoute.FromUrl(pin.Url) ?? pin.Url, pin.PinnedUtc.LocalDateTime);
    }

    public PinnedConversation Pin { get; }
    public string Title { get; }
    public string Detail { get; }
    public string AutomationName => $"{Title}, {Detail}";

    /// <summary>A pin's display title: the title frozen at pin time, or the dated label when no title could be derived then.</summary>
    public static string TitleFor(PinnedConversation pin) =>
        string.IsNullOrWhiteSpace(pin.Title)
            ? Strings.Format("pin.untitled", pin.PinnedUtc.LocalDateTime)
            : pin.Title;
}
