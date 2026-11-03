using System.Globalization;
using System.IO;
using System.Windows;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>One context-pack row: the pack's name plus its size and last change, in the user's local time.</summary>
public sealed class ContextPackRow
{
    public ContextPackRow(ContextPack pack)
    {
        Pack = pack;
        Title = pack.Name;
        Detail = Strings.Format(
            "context_pack.pack_detail",
            pack.Items.Count,
            pack.UpdatedUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));
    }

    public ContextPack Pack { get; }
    public string Title { get; }
    public string Detail { get; }
    public string AutomationName => $"{Title}, {Detail}";
}

/// <summary>One context-pack item row: a kind label, the name the user will recognize in the composed block, and a one-line preview or location.</summary>
public sealed class ContextPackItemRow
{
    public ContextPackItemRow(ContextPackItem item, IReadOnlyDictionary<string, PromptSnippet> snippets)
    {
        Item = item;
        switch (item.Kind)
        {
            case ContextPackItemKind.Note:
                KindLabel = Strings.Get("context_pack.note_kind");
                Title = item.Title;
                Detail = Preview(item.Text);
                break;
            case ContextPackItemKind.Snippet:
                KindLabel = Strings.Get("context_pack.snippet");
                if (snippets.TryGetValue(item.SnippetId, out var snippet))
                {
                    Title = snippet.Name;
                    Detail = Preview(snippet.Body);
                }
                else
                {
                    Title = Strings.Get("context_pack.missing_snippet");
                    Detail = Strings.Get("context_pack.snippet_unavailable");
                }
                break;
            case ContextPackItemKind.File:
            default:
                KindLabel = Strings.Get("context_pack.file");
                Title = FileTitle(item.Path);
                Detail = item.Path;
                break;
        }
    }

    public ContextPackItem Item { get; }
    public string KindLabel { get; }
    public string Title { get; }
    public string Detail { get; }
    public string AutomationName => $"{KindLabel}: {Title}. {Detail}";

    private static string FileTitle(string path)
    {
        var trimmed = path.Trim().TrimEnd('\\', '/');
        var name = trimmed.Length == 0 ? "" : System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }

    private static string Preview(string text)
    {
        var clean = string.Join(" ", text.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length <= 120 ? clean : clean[..120].TrimEnd() + "…";
    }
}

/// <summary>
/// The context packs manager (v2.1.0, Concept A): a modeless
/// two-pane window — packs on the left, the selected pack's items
/// on the right. All store work rides the delegates the main window
/// passes in, so this window owns no paths and always shows the
/// store's current truth. Starting a conversation also rides a
/// delegate: the main window composes the pack, opens the tab, and
/// reports whether the text landed; this window closes only when
/// it did. Deleting a pack asks first and can be undone from this
/// window until it closes — the deleted pack is held in memory
/// only, never written anywhere else.
/// </summary>
public partial class ContextPacksWindow : Window
{
    private readonly Func<ContextPackLibrary> _load;
    private readonly Action<ContextPackLibrary> _save;
    private readonly Func<IReadOnlyList<PromptSnippet>> _snippets;
    private readonly Func<ContextPack, Task<bool>> _startConversation;

    private ContextPackLibrary _library = new();
    private IReadOnlyDictionary<string, PromptSnippet> _snippetsById =
        new Dictionary<string, PromptSnippet>();
    private bool _reloading;
    private ContextPack? _deletedPack;
    private int _deletedPackIndex = -1;
    private bool _closeStarted;

    public ContextPacksWindow(
        Func<ContextPackLibrary> load,
        Action<ContextPackLibrary> save,
        Func<IReadOnlyList<PromptSnippet>> snippets,
        Func<ContextPack, Task<bool>> startConversation)
    {
        _load = load;
        _save = save;
        _snippets = snippets;
        _startConversation = startConversation;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Closed += (_, _) => _closeStarted = true;
        _library = _load();
        Reload();
    }

    // ─────────────────────────── rendering ───────────────────────────

    private ContextPack? SelectedPack =>
        (PackList.SelectedItem as ContextPackRow)?.Pack;

    private ContextPackItem? SelectedItem =>
        (ItemList.SelectedItem as ContextPackItemRow)?.Item;

