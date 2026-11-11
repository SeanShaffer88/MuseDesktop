using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MuseDesktop.Core;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using SystemColors = System.Windows.SystemColors;

namespace MuseDesktop;

public partial class DualWindow
{
    private Pane _active = null!;
    private Pane? _expanded;
    private ComboBox _pairs = null!;
    private ComboBox _commands = null!;
    private TextBox _find = null!;
    private CheckBox _sync = null!;
    private CheckBox _watch = null!;
    private DispatcherTimer _replyTimer = null!;
    private DispatcherTimer _notesTimer = null!;
    private bool _ownerClosing;
    private bool _polling;
    private bool _syncBusy;
    private readonly HashSet<string> _unlockedProfiles = new();
    private static readonly Dictionary<string, AppLockThrottle> ProfileThrottles = new();

    private static Button Tool(string key, Action action)
    {
        var button = new Button { Content = Strings.Get(key), ToolTip = Strings.Get(key),
            Margin = new Thickness(2), Padding = new Thickness(7, 3, 7, 3) };
        AutomationProperties.SetName(button, Strings.Get(key));
        if (SystemParameters.HighContrast)
        {
            button.Background = SystemColors.ControlBrush;
            button.Foreground = SystemColors.ControlTextBrush;
            button.BorderBrush = SystemColors.ControlTextBrush;
        }
        button.Click += (_, _) => action();
        return button;
    }

