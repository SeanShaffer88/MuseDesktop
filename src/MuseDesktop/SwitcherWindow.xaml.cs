using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MuseDesktop.Core;
using Brush = System.Windows.Media.Brush;
using ContextMenu = System.Windows.Controls.ContextMenu;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace MuseDesktop;

/// <summary>
/// One switcher row: either a window (its descriptor plus the profile
/// color dot, resolved here from the Core palette key) or — R5 #144 —
/// a window-GROUP header naming the group and how many windows it
/// holds; choosing a header raises the whole group.
/// </summary>
public sealed class SwitcherRow
{
    public SwitcherRow(WindowDescriptor window)
    {
        Window = window;
        if (ProfileColors.HexFor(window.ProfileColorKey) is { } hex)
        {
            try
            {
                DotBrush = new SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            }
            catch { /* an unparsable hex just means no dot */ }
        }
    }

    private SwitcherRow(WindowGroup group, IReadOnlyList<string> liveMemberIds)
    {
        IsGroupHeader = true;
        GroupId = group.Id;
        GroupName = group.Name;
        GroupMemberIds = liveMemberIds;
    }

    public static SwitcherRow Header(WindowGroup group, IReadOnlyList<string> liveMemberIds) =>
        new(group, liveMemberIds);

    public WindowDescriptor? Window { get; }
    public bool IsGroupHeader { get; }
    public string? GroupId { get; }
    public string? GroupName { get; }
    public IReadOnlyList<string> GroupMemberIds { get; } = Array.Empty<string>();

    public string Title => IsGroupHeader
        ? $"Group: {GroupName} ({GroupMemberIds.Count})"
        : string.IsNullOrWhiteSpace(Window!.Title) ? "Muse window" : Window.Title;
    // R3 #121: the profile's avatar glyph rides the descriptor and
    // leads the profile name here, like every other switcher surface.
    public string ProfileLabel => IsGroupHeader
        ? "Window group — Enter raises all its windows"
        : Window!.ProfileName is null
            ? (Window.IsMain ? "Main window" : "Second window")
            : $"{(Window.IsMain ? "Main window" : "Second window")} · profile {(WorkspaceGlyphs.SymbolFor(Window.ProfileGlyphKey) is { } symbol ? symbol + " " : "")}{Window.ProfileName}";
    public Brush? DotBrush { get; }
    public Visibility DotVisibility => DotBrush is null ? Visibility.Collapsed : Visibility.Visible;
    public FontWeight TitleWeight => IsGroupHeader ? FontWeights.SemiBold : FontWeights.Normal;
    public string AutomationName => $"{Title}, {ProfileLabel}";
}

/// <summary>
/// The window switcher overlay (R1 #103): every open window in
/// most-recently-used order (Core's <see cref="WindowMruList"/> did
/// the sorting before this window existed), each with its profile
/// color dot and page title. The previous window is preselected —
/// the overwhelmingly common switch is "back to the one I was just
/// in". Arrow keys + Enter switch; Esc cancels; the list and every
/// row carry screen-reader names like the palette's.
///
/// R5 #144 adds window groups-lite: when the caller passes the
/// session's group set, grouped windows sit under their group's
/// header, Enter on a header raises the whole group, and the
/// right-click menu manages groups (create / assign / remove /
/// rename / delete) and offers the #149 tiling presets for any
/// window. Groups live in Core's WindowGroupSet; this window only
/// renders and edits them.
/// </summary>
public partial class SwitcherWindow : Window
{
    private readonly Action<string> _onChosen;
    private readonly IReadOnlyList<WindowDescriptor> _windows;
    private readonly WindowGroupSet? _groups;
    private readonly Action<IReadOnlyList<string>>? _onShowGroup;
    private readonly Action<string, TilePreset>? _onTilePreset;

    public SwitcherWindow(IReadOnlyList<WindowDescriptor> windowsMru, Action<string> onChosen,
        WindowGroupSet? groups = null,
        Action<IReadOnlyList<string>>? onShowGroup = null,
        Action<string, TilePreset>? onTilePreset = null)
    {
        _onChosen = onChosen;
        _windows = windowsMru;
        _groups = groups;
        _onShowGroup = onShowGroup;
        _onTilePreset = onTilePreset;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        RebuildRows();
        WindowList.SelectedIndex = DefaultRowIndex();
        Loaded += (_, _) =>
        {
            WindowList.Focus();
            if (WindowList.SelectedItem is not null)
                WindowList.ScrollIntoView(WindowList.SelectedItem);
        };
    }