    private void Reload(string? keepPackId = null, string? keepItemId = null, string? status = null)
    {
        _reloading = true;
        _snippetsById = _snippets()
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var packRows = _library.Packs.Select(p => new ContextPackRow(p)).ToList();
        PackList.ItemsSource = packRows;
        PackList.SelectedItem =
            packRows.FirstOrDefault(r => r.Pack.Id == keepPackId)
            ?? packRows.FirstOrDefault(r => r.Pack.Id == SelectedPack?.Id)
            ?? packRows.FirstOrDefault();
        _reloading = false;
        ReloadItems(keepItemId);
        if (status is not null)
            StatusText.Text = status;
        else if (packRows.Count == 0)
            StatusText.Text = Strings.Get("context_pack.empty_packs");
        else if (SelectedPack is { Items.Count: 0 })
            StatusText.Text = Strings.Get("context_pack.empty_items");
    }

    private void ReloadItems(string? keepItemId = null)
    {
        var pack = SelectedPack;
        var rows = pack is null
            ? new List<ContextPackItemRow>()
            : pack.Items.Select(i => new ContextPackItemRow(i, _snippetsById)).ToList();
        ItemList.ItemsSource = rows;
        ItemList.SelectedItem = rows.FirstOrDefault(r => r.Item.Id == keepItemId);
        UpdateControls();
    }

    private void UpdateControls()
    {
        var pack = SelectedPack;
        var hasPack = pack is not null;
        NewPackButton.IsEnabled = _library.Packs.Count < ContextPackLibrary.MaxPacks;
        RenamePackButton.IsEnabled = hasPack;
        DeletePackButton.IsEnabled = hasPack;
        var canAdd = pack is not null && pack.Items.Count < ContextPackLibrary.MaxItemsPerPack;
        AddFileButton.IsEnabled = canAdd;
        AddNoteButton.IsEnabled = canAdd;
        AddSnippetItemButton.IsEnabled = canAdd;
        RemoveItemButton.IsEnabled = SelectedItem is not null;
        StartButton.IsEnabled = pack is not null && pack.Items.Count > 0;
        StartButton.ToolTip = pack is { Items.Count: 0 }
            ? Strings.Get("context_pack.empty_start_reason")
            : Strings.Get("context_pack.start_tooltip");
        UndoDeleteButton.Visibility = _deletedPack is null ? Visibility.Collapsed : Visibility.Visible;
        UndoDeleteButton.IsEnabled = _deletedPack is not null;
    }

    private bool TrySave()
    {
        try
        {
            _save(_library);
            return true;
        }
        catch (Exception)
        {
            // The store's truth is what's on disk: reload it so the
            // window never displays a change that wasn't kept.
            _library = _load();
            Reload(status: Strings.Get("context_pack.save_failed"));
            return false;
        }
    }

    private void ClearUndo()
    {
        _deletedPack = null;
        _deletedPackIndex = -1;
    }

    // ─────────────────────────── packs ───────────────────────────