    private void InitializeWorkbench()
    {
        _active = _left;
        _left.ProfileId = _right.ProfileId = _profileId;
        _unlockedProfiles.Add(_profileId);
        if (SystemParameters.HighContrast)
        { Background = SystemColors.WindowBrush; Foreground = SystemColors.WindowTextBrush; }
        var layout = new WrapPanel();
        layout.Children.Add(Tool("dual.swap", () =>
        {
            _session.Swapped = !_session.Swapped;
            ConfigureLayout(); SaveSession();
        }));
        layout.Children.Add(Tool("dual.orientation", () =>
        {
            _session.Vertical = !_session.Vertical;
            ConfigureLayout(); SaveSession();
        }));
        layout.Children.Add(Tool("dual.expand", () => ExpandPane(_active)));
        layout.Children.Add(Tool("dual.detach", () => DetachPane(_active)));
        layout.Children.Add(Tool("dual.transfer", () => _ = TransferSelectionAsync()));
        _sync = new CheckBox { Content = Strings.Get("dual.sync"), Margin = new Thickness(8, 5, 8, 0) };
        _sync.Click += async (_, _) =>
        {
            foreach (var pane in new[] { _left, _right }) await ApplyPageToolsAsync(pane);
        };
        layout.Children.Add(_sync);
        _watch = new CheckBox { Content = Strings.Get("dual.watch"), Margin = new Thickness(8, 5, 8, 0) };
        _watch.Unchecked += (_, _) =>
        {
            foreach (var pane in new[] { _left, _right })
            { pane.Replies.Reset(); pane.PendingReply = false; UpdatePaneTitle(pane); }
        };
        layout.Children.Add(_watch);
        var restore = new CheckBox { Content = Strings.Get("dual.restore"), IsChecked = _session.RestoreOnLaunch,
            Margin = new Thickness(8, 5, 8, 0), ToolTip = Strings.Get("dual.restore_hint") };
        restore.Click += (_, _) => { _session.RestoreOnLaunch = restore.IsChecked == true; SaveSession(); };
        layout.Children.Add(restore);
        WorkbenchHost.Children.Add(layout);

        var pairs = new WrapPanel();
        _pairs = new ComboBox { MinWidth = 150, MaxWidth = 240, DisplayMemberPath = nameof(DualPair.Name),
            Margin = new Thickness(2), ToolTip = Strings.Get("dual.pairs") };
        AutomationProperties.SetName(_pairs, Strings.Get("dual.pairs"));
        pairs.Children.Add(_pairs);
        pairs.Children.Add(Tool("dual.save_pair", SavePair));
        pairs.Children.Add(Tool("dual.open_pair", () => _ = OpenPairAsync()));
        pairs.Children.Add(Tool("dual.delete_pair", DeletePair));
        _find = new TextBox { MinWidth = 140, MaxWidth = 240, Margin = new Thickness(8, 2, 2, 2),
            ToolTip = Strings.Get("dual.find"), MaxLength = 512 };
        AutomationProperties.SetName(_find, Strings.Get("dual.find"));
        _find.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await FindBothAsync(); }
            if (e.Key == Key.Escape) { _find.Clear(); await FindBothAsync(); FocusPane(_active); }
        };
        pairs.Children.Add(_find);
        pairs.Children.Add(Tool("dual.find", () => _ = FindBothAsync()));
        pairs.Children.Add(Tool("dual.find_next", () => { try { _active.Core?.Find.FindNext(); } catch { Announce(_active, "dual.tool_unavailable"); } }));
        pairs.Children.Add(Tool("dual.find_previous", () => { try { _active.Core?.Find.FindPrevious(); } catch { Announce(_active, "dual.tool_unavailable"); } }));
        WorkbenchHost.Children.Add(pairs);
        RefreshPairs();

        var commands = new WrapPanel();
        _commands = new ComboBox { MinWidth = 180, Margin = new Thickness(2),
            ToolTip = Strings.Get("dual.commands") };
        AutomationProperties.SetName(_commands, Strings.Get("dual.commands"));
        foreach (var key in new[] { "back", "forward", "reload", "dual.new_chat", "dual.zoom_in",
            "dual.zoom_out", "dual.expand", "dual.detach", "dual.privacy", "dual.recover", "dual.other" })
            _commands.Items.Add(new ComboBoxItem { Content = Strings.Get(key), Tag = key });
        _commands.SelectedIndex = 0;
        commands.Children.Add(_commands);
        commands.Children.Add(Tool("dual.run", RunPaneCommand));
        var help = new TextBlock { Text = Strings.Get("dual.shortcuts"), Margin = new Thickness(8, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap };
        commands.Children.Add(help);
        WorkbenchHost.Children.Add(commands);

        foreach (var pane in new[] { _left, _right })
        {
            var picker = new ComboBox { MinWidth = 90, MaxWidth = 150, DisplayMemberPath = nameof(AccountProfile.Name),
                SelectedValuePath = nameof(AccountProfile.Id), Margin = new Thickness(4, 0, 4, 0),
                ItemsSource = Profiles.VisibleProfiles(_settings), SelectedValue = _profileId,
                ToolTip = Strings.Get("dual.profile") };
            AutomationProperties.SetName(picker, Strings.Get("dual.profile"));
            pane.ProfilePicker = picker;
            picker.SelectionChanged += async (_, _) =>
            {
                if (picker.SelectedValue is not string id || id == pane.ProfileId || _closed) return;
                if (!ConfirmReplace()) { picker.SelectedValue = pane.ProfileId; return; }
                await StartProfileAsync(pane, id, UrlPolicy.HomeUrl, resetRoute: true);
                if (!_closed) picker.SelectedValue = pane.ProfileId;
            };
            var toolbar = ReferenceEquals(pane, _left) ? LeftToolbar : RightToolbar;
            toolbar.Children.Add(picker);
            toolbar.Children.Add(Tool("dual.privacy", () => { pane.Covered = !pane.Covered; ApplyPrivacy(pane); }));
            toolbar.Children.Add(Tool("dual.recover", () => _ = RecoverPaneAsync(pane)));
            toolbar.Children.Add(Tool("dual.detach", () => DetachPane(pane)));
            RootFor(pane).GotKeyboardFocus += (_, _) => SetActive(pane);
        }
        _notesTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _notesTimer.Tick += (_, _) => { _notesTimer.Stop(); SaveSession(); };
        Scratchpad.Text = _session.Scratchpad;
        _replyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _replyTimer.Tick += async (_, _) => await PollRepliesAsync();
        _replyTimer.Start();
        Closing += OnWorkbenchClosing;
        Loaded += (_, _) =>
        {
            if (Owner is not null) Owner.Closing += OnOwnerClosing;
            if (_session.RestoreOnLaunch)
            {
                var rect = PlacementClamp.Clamp(new ScreenRect(_session.WindowLeft, _session.WindowTop,
                    _session.WindowWidth, _session.WindowHeight), MonitorRects.AllWorkAreasInDip());
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = rect.Left; Top = rect.Top; Width = rect.Width; Height = rect.Height;
            }
            _session.WasOpen = true;
            SaveSession();
            SetActive(_left);
        };
        var tabIndex = 0;
        AssignTabOrder((DependencyObject)Content, ref tabIndex);
    }

    private static void AssignTabOrder(DependencyObject node, ref int index)
    {
        if (node is Button or ComboBox or TextBox or CheckBox or GridSplitter)
            KeyboardNavigation.SetTabIndex(node, index++);
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            AssignTabOrder(child, ref index);
    }

    private Grid RootFor(Pane pane) => ReferenceEquals(pane, _left) ? LeftPane : RightPane;
    private Border FrameFor(Pane pane) => ReferenceEquals(pane, _left) ? LeftFrame : RightFrame;
    private string Side(Pane pane)
    {
        var first = ReferenceEquals(pane, _left) != _session.Swapped;
        return Strings.Get(_session.Vertical ? (first ? "dual.top" : "dual.bottom")
            : (first ? "dual.left" : "dual.right"));
    }
    private string ProfileName(Pane pane) =>
        _settings.Profiles.FirstOrDefault(p => p.Id == pane.ProfileId)?.Name ?? pane.ProfileId;

    private void ConfigureLayout()
    {
        var vertical = _session.Vertical;
        var fraction = _session.LeftFraction;
        PaneLayout.ColumnDefinitions.Clear();
        PaneLayout.RowDefinitions.Clear();
        if (vertical)
        {
            PaneLayout.ColumnDefinitions.Add(new ColumnDefinition());
            PaneLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(fraction, GridUnitType.Star), MinHeight = 170 });
            PaneLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            PaneLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - fraction, GridUnitType.Star), MinHeight = 170 });
        }
        else
        {
            PaneLayout.RowDefinitions.Add(new RowDefinition());
            PaneLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star), MinWidth = 340 });
            PaneLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            PaneLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star), MinWidth = 340 });
        }
        var first = _session.Swapped ? _right : _left;
        foreach (var pane in new[] { _left, _right })
        {
            var root = RootFor(pane);
            root.Visibility = _expanded is null || ReferenceEquals(pane, _expanded) ? Visibility.Visible : Visibility.Collapsed;
            var index = _expanded is not null || ReferenceEquals(pane, first) ? 0 : 2;
            Grid.SetRow(root, vertical ? index : 0);
            Grid.SetColumn(root, vertical ? 0 : index);
            if (_expanded is not null)
            {
                Grid.SetRowSpan(root, vertical ? 3 : 1);
                Grid.SetColumnSpan(root, vertical ? 1 : 3);
            }
            else { Grid.SetRowSpan(root, 1); Grid.SetColumnSpan(root, 1); }
            UpdatePaneTitle(pane);
        }
        Divider.Visibility = _expanded is null ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(Divider, vertical ? 1 : 0);
        Grid.SetColumn(Divider, vertical ? 0 : 1);
        Divider.Width = vertical ? double.NaN : 6;
        Divider.Height = vertical ? 6 : double.NaN;
        Divider.HorizontalAlignment = vertical ? System.Windows.HorizontalAlignment.Stretch : System.Windows.HorizontalAlignment.Center;
        Divider.VerticalAlignment = vertical ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        Divider.ResizeDirection = vertical ? GridResizeDirection.Rows : GridResizeDirection.Columns;
    }

    private void ExpandPane(Pane pane)
    {
        _expanded = ReferenceEquals(_expanded, pane) ? null : pane;
        ConfigureLayout(); FocusPane(pane);
    }

    private void SetActive(Pane pane)
    {
        _active = pane;
        pane.PendingReply = false;
        foreach (var item in new[] { _left, _right })
        {
            FrameFor(item).BorderBrush = ReferenceEquals(item, pane) ? SystemColors.HighlightBrush
                : (TryFindResource("HairlineBrush") as Brush ?? SystemColors.ControlDarkBrush);
            FrameFor(item).BorderThickness = new Thickness(ReferenceEquals(item, pane) ? 3 : 1);
            UpdatePaneTitle(item);
        }
    }

    private void FocusPane(Pane pane)
    {
        if (_closed) return;
        if (_expanded is not null && !ReferenceEquals(_expanded, pane)) { _expanded = pane; ConfigureLayout(); }
        SetActive(pane);
        pane.Detached?.Activate();
        if (!pane.Covered) pane.Browser?.Focus();
        else RootFor(pane).MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        Announce(pane, pane.Covered ? "dual.covered" : "dual.active");
    }

    private void UpdatePaneTitle(Pane pane)
    {
        pane.Name = Side(pane);
        var title = pane.Covered ? Strings.Get("dual.covered")
            : DualWorkbench.CleanName(pane.Core?.DocumentTitle);
        var text = Strings.Format("dual.header", pane.Name, ProfileName(pane), title);
        if (pane.PendingReply) text += Strings.Get("dual.reply_badge");
        (ReferenceEquals(pane, _left) ? LeftTitle : RightTitle).Text = text;
        AutomationProperties.SetName(RootFor(pane), text);
        if (pane.Browser is { } browser) AutomationProperties.SetName(browser, text);
        if (pane.Detached is { } detached) detached.Title = pane.Covered ? Strings.Get("dual.covered") : text;
    }

    private void Announce(Pane pane, string key)
    {
        if (_closed) return;
        pane.Status.Text = Strings.Get(key);
        var peer = UIElementAutomationPeer.FromElement(pane.Status) ?? new TextBlockAutomationPeer(pane.Status);
        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private bool ConfirmReplace() => System.Windows.MessageBox.Show(this,
        Strings.Get("dual.replace_warning"), Strings.Get("dual.title"),
        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private bool AuthorizeProfile(string id)
    {
        if (_closed || _settings.Profiles.FirstOrDefault(p => p.Id == id && !p.Archived) is not { } profile) return false;
        if (_unlockedProfiles.Contains(id)) return true;
        if (ProfileLock.IsLockActive(profile))
        {
            if (!ProfileLock.TryGetRecord(profile, out var record) || record is null) return false;
            if (!ProfileThrottles.TryGetValue(id, out var throttle))
                ProfileThrottles[id] = throttle = new AppLockThrottle();
            var window = new LockWindow(record, throttle, exitOnClose: false) { Owner = this };
            ShellTextScaling.Apply(window, _settings.ShellFontScale);
            window.ShowDialog();
            if (!window.Unlocked || _closed) return false;
        }
        _unlockedProfiles.Add(id);
        return true;
    }

    private async Task StartProfileAsync(Pane pane, string profileId, string url, bool resetRoute = false)
    {
        if (!AuthorizeProfile(profileId)) { Announce(pane, "dual.profile_locked"); return; }
        var generation = ++pane.Generation;
        var old = pane.Browser;
        pane.Browser = null;
        if (old is not null)
        {
            (pane.DetachedHost ?? pane.Host).Children.Remove(old);
            try { old.Dispose(); } catch { }
        }
        pane.ProfileId = profileId;
        pane.Failed = false;
        pane.Replies.Reset(); pane.PendingReply = false;
        if (ReferenceEquals(pane, _left))
        { _session.LeftProfileId = profileId; if (resetRoute) _session.LeftRoute = "/"; }
        else { _session.RightProfileId = profileId; if (resetRoute) _session.RightRoute = "/"; }
        if (pane.ProfilePicker is { } picker) picker.SelectedValue = profileId;
        SaveSession();
        try
        {
            Directory.CreateDirectory(AppPaths.ProfileDir(profileId));
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.ProfileDir(profileId));
            if (_closed || generation != pane.Generation) return;
            await StartPaneAsync(pane, env, url, generation);
        }
        catch (Exception ex)
        {
            if (!_closed && generation == pane.Generation)
            { pane.Failed = true; Announce(pane, "dual.recover_hint"); }
            _log.Warn($"Dual profile initialization failed: {ex.GetType().Name}");
        }
    }

    private async Task RecoverPaneAsync(Pane pane)
    {
        if (_closed) return;
        var route = ReferenceEquals(pane, _left) ? _session.LeftRoute : _session.RightRoute;
        if (!pane.Failed && pane.Browser is not null && !ConfirmReplace()) return;
        await StartProfileAsync(pane, pane.ProfileId, "https://muse.ai" + route);
    }

    private void DetachPane(Pane pane)
    {
        if (_closed || pane.Browser is null) return;
        if (pane.Detached is not null) { pane.Detached.Close(); return; }
        var host = new Grid();
        pane.Host.Children.Remove(pane.Browser);
        host.Children.Add(pane.Browser);
        var window = new Window { Owner = this, Content = host, Width = 850, Height = 700,
            MinWidth = 380, MinHeight = 350, Background = SystemColors.WindowBrush };
        pane.Detached = window; pane.DetachedHost = host;
        window.GotKeyboardFocus += (_, _) => SetActive(pane);
        window.PreviewKeyDown += OnPreviewKeyDown;
        window.Closed += (_, _) =>
        {
            if (pane.Browser is { } browser) host.Children.Remove(browser);
            pane.Detached = null; pane.DetachedHost = null;
            if (!_closed && pane.Browser is { } live)
            { pane.Host.Children.Add(live); ApplyPrivacy(pane); }
        };
        ThemeManager.ApplyTitleBar(window);
        ShellTextScaling.Apply(window, _settings.ShellFontScale);
        ApplyPrivacy(pane); window.Show(); window.Activate();
    }

    private void ApplyPrivacy(Pane pane)
    {
        if (pane.Browser is { } browser) browser.Visibility = pane.Covered ? Visibility.Hidden : Visibility.Visible;
        foreach (var host in new[] { pane.Host, pane.DetachedHost }.Where(h => h is not null))
        {
            foreach (var child in host!.Children.Cast<UIElement>().Where(c => c is not WebView2).ToList())
                host.Children.Remove(child);
            if (pane.Covered)
            {
                var cover = new StackPanel { Background = SystemColors.WindowBrush,
                    VerticalAlignment = VerticalAlignment.Center };
                cover.Children.Add(new TextBlock { Text = Strings.Get("dual.covered"),
                    Foreground = SystemColors.WindowTextBrush, TextAlignment = TextAlignment.Center });
                cover.Children.Add(Tool("dual.reveal", () => { pane.Covered = false; ApplyPrivacy(pane); FocusPane(pane); }));
                host.Children.Add(cover);
            }
            else if (ReferenceEquals(host, pane.Host) && pane.Detached is not null)
                host.Children.Add(new TextBlock { Text = Strings.Get("dual.detached_hint"), TextWrapping = TextWrapping.Wrap });
        }
        pane.Replies.Reset();
        pane.PendingReply = false;
        UpdatePaneTitle(pane);
        Announce(pane, pane.Covered ? "dual.covered" : "dual.ready");
    }

    private void RefreshPairs()
    {
        _pairs.ItemsSource = null;
        _pairs.ItemsSource = _session.Pairs;
        if (_session.Pairs.Count > 0) _pairs.SelectedIndex = 0;
    }

    private void SavePair()
    {
        var prompt = new TextPromptWindow(Strings.Get("dual.save_pair"), Strings.Get("dual.pair_name")) { Owner = this };
        if (prompt.ShowDialog() != true || _closed) return;
        var name = DualWorkbench.CleanName(prompt.Text);
        if (name.Length == 0) return;
        var existing = _session.Pairs.Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !ConfirmReplace()) return;
        if (existing is null && _session.Pairs.Count >= 20) { Announce(_active, "dual.pair_limit"); return; }
        if (existing is not null) _session.Pairs.Remove(existing);
        var first = _session.Swapped ? _right : _left;
        var second = ReferenceEquals(first, _left) ? _right : _left;
        _session.Pairs.Add(new DualPair { Name = name, LeftProfileId = first.ProfileId, RightProfileId = second.ProfileId,
            LeftRoute = DualMode.RestorableRoute(first.Core?.Source) ?? "/",
            RightRoute = DualMode.RestorableRoute(second.Core?.Source) ?? "/",
            LeftZoom = first.Browser?.ZoomFactor ?? 1, RightZoom = second.Browser?.ZoomFactor ?? 1 });
        SaveSession(); RefreshPairs();
    }

    private async Task OpenPairAsync()
    {
        if (_pairs.SelectedItem is not DualPair pair || !ConfirmReplace() ||
            !AuthorizeProfile(pair.LeftProfileId) || !AuthorizeProfile(pair.RightProfileId)) return;
        _session.Swapped = false; _expanded = null;
        _session.LeftZoom = pair.LeftZoom; _session.RightZoom = pair.RightZoom;
        ConfigureLayout();
        await Task.WhenAll(StartProfileAsync(_left, pair.LeftProfileId, "https://muse.ai" + pair.LeftRoute),
            StartProfileAsync(_right, pair.RightProfileId, "https://muse.ai" + pair.RightRoute));
    }

    private void DeletePair()
    {
        if (_pairs.SelectedItem is not DualPair pair || !ConfirmReplace()) return;
        _session.Pairs.Remove(pair); SaveSession(); RefreshPairs();
    }

    private async Task<string> ExecuteSafeAsync(Pane pane, string script)
    {
        if (_closed || pane.Covered || pane.Core is not { } core || !DualWorkbench.CanInspect(core.Source)) return "null";
        var generation = pane.Generation;
        try
        {
            // Recheck inside the document too: a navigation can race an async browser call.
            var guarded = "(() => { if (location.origin !== 'https://muse.ai' || " +
                "!(location.pathname === '/' || /^\\/chat(?:\\/|$)/.test(location.pathname))) return null; return " + script + "; })()";
            var result = await core.ExecuteScriptAsync(guarded);
            return !_closed && generation == pane.Generation && ReferenceEquals(core, pane.Core) ? result : "null";
        }
        catch { return "null"; }
    }

    private async Task TransferSelectionAsync()
    {
        var source = _active;
        var target = ReferenceEquals(source, _left) ? _right : _left;
        var raw = await ExecuteSafeAsync(source, DualToolsScripts.Selection);
        string? selected;
        try { selected = JsonSerializer.Deserialize<string>(raw); } catch { selected = null; }
        if (string.IsNullOrWhiteSpace(selected) || target.Covered || target.Core is not { } core ||
            !DualWorkbench.CanInspect(core.Source)) { Announce(source, "dual.transfer_unavailable"); return; }
        var destination = core.Source;
        var prompt = new TextPromptWindow(Strings.Get("dual.transfer"),
            Strings.Format("dual.transfer_review", ProfileName(source), ProfileName(target)),
            selected, multiline: true, okText: Strings.Get("dual.place_unsent")) { Owner = this };
        if (prompt.ShowDialog() != true || _closed) return;
        if (!ReferenceEquals(core, target.Core) || core.Source != destination)
        { Announce(target, "dual.transfer_unavailable"); return; }
        var result = await ExecuteSafeAsync(target, DualToolsScripts.InsertSelection(prompt.Text));
        Announce(target, result == "true" ? "dual.placed_unsent" : "dual.transfer_unavailable");
        if (result == "true") FocusPane(target);
    }

    private async Task ApplyPageToolsAsync(Pane pane) =>
        _ = await ExecuteSafeAsync(pane, _sync.IsChecked == true ? DualToolsScripts.StartScroll : DualToolsScripts.StopScroll);

    private void WirePaneTools(Pane pane, WebView2 browser, CoreWebView2 core, int generation)
    {
        browser.GotKeyboardFocus += (_, _) => SetActive(pane);
        core.DocumentTitleChanged += (_, _) => { if (!_closed && generation == pane.Generation) UpdatePaneTitle(pane); };
        try { core.Find.MatchCountChanged += (_, _) => ShowFindCount(pane, core); }
        catch { /* Older runtimes can still host chats without native Find. */ }
        core.ContextMenuRequested += (_, e) =>
        {
            if (_closed || pane.Covered || generation != pane.Generation || !e.ContextMenuTarget.HasLinkUri) return;
            var uri = e.ContextMenuTarget.LinkUri;
            if (UrlPolicy.ClassifyNavigation(uri) == LinkDisposition.Block) return;
            var item = core.Environment.CreateContextMenuItem(Strings.Get("dual.link_other"), null, CoreWebView2ContextMenuItemKind.Command);
            item.CustomItemSelected += (_, _) =>
            {
                var target = ReferenceEquals(pane, _left) ? _right : _left;
                if (!_closed && !target.Covered) target.Core?.Navigate(uri);
            };
            e.MenuItems.Add(item);
        };
        core.WebMessageReceived += async (_, e) =>
        {
            if (_closed || _syncBusy || _sync.IsChecked != true || pane.Covered ||
                generation != pane.Generation || !ReferenceEquals(_active, pane) ||
                !DualWorkbench.CanInspect(e.Source)) return;
            try
            {
                using var data = JsonDocument.Parse(e.WebMessageAsJson);
                if (data.RootElement.GetProperty("type").GetString() != "musedesktop-dual-scroll") return;
                var ratio = data.RootElement.GetProperty("ratio").GetDouble();
                if (!double.IsFinite(ratio) || ratio < 0 || ratio > 1) return;
                _syncBusy = true;
                var target = ReferenceEquals(pane, _left) ? _right : _left;
                await ExecuteSafeAsync(target, DualToolsScripts.ApplyScroll(ratio));
            }
            catch { /* Untrusted messages have no other effect. */ }
            finally { _syncBusy = false; }
        };
    }

    private async Task PollRepliesAsync()
    {
        if (_closed || _polling || _watch.IsChecked != true) return;
        _polling = true;
        try
        {
            foreach (var pane in new[] { _left, _right })
            {
                var result = await ExecuteSafeAsync(pane, DualToolsScripts.Streaming);
                if (result is not ("true" or "false")) { pane.Replies.Reset(); continue; }
                if (pane.Replies.Observe(result == "true", !ReferenceEquals(_active, pane) || !IsActive))
                { pane.PendingReply = true; UpdatePaneTitle(pane); Announce(pane, "dual.reply_done"); }
            }
        }
        finally { _polling = false; }
    }

    private async Task FindBothAsync()
    {
        foreach (var pane in new[] { _left, _right })
        {
            if (pane.Covered || pane.Core is not { } core) continue;
            try
            {
                if (string.IsNullOrWhiteSpace(_find.Text)) { core.Find.Stop(); Announce(pane, "dual.ready"); continue; }
                var options = core.Environment.CreateFindOptions();
                options.FindTerm = _find.Text;
                options.ShouldHighlightAllMatches = true;
                options.SuppressDefaultFindDialog = true;
                await core.Find.StartAsync(options);
                ShowFindCount(pane, core);
            }
            catch { Announce(pane, "dual.tool_unavailable"); }
        }
    }

    private void ShowFindCount(Pane pane, CoreWebView2 core)
    {
        if (!_closed && !pane.Covered && ReferenceEquals(core, pane.Core) && _find.Text.Length > 0)
            pane.Status.Text = Strings.Format("dual.find_count", Side(pane), core.Find.MatchCount);
    }

    private void RunPaneCommand()
    {
        if (_commands.SelectedItem is not ComboBoxItem { Tag: string command }) return;
        var pane = _active;
        switch (command)
        {
            case "back": if (pane.Core?.CanGoBack == true) pane.Core.GoBack(); break;
            case "forward": if (pane.Core?.CanGoForward == true) pane.Core.GoForward(); break;
            case "reload": if (pane.Core is not null && !pane.Failed) pane.Core.Reload(); else _ = RecoverPaneAsync(pane); break;
            case "dual.new_chat": pane.Core?.Navigate(UrlPolicy.HomeUrl); break;
            case "dual.zoom_in": if (pane.Browser is { } b) SetZoom(pane, ZoomMath.StepUp(b.ZoomFactor, _settings.ExtendedZoomRange)); break;
            case "dual.zoom_out": if (pane.Browser is { } c) SetZoom(pane, ZoomMath.StepDown(c.ZoomFactor, _settings.ExtendedZoomRange)); break;
            case "dual.expand": ExpandPane(pane); break;
            case "dual.detach": DetachPane(pane); break;
            case "dual.privacy": pane.Covered = !pane.Covered; ApplyPrivacy(pane); break;
            case "dual.recover": _ = RecoverPaneAsync(pane); break;
            case "dual.other": FocusOtherPane(pane); break;
        }
    }

    private bool HandleWorkbenchKey(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            switch (e.Key)
            {
                case Key.D1: FocusPane(_session.Swapped ? _right : _left); break;
                case Key.D2: FocusPane(_session.Swapped ? _left : _right); break;
                case Key.R: if (_active.Core is not null && !_active.Failed) _active.Core.Reload(); else _ = RecoverPaneAsync(_active); break;
                case Key.N: _active.Core?.Navigate(UrlPolicy.HomeUrl); break;
                case Key.P: _active.Covered = !_active.Covered; ApplyPrivacy(_active); break;
                case Key.Enter: ExpandPane(_active); break;
                default: return false;
            }
            e.Handled = true; return true;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K)
        { _commands.Focus(); _commands.IsDropDownOpen = true; e.Handled = true; return true; }
        return false;
    }

    private void OnScratchpadChanged(object sender, TextChangedEventArgs e)
    {
        if (_notesTimer is null || _closed) return;
        _session.Scratchpad = Scratchpad.Text;
        _notesTimer.Stop(); _notesTimer.Start();
    }

    private void OnOwnerClosing(object? sender, CancelEventArgs e) => _ownerClosing = !e.Cancel;

    private void OnWorkbenchClosing(object? sender, CancelEventArgs e)
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        _session.WindowLeft = bounds.Left; _session.WindowTop = bounds.Top;
        _session.WindowWidth = bounds.Width; _session.WindowHeight = bounds.Height;
        _session.WasOpen = _ownerClosing;
        _session.Scratchpad = Scratchpad.Text;
        SaveSession();
    }

    private void CloseWorkbench()
    {
        _replyTimer.Stop(); _notesTimer.Stop();
        if (Owner is not null) Owner.Closing -= OnOwnerClosing;
        foreach (var pane in new[] { _left, _right })
        {
            pane.Generation++;
            pane.Detached?.Close();
        }
    }

    /// <summary>Hosted smoke: real controls/engines and live-view moves, not live chat acceptance.</summary>
    internal async Task<bool> RunSmokeChecksAsync(TimeSpan timeout)
    {
        if (await Task.WhenAny(_ready.Task, Task.Delay(timeout)) != _ready.Task || !await _ready.Task)
            return false;
        var left = _left.Browser;
        var right = _right.Browser;
        var swapped = _session.Swapped;
        var vertical = _session.Vertical;
        _session.Swapped = !swapped;
        ConfigureLayout(); UpdateLayout();
        _session.Vertical = !vertical;
        ConfigureLayout(); UpdateLayout();
        _expanded = _left;
        ConfigureLayout(); UpdateLayout();
        _expanded = null;
        DetachPane(_left);
        if (_left.Detached is null) return false;
        _left.Covered = true; ApplyPrivacy(_left);
        if (left?.Visibility != Visibility.Hidden) return false;
        _left.Covered = false; ApplyPrivacy(_left);
        _left.Detached.Close();
        _session.Swapped = swapped; _session.Vertical = vertical;
        ConfigureLayout(); UpdateLayout();
        return ReferenceEquals(left, _left.Browser) && ReferenceEquals(right, _right.Browser) &&
            left?.Visibility == Visibility.Visible && _left.Detached is null &&
            left.Parent == _left.Host && right?.Parent == _right.Host;
    }
}