    /// <summary>
    /// The row list: each group (in set order) leads with its header
    /// and its members in MRU order; ungrouped windows follow in MRU
    /// order. Without a group set this is exactly R1's flat list.
    /// </summary>
    private List<SwitcherRow> BuildRows()
    {
        var rows = new List<SwitcherRow>();
        if (_groups is null || _groups.Groups.Count == 0)
        {
            rows.AddRange(_windows.Select(w => new SwitcherRow(w)));
            return rows;
        }
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in _groups.Groups)
        {
            var members = _windows.Where(w => group.WindowIds.Contains(w.Id)).ToList();
            if (members.Count == 0)
                continue;
            rows.Add(SwitcherRow.Header(group, members.Select(m => m.Id).ToList()));
            foreach (var member in members)
            {
                rows.Add(new SwitcherRow(member));
                placed.Add(member.Id);
            }
        }
        rows.AddRange(_windows.Where(w => !placed.Contains(w.Id)).Select(w => new SwitcherRow(w)));
        return rows;
    }

    private void RebuildRows(string? keepWindowId = null)
    {
        var rows = BuildRows();
        WindowList.ItemsSource = rows;
        if (keepWindowId is not null)
            WindowList.SelectedItem = rows.FirstOrDefault(r => r.Window?.Id == keepWindowId);
    }

    /// <summary>The flat-list default (the previous window) translated into row indices when headers interleave.</summary>
    private int DefaultRowIndex()
    {
        var rows = (List<SwitcherRow>)WindowList.ItemsSource;
        var windowRows = rows.Where(r => !r.IsGroupHeader).ToList();
        if (windowRows.Count == 0)
            return 0;
        var pick = windowRows[Math.Min(
            WindowMruList.DefaultSelectionIndex(windowRows.Count), windowRows.Count - 1)];
        return rows.IndexOf(pick);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ChooseSelected();
            e.Handled = true;
        }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => ChooseSelected();

    private void ChooseSelected()
    {
        if (WindowList.SelectedItem is not SwitcherRow row)
            return;
        if (row.IsGroupHeader)
        {
            var members = row.GroupMemberIds;
            Close();
            _onShowGroup?.Invoke(members);
            return;
        }
        var id = row.Window!.Id;
        Close();
        _onChosen(id);
    }

    // ─────────────────────── groups + tiling context menu (#144/#149) ───────────────────────

    private void OnListRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_groups is null)
            return;
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.Content is not SwitcherRow row)
            return;
        WindowList.SelectedItem = row;
        var menu = new ContextMenu { PlacementTarget = WindowList };

        if (row.IsGroupHeader)
        {
            var raise = new MenuItem { Header = "Raise all windows" };
            raise.Click += (_, _) => ChooseSelected();
            menu.Items.Add(raise);
            var rename = new MenuItem { Header = "Rename group…" };
            rename.Click += (_, _) =>
            {
                var prompt = new TextPromptWindow("Rename group", "Group name:", row.GroupName ?? "")
                    { Owner = this };
                if (prompt.ShowDialog() == true && _groups.Rename(row.GroupId!, prompt.Text))
                    RebuildRows();
            };
            menu.Items.Add(rename);
            var delete = new MenuItem { Header = "Delete group" };
            delete.Click += (_, _) =>
            {
                _groups.Delete(row.GroupId!);
                RebuildRows();
            };
            menu.Items.Add(delete);
        }
        else
        {
            var windowId = row.Window!.Id;
            if (_onTilePreset is not null)
            {
                var tile = new MenuItem { Header = "Tile window" };
                foreach (var preset in WindowTilingPresets.All)
                {
                    var captured = preset;
                    var presetItem = new MenuItem { Header = WindowTilingPresets.Title(preset) };
                    presetItem.Click += (_, _) =>
                    {
                        Close();
                        _onTilePreset(windowId, captured);
                    };
                    tile.Items.Add(presetItem);
                }
                menu.Items.Add(tile);
                menu.Items.Add(new Separator());
            }
            var create = new MenuItem { Header = "New group with this window…" };
            create.Click += (_, _) =>
            {
                var prompt = new TextPromptWindow("New window group",
                        "Name the group (it lasts until the app exits):")
                    { Owner = this };
                if (prompt.ShowDialog() == true)
                {
                    _groups.Create(prompt.Text, new[] { windowId });
                    RebuildRows(windowId);
                }
            };
            menu.Items.Add(create);
            var current = _groups.GroupOf(windowId);
            var others = _groups.Groups.Where(g => g.Id != current?.Id).ToList();
            if (others.Count > 0)
            {
                var add = new MenuItem { Header = "Add to group" };
                foreach (var group in others)
                {
                    var captured = group;
                    var groupItem = new MenuItem { Header = group.Name };
                    groupItem.Click += (_, _) =>
                    {
                        _groups.Assign(windowId, captured.Id);
                        RebuildRows(windowId);
                    };
                    add.Items.Add(groupItem);
                }
                menu.Items.Add(add);
            }
            if (current is not null)
            {
                var remove = new MenuItem { Header = $"Remove from group \"{current.Name}\"" };
                remove.Click += (_, _) =>
                {
                    _groups.Unassign(windowId);
                    RebuildRows(windowId);
                };
                menu.Items.Add(remove);
            }
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T hit)
                return hit;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }
}