    private void OnPackSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_reloading)
            return;
        HideEditors();
        ReloadItems();
        StatusText.Text = SelectedPack is { Items.Count: 0 }
            ? Strings.Get("context_pack.empty_items")
            : "";
    }

    private void OnNewPack(object sender, RoutedEventArgs e)
    {
        if (_library.Packs.Count >= ContextPackLibrary.MaxPacks)
        {
            StatusText.Text = Strings.Format("context_pack.pack_cap", ContextPackLibrary.MaxPacks);
            return;
        }
        while (true)
        {
            var prompt = new TextPromptWindow(
                Strings.Get("context_pack.new_title"),
                Strings.Get("context_pack.new_body"),
                "", okText: Strings.Get("context_pack.new_pack")) { Owner = this };
            if (FocusReturn.ShowDialog(prompt) != true)
                return;
            var pack = _library.AddPack(prompt.Text, DateTimeOffset.UtcNow);
            if (pack is null)
            {
                StatusText.Text = Strings.Get("context_pack.new_name_required");
                continue;
            }
            if (!TrySave())
                return;
            ClearUndo();
            HideEditors();
            Reload(pack.Id, status: "");
            return;
        }
    }

    private void OnRenamePack(object sender, RoutedEventArgs e)
    {
        if (SelectedPack is not { } pack)
            return;
        while (true)
        {
            var prompt = new TextPromptWindow(
                Strings.Get("context_pack.rename_title"),
                Strings.Get("context_pack.rename_body"),
                pack.Name, okText: Strings.Get("prompt.rename_ok")) { Owner = this };
            if (FocusReturn.ShowDialog(prompt) != true)
                return;
            if (!_library.RenamePack(pack.Id, prompt.Text, DateTimeOffset.UtcNow))
            {
                StatusText.Text = Strings.Get("context_pack.rename_name_required");
                continue;
            }
            if (!TrySave())
                return;
            ClearUndo();
            Reload(pack.Id, status: "");
            return;
        }
    }

    private void OnDeletePack(object sender, RoutedEventArgs e)
    {
        if (SelectedPack is not { } pack)
            return;
        var answer = System.Windows.MessageBox.Show(this,
            Strings.Format("context_pack.delete_body", pack.Name),
            Strings.Get("context_pack.delete_title"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;
        var index = _library.Packs.IndexOf(pack);
        if (!_library.RemovePack(pack.Id))
            return;
        if (!TrySave())
            return;
        _deletedPack = pack;
        _deletedPackIndex = index;
        HideEditors();
        var next = _library.Packs.Count == 0
            ? null
            : _library.Packs[Math.Clamp(index, 0, _library.Packs.Count - 1)].Id;
        Reload(next, status: Strings.Get("context_pack.deleted"));
    }

    private void OnUndoDelete(object sender, RoutedEventArgs e)
    {
        if (_deletedPack is not { } pack)
            return;
        if (!_library.RestorePack(pack, _deletedPackIndex))
        {
            StatusText.Text = Strings.Format("context_pack.pack_cap", ContextPackLibrary.MaxPacks);
            return;
        }
        if (!TrySave())
            return;
        ClearUndo();
        Reload(pack.Id, status: Strings.Get("context_pack.restored"));
    }

    // ─────────────────────────── items ───────────────────────────

    private void OnItemSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateControls();

    private void OnAddFile(object sender, RoutedEventArgs e)
    {
        if (SelectedPack is not { } pack)
            return;
        if (pack.Items.Count >= ContextPackLibrary.MaxItemsPerPack)
        {
            StatusText.Text = Strings.Format("context_pack.item_cap", ContextPackLibrary.MaxItemsPerPack);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Strings.Get("context_pack.add_file_filter"),
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
            AddPaths(pack, dialog.FileNames);
    }

    // (Drag types fully qualified throughout: the WinForms global
    // usings make them ambiguous — the codebase's standing pattern
    // for this collision, as in MainWindow's own drop handlers.)
    private void OnDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            e.Effects = System.Windows.DragDropEffects.Copy;
    }

    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            return;
        e.Handled = true;
        if (SelectedPack is not { } pack)
        {
            StatusText.Text = Strings.Get("context_pack.empty_packs");
            return;
        }
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
            AddPaths(pack, paths);
    }

    private void AddPaths(ContextPack pack, IEnumerable<string> paths)
    {
        var added = 0;
        var skipped = 0;
        string? lastAddedId = null;
        foreach (var path in paths)
        {
            if (Directory.Exists(path) || !File.Exists(path)
                || pack.Items.Count >= ContextPackLibrary.MaxItemsPerPack)
            {
                skipped++;
                continue;
            }
            var item = _library.AddFileItem(pack.Id, path, DateTimeOffset.UtcNow);
            if (item is null)
            {
                skipped++;
                continue;
            }
            added++;
            lastAddedId = item.Id;
        }
        if (added == 0)
        {
            StatusText.Text = pack.Items.Count >= ContextPackLibrary.MaxItemsPerPack
                ? Strings.Format("context_pack.item_cap", ContextPackLibrary.MaxItemsPerPack)
                : Strings.Format("context_pack.files_skipped", skipped);
            return;
        }
        if (!TrySave())
            return;
        ClearUndo();
        var status = Strings.Format("context_pack.files_added", added);
        if (skipped > 0)
            status += " " + Strings.Format("context_pack.files_skipped", skipped);
        Reload(pack.Id, lastAddedId, status);
    }

    private void OnAddNote(object sender, RoutedEventArgs e)
    {
        if (SelectedPack is not { } pack)
            return;
        if (pack.Items.Count >= ContextPackLibrary.MaxItemsPerPack)
        {
            StatusText.Text = Strings.Format("context_pack.item_cap", ContextPackLibrary.MaxItemsPerPack);
            return;
        }
        SnippetPickerPanel.Visibility = Visibility.Collapsed;
        NoteEditorPanel.Visibility = Visibility.Visible;
        NoteTitleBox.Focus();
    }

    private void OnSaveNote(object sender, RoutedEventArgs e)
    {
        if (SelectedPack is not { } pack)
            return;
        var item = _library.AddNoteItem(pack.Id, NoteTitleBox.Text, NoteTextBox.Text, DateTimeOffset.UtcNow);
        if (item is null)
        {
            StatusText.Text = NoteTextBox.Text.Trim().Length > ContextPackLibrary.MaxNoteTextLength
                ? Strings.Format("ui.use_at_most_characters", ContextPackLibrary.MaxNoteTextLength)
                : Strings.Get("context_pack.note_required");
            return;
        }
        if (!TrySave())
            return;
        ClearUndo();
        NoteTitleBox.Text = "";
        NoteTextBox.Text = "";
        NoteEditorPanel.Visibility = Visibility.Collapsed;
        Reload(pack.Id, item.Id, "");
    }

    private void OnCancelNote(object sender, RoutedEventArgs e)
    {
        NoteTitleBox.Text = "";
        NoteTextBox.Text = "";
        NoteEditorPanel.Visibility = Visibility.Collapsed;
    }

    private void OnAddSnippet(object sender, RoutedEventArgs e)
    {
        if (SelectedPack is not { } pack)
            return;
        if (pack.Items.Count >= ContextPackLibrary.MaxItemsPerPack)
        {
            StatusText.Text = Strings.Format("context_pack.item_cap", ContextPackLibrary.MaxItemsPerPack);
            return;
        }
        var snippets = _snippets();
        if (snippets.Count == 0)
        {
            StatusText.Text = Strings.Get("context_pack.no_snippets");
            return;
        }
        NoteEditorPanel.Visibility = Visibility.Collapsed;
        SnippetCombo.ItemsSource = snippets;
        SnippetCombo.SelectedIndex = 0;
        SnippetPickerPanel.Visibility = Visibility.Visible;
        SnippetCombo.Focus();
    }

    private void OnAddChosenSnippet(object sender, RoutedEventArgs e)
    {
        if (SelectedPack is not { } pack)
            return;
        if (SnippetCombo.SelectedItem is not PromptSnippet snippet)
        {
            StatusText.Text = Strings.Get("context_pack.snippet_required");
            return;
        }
        var item = _library.AddSnippetItem(pack.Id, snippet.Id, DateTimeOffset.UtcNow);
        if (item is null)
        {
            StatusText.Text = Strings.Format("context_pack.item_cap", ContextPackLibrary.MaxItemsPerPack);
            return;
        }
        if (!TrySave())
            return;
        ClearUndo();
        SnippetPickerPanel.Visibility = Visibility.Collapsed;
        Reload(pack.Id, item.Id, "");
    }

    private void OnCancelSnippet(object sender, RoutedEventArgs e) =>
        SnippetPickerPanel.Visibility = Visibility.Collapsed;

    private void OnRemoveItem(object sender, RoutedEventArgs e)
    {
        if (SelectedPack is not { } pack || SelectedItem is not { } item)
            return;
        if (!_library.RemoveItem(pack.Id, item.Id, DateTimeOffset.UtcNow))
            return;
        if (!TrySave())
            return;
        ClearUndo();
        Reload(pack.Id, status: "");
    }

    private void HideEditors()
    {
        NoteEditorPanel.Visibility = Visibility.Collapsed;
        SnippetPickerPanel.Visibility = Visibility.Collapsed;
    }

    // ─────────────────────────── start ───────────────────────────

    private async void OnStartConversation(object sender, RoutedEventArgs e)
    {
        if (SelectedPack is not { } pack || pack.Items.Count == 0)
            return;
        StartButton.IsEnabled = false;
        var started = false;
        try { started = await _startConversation(pack); }
        catch (Exception) { started = false; }
        if (started)
        {
            _closeStarted = true;
            Close();
            return;
        }
        if (!_closeStarted)
        {
            StatusText.Text = Strings.Get("context_pack.start_failed");
            UpdateControls();
        }
    }
}
