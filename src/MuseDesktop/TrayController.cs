using System.Windows.Threading;
using MuseDesktop.Core;
using Forms = System.Windows.Forms;

namespace MuseDesktop;

/// <summary>
/// The tray icon and everything that hangs off it: the context menu
/// (v0.8.0/S4 #32 — complete enough to run the app hidden: window,
/// downloads, settings, profiles, status, exit), the balloons, the
/// composed icon (profile tint #37 + attention badge #31), single- vs
/// double-click restore (#36), and the suspend timer that puts the
/// WebView2 renderer to sleep after the window has been hidden for the
/// configured timeout (#34), skipping while downloads run or a
/// microphone session may be live (#35). The WebView2 operations
/// themselves stay with the window and arrive as delegates.
/// </summary>
public sealed class TrayController : IDisposable
{
    // How often a blocked suspend (download / possible voice session)
    // re-checks — long enough to be free, short enough to still save
    // memory once the block clears.
    private static readonly TimeSpan BlockedRecheck = TimeSpan.FromMinutes(2);

    private readonly DiagnosticLog _log;
    private readonly Dispatcher _dispatcher;
    private readonly Action _showWindow;
    private readonly Action _reload;
    private readonly Action _exitApp;
    private readonly Func<bool> _suspendEnabled;
    private readonly Func<bool> _webReady;
    private readonly Func<bool> _downloadActive;
    private readonly Func<Task<bool>> _trySuspendAsync;
    private readonly Func<bool> _resume;
    private readonly Func<IReadOnlyList<AccountProfile>> _profiles;
    private readonly Func<string> _activeProfileId;
    private readonly Action<string> _switchProfile;
    private readonly Action _openDownloads;
    private readonly Action _openSettings;
    private readonly Action _openNewWindow;
    private readonly Action<string> _openDownloadFile;
    private readonly Func<IReadOnlyList<DownloadRecord>> _recentDownloads;
    private readonly Func<SuspendTimeout> _suspendTimeout;
    private readonly Func<bool> _microphoneGranted;
    private readonly Func<bool> _singleClickRestore;
    private readonly Func<NotificationSuppression> _notificationGate;
    // App lock (S7 #64): while locked the menu must not leak page
    // titles, unread counts, or download names, and its window actions
    // stay disabled (Open still routes — the window answers with the
    // lock screen). All three are optional: no lock delegates, no lock UI.
    private readonly Func<bool>? _isLocked;
    private readonly Func<bool>? _lockConfigured;
    private readonly Action? _lockNow;
    // Quick dictate (S11 #97): optional like the lock delegates.
    private readonly Action? _quickDictate;
    // Command palette (R1 #110): middle-click opens it compact.
    private readonly Action? _openPalette;
    // Workspaces (R2 #115): optional like the lock delegates — the
    // submenu lists the saved workspaces (glyph + name, #119) and
    // applies one through the main window's own apply path.
    private readonly Func<IReadOnlyList<Workspace>>? _workspaces;
    private readonly Func<string?>? _activeWorkspaceId;
    private readonly Action<string>? _applyWorkspace;
    // Profile groups (R3 #127): optional like the workspace delegates —
    // when supplied, the switch submenu gathers grouped profiles
    // under their group headings.
    private readonly Func<IReadOnlyList<ProfileGroup>>? _profileGroups;
    // Tray icon styles (R6 #155): optional like the groups delegates —
    // when supplied, the icon composition reads the user's style
    // choices live; absent means the long-standing Color + badge face.
    private readonly Func<TrayIconStyle>? _trayIconStyle;
    private readonly Func<TrayOverlayStyle>? _trayOverlayStyle;

    private Forms.NotifyIcon? _icon;
    private Forms.ToolStripMenuItem? _openItem;
    private Forms.ToolStripMenuItem? _dictateItem;
    private Forms.ToolStripMenuItem? _newWindowItem;
    private Forms.ToolStripMenuItem? _reloadItem;
    private Forms.ToolStripMenuItem? _settingsItem;
    private Forms.ToolStripMenuItem? _lockItem;
    private Forms.ToolStripMenuItem? _switchItem;
    private Forms.ToolStripMenuItem? _workspacesItem;
    private Forms.ToolStripMenuItem? _downloadsItem;
    private Forms.ToolStripMenuItem? _statusItem;
    private bool _locked;
    private DispatcherTimer? _suspendTimer;
    private DispatcherTimer? _clickTimer;
    private bool _hidden;
    private bool _suspended;
    private bool _balloonShown;
    private bool _micBlockLogged;

    // Icon composition (S4 #31/#37): the extracted app icon plus the
    // Core-planned overlays, painted by TrayIconRenderer.
    private System.Drawing.Icon? _baseIcon;
    private System.Drawing.Icon? _composedIcon;
    private string _baseText = "MuseDesktop";
    private string? _profileColorHex;
    private string? _timerText;
    private AttentionSignal _attention = AttentionSignal.None;

    public TrayController(
        DiagnosticLog log,
        Dispatcher dispatcher,
        Action showWindow,
        Action reload,
        Action exitApp,
        Func<bool> suspendEnabled,
        Func<bool> webReady,
        Func<bool> downloadActive,
        Func<Task<bool>> trySuspendAsync,
        Func<bool> resume,
        Func<IReadOnlyList<AccountProfile>> profiles,
        Func<string> activeProfileId,
        Action<string> switchProfile,
        Action openDownloads,
        Action openSettings,
        Action openNewWindow,
        Action<string> openDownloadFile,
        Func<IReadOnlyList<DownloadRecord>> recentDownloads,
        Func<SuspendTimeout> suspendTimeout,
        Func<bool> microphoneGranted,
        Func<bool> singleClickRestore,
        Func<NotificationSuppression> notificationGate,
        Func<bool>? isLocked = null,
        Func<bool>? lockConfigured = null,
        Action? lockNow = null,
        Action? quickDictate = null,
        Action? openPalette = null,
        Func<IReadOnlyList<Workspace>>? workspaces = null,
        Func<string?>? activeWorkspaceId = null,
        Action<string>? applyWorkspace = null,
        Func<IReadOnlyList<ProfileGroup>>? profileGroups = null,
        Func<TrayIconStyle>? trayIconStyle = null,
        Func<TrayOverlayStyle>? trayOverlayStyle = null)
    {
        _log = log;
        _dispatcher = dispatcher;
        _showWindow = showWindow;
        _reload = reload;
        _exitApp = exitApp;
        _suspendEnabled = suspendEnabled;
        _webReady = webReady;
        _downloadActive = downloadActive;
        _trySuspendAsync = trySuspendAsync;
        _resume = resume;
        _profiles = profiles;
        _activeProfileId = activeProfileId;
        _switchProfile = switchProfile;
        _openDownloads = openDownloads;
        _openSettings = openSettings;
        _openNewWindow = openNewWindow;
        _openDownloadFile = openDownloadFile;
        _recentDownloads = recentDownloads;
        _suspendTimeout = suspendTimeout;
        _microphoneGranted = microphoneGranted;
        _singleClickRestore = singleClickRestore;
        _notificationGate = notificationGate;
        _isLocked = isLocked;
        _lockConfigured = lockConfigured;
        _lockNow = lockNow;
        _quickDictate = quickDictate;
        _openPalette = openPalette;
        _workspaces = workspaces;
        _activeWorkspaceId = activeWorkspaceId;
        _applyWorkspace = applyWorkspace;
        _profileGroups = profileGroups;
        _trayIconStyle = trayIconStyle;
        _trayOverlayStyle = trayOverlayStyle;
    }

    public bool IsSuspended => _suspended;

    /// <summary>Create the icon. Failure is logged, never fatal — the window must still start.</summary>
    public void Build()
    {
        try
        {
            _baseIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "")
                       ?? System.Drawing.SystemIcons.Application;
            _icon = new Forms.NotifyIcon
            {
                Text = _baseText,
                Visible = true,
                Icon = _baseIcon,
            };
            var menu = new Forms.ContextMenuStrip();
            _openItem = new Forms.ToolStripMenuItem(Strings.Get("tray.open_claude"));
            _openItem.Click += (_, _) => _dispatcher.Invoke(_showWindow);
            menu.Items.Add(_openItem);
            // Quick dictate (S11 #97): one click to a window whose
            // page has focus, ready for Muse's voice button. Gated
            // while locked like the other content actions (its
            // handler answers with the lock screen too, belt and
            // braces — a voice session is content).
            if (_quickDictate is not null)
            {
                _dictateItem = new Forms.ToolStripMenuItem(Strings.Get("tray.quick_dictate"));
                _dictateItem.Click += (_, _) => _dispatcher.Invoke(_quickDictate);
                menu.Items.Add(_dictateItem);
            }
            _newWindowItem = new Forms.ToolStripMenuItem(Strings.Get("tray.new_window"));
            _newWindowItem.Click += (_, _) => _dispatcher.Invoke(_openNewWindow);
            menu.Items.Add(_newWindowItem);
            _reloadItem = new Forms.ToolStripMenuItem(Strings.Get("tray.reload"));
            _reloadItem.Click += (_, _) => _dispatcher.Invoke(_reload);
            menu.Items.Add(_reloadItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            // Downloads (S4 #32): the window action plus the newest
            // completed files, rebuilt on every open.
            _downloadsItem = new Forms.ToolStripMenuItem(Strings.Get("tray.downloads"));
            menu.Items.Add(_downloadsItem);
            _settingsItem = new Forms.ToolStripMenuItem(Strings.Get("tray.settings"));
            _settingsItem.Click += (_, _) => _dispatcher.Invoke(_openSettings);
            menu.Items.Add(_settingsItem);
            // App lock (S7 #64): visible only while a lock is set and
            // the app isn't already locked (refreshed on every open).
            if (_lockNow is not null)
            {
                _lockItem = new Forms.ToolStripMenuItem(Strings.Get("tray.lock_now"));
                _lockItem.Click += (_, _) => _dispatcher.Invoke(_lockNow);
                menu.Items.Add(_lockItem);
            }
            // Switch profile (S3 #25): rebuilt on every open so it always
            // reflects the current list/active profile.
            _switchItem = new Forms.ToolStripMenuItem(Strings.Get("tray.switch_profile"));
            menu.Items.Add(_switchItem);
            // Workspaces (R2 #115): same rebuild-on-open pattern; the
            // item exists only when the window wired the delegates.
            if (_workspaces is not null && _applyWorkspace is not null)
            {
                _workspacesItem = new Forms.ToolStripMenuItem(Strings.Get("tray.workspaces"));
                menu.Items.Add(_workspacesItem);
            }
            // Suspend status (S4 #32): informational, refreshed on open.
            _statusItem = new Forms.ToolStripMenuItem { Enabled = false };
            menu.Items.Add(_statusItem);
            menu.Opening += (_, _) =>
            {
                RebuildDownloadsMenu();
                RebuildSwitchMenu();
                RebuildWorkspacesMenu();
                RefreshLockState();
                RefreshStatusItem();
            };
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Strings.Get("tray.exit"), null, (_, _) => _dispatcher.Invoke(_exitApp));
            _icon.ContextMenuStrip = menu;
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                    OnIconLeftClick();
                // R1 #110: middle-click opens the command palette in
                // its compact form. The main window's handler owns
                // the lock gate — while locked it answers with the
                // lock screen, like every other tray surface.
                else if (e.Button == Forms.MouseButtons.Middle && _openPalette is not null)
                    _dispatcher.Invoke(_openPalette);
            };
            _icon.DoubleClick += (_, _) =>
            {
                // A double-click always opens — and cancels any pending
                // single-click restore so the window doesn't open twice.
                _clickTimer?.Stop();
                _dispatcher.Invoke(_showWindow);
            };
        }
        catch (Exception ex)
        {
            _log.Warn($"Tray creation failed: {ex.Message}");
            _icon = null;
        }
    }

    /// <summary>
    /// Single-click restore (S4 #36): when the setting is on, a left
    /// click schedules the restore just past the double-click window —
    /// if a second click turns it into a double-click, that handler
    /// cancels the timer and opens immediately. When the setting is
    /// off, clicks do nothing (the double-click handler owns restore),
    /// which is the behavior the app has always had.
    /// </summary>
    private void OnIconLeftClick()
    {
        if (!_singleClickRestore())
            return;
        _clickTimer?.Stop();
        _clickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime + 80),
        };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer?.Stop();
            _dispatcher.Invoke(_showWindow);
        };
        _clickTimer.Start();
    }

    // ───────────────────────── app lock (S7 #64) ─────────────────────────

    /// <summary>
    /// Push the lock state in (the main window owns it): while locked
    /// the tooltip goes plain, the attention badge/suffix clear, and
    /// the menu gates itself on open. Downloads already in flight are
    /// untouched — the lock gates surfaces, not the engine.
    /// </summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        if (locked)
        {
            _attention = AttentionSignal.None;
            RefreshIcon();
        }
        ApplyText();
    }

    /// <summary>Re-read the lock delegates and gate the menu (runs on every menu open, after the rebuilds it overrides).</summary>
    private void RefreshLockState()
    {
        var locked = _isLocked?.Invoke() ?? false;
        if (locked != _locked)
            SetLocked(locked);
        if (_lockItem is not null)
            _lockItem.Visible = (_lockConfigured?.Invoke() ?? false) && !locked;
        // While locked: nothing that names content or opens a surface
        // stays enabled. "Open Muse" remains — the main window
        // answers it with the lock screen. Exit always remains. The
        // switch item's enabled state is the switch rebuild's business
        // except while locked, where it is forced off.
        if (_newWindowItem is not null) _newWindowItem.Enabled = !locked;
        if (_dictateItem is not null) _dictateItem.Enabled = !locked;
        if (_reloadItem is not null) _reloadItem.Enabled = !locked;
        if (_downloadsItem is not null) _downloadsItem.Enabled = !locked;
        if (_settingsItem is not null) _settingsItem.Enabled = !locked;
        if (locked && _switchItem is not null) _switchItem.Enabled = false;
        if (locked && _workspacesItem is not null) _workspacesItem.Enabled = false;
    }

    /// <summary>
    /// Rebuild the Downloads submenu (S4 #32): open the manager, then
    /// the newest completed downloads (click = open the file). Failed
    /// records aren't offered — there may be no file to open.
    /// </summary>
    private void RebuildDownloadsMenu()
    {
        if (_downloadsItem is null)
            return;
        try
        {
            foreach (Forms.ToolStripItem old in _downloadsItem.DropDownItems)
                old.Dispose();
            _downloadsItem.DropDownItems.Clear();

            if (_locked)
            {
                // File names are content — behind the lock they don't
                // appear even in a disabled submenu's items.
                _downloadsItem.DropDownItems.Add(
                    new Forms.ToolStripMenuItem(Strings.Get("tray.locked")) { Enabled = false });
                return;
            }

            _downloadsItem.DropDownItems.Add(Strings.Get("tray.open_downloads_window"), null,
                (_, _) => _dispatcher.Invoke(_openDownloads));

            var recent = _recentDownloads()
                .Where(r => r.State == DownloadRecordState.Completed)
                .Take(5)
                .ToList();
            if (recent.Count == 0)
            {
                _downloadsItem.DropDownItems.Add(new Forms.ToolStripMenuItem(Strings.Get("tray.no_downloads_yet")) { Enabled = false });
                return;
            }
            _downloadsItem.DropDownItems.Add(new Forms.ToolStripSeparator());
            foreach (var record in recent)
            {
                var path = record.FullPath;
                var item = new Forms.ToolStripMenuItem(record.FileName)
                {
                    ToolTipText = path,
                };
                item.Click += (_, _) => _dispatcher.Invoke(() => _openDownloadFile(path));
                _downloadsItem.DropDownItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Tray downloads menu failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Rebuild the Switch-profile submenu (S3 #25): one item per profile,
    /// the active one checked and disabled, a real color swatch as the
    /// item image where the profile has an accent color. The switch
    /// itself is the main window's restart flow, via the delegate.
    /// </summary>
    private void RebuildSwitchMenu()
    {
        if (_switchItem is null)
            return;
        try
        {
            foreach (Forms.ToolStripItem old in _switchItem.DropDownItems)
            {
                old.Image?.Dispose();
                old.Dispose();
            }
            _switchItem.DropDownItems.Clear();

            // R3: archived profiles are hidden (#130), labels carry
            // the avatar glyph when there is one (#121), and groups
            // gather their members under a heading (#127).
            var profiles = Profiles.Visible(_profiles());
            var activeId = _activeProfileId();
            _switchItem.Enabled = profiles.Count > 1;
            var placed = new HashSet<string>();
            if (_profileGroups is not null)
            {
                foreach (var group in _profileGroups())
                {
                    var members = profiles.Where(p => p.GroupId == group.Id).ToList();
                    if (members.Count == 0)
                        continue;
                    var header = new Forms.ToolStripMenuItem(group.Name);
                    foreach (var member in members)
                    {
                        header.DropDownItems.Add(BuildSwitchItem(member, activeId));
                        placed.Add(member.Id);
                    }
                    _switchItem.DropDownItems.Add(header);
                }
            }
            foreach (var p in profiles)
            {
                if (placed.Contains(p.Id))
                    continue;
                _switchItem.DropDownItems.Add(BuildSwitchItem(p, activeId));
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Tray switch-profile menu failed: {ex.Message}");
        }
    }

    /// <summary>One switchable profile row for the tray menu (R3 label: glyph-or-dot; the color rides as the swatch image).</summary>
    private Forms.ToolStripMenuItem BuildSwitchItem(AccountProfile p, string activeId)
    {
        var id = p.Id;
        var item = new Forms.ToolStripMenuItem(Profiles.DisplayLabel(p))
        {
            Checked = id == activeId,
            Enabled = id != activeId,
        };
        if (ProfileColors.HexFor(p.DisplayColor) is { } hex)
            item.Image = ColorSwatch(hex);
        item.Click += (_, _) => _dispatcher.Invoke(() => _switchProfile(id));
        return item;
    }

    /// <summary>
    /// Rebuild the Workspaces submenu (R2 #115): one item per saved
    /// workspace, labeled with its glyph (#119), the active one
    /// checked, and the workspace's accent shown as a swatch when it
    /// has one. With no workspaces the item disables itself with the
    /// reason — the tray never offers an empty promise.
    /// </summary>
    private void RebuildWorkspacesMenu()
    {
        if (_workspacesItem is null || _workspaces is null || _applyWorkspace is null)
            return;
        try
        {
            foreach (Forms.ToolStripItem old in _workspacesItem.DropDownItems)
            {
                old.Image?.Dispose();
                old.Dispose();
            }
            _workspacesItem.DropDownItems.Clear();

            var workspaces = _workspaces();
            var activeId = _activeWorkspaceId?.Invoke();
            _workspacesItem.Enabled = workspaces.Count > 0;
            if (workspaces.Count == 0)
            {
                _workspacesItem.DropDownItems.Add(
                    new Forms.ToolStripMenuItem(Strings.Get("tray.no_workspaces_saved_yet")) { Enabled = false });
                return;
            }
            foreach (var ws in workspaces)
            {
                var id = ws.Id;
                var item = new Forms.ToolStripMenuItem(WorkspaceGlyphs.MenuLabel(ws))
                {
                    Checked = id == activeId,
                };
                if (ProfileColors.HexFor(ws.AccentColor) is { } hex)
                    item.Image = ColorSwatch(hex);
                item.Click += (_, _) => _dispatcher.Invoke(() => _applyWorkspace(id));
                _workspacesItem.DropDownItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Tray workspaces menu failed: {ex.Message}");
        }
    }

    private void RefreshStatusItem()
    {
        if (_statusItem is null)
            return;
        _statusItem.Text = _locked
            ? Strings.Get("tray.status_locked")
            : SuspendLineLocalized();
    }

    /// <summary>
    /// The suspend status line in the active language (R7 #161): the
    /// localized twin of Core's TrayStatusText.SuspendLine — same
    /// branches, same inputs; R7Tests pin the catalog templates
    /// against the Core composer's output.
    /// </summary>
    private string SuspendLineLocalized()
    {
        if (_suspended)
            return Strings.Get("tray.webview_suspended");
        if (!_suspendEnabled())
            return Strings.Get("tray.webview_suspend_off");
        var timeout = _suspendTimeout();
        if (timeout == SuspendTimeout.Never)
            return Strings.Get("tray.webview_stays_awake");
        return _hidden
            ? Strings.Format("tray.webview_suspends_after", SuspendLabelLocalized(timeout))
            : Strings.Get("tray.webview_awake");
    }

    private static string SuspendLabelLocalized(SuspendTimeout timeout) => timeout switch
    {
        SuspendTimeout.OneMinute => Strings.Get("tray.suspend.1min"),
        SuspendTimeout.FiveMinutes => Strings.Get("tray.suspend.5min"),
        SuspendTimeout.FifteenMinutes => Strings.Get("tray.suspend.15min"),
        _ => Strings.Get("tray.suspend.never"),
    };

    private static System.Drawing.Bitmap ColorSwatch(string hex)
    {
        var bmp = new System.Drawing.Bitmap(12, 12);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new System.Drawing.SolidBrush(System.Drawing.ColorTranslator.FromHtml(hex));
        g.FillEllipse(brush, 1, 1, 10, 10);
        return bmp;
    }

    // ───────────────────────── notifications (S4 #38) ─────────────────────────

    /// <summary>
    /// The quiet-hours gate every tray surface passes through: a
    /// suppression is always logged with its reason (never silent).
    /// </summary>
    private bool NotificationAllowed(string what)
    {
        var suppression = _notificationGate();
        if (suppression == NotificationSuppression.None)
            return true;
        _log.Info($"{what} not shown: {NotificationPolicy.ReasonText(suppression)}");
        return false;
    }

    /// <summary>
    /// Download-complete balloon (S2 #18): shown only when the caller has
    /// checked the opt-in setting and that the window is hidden, and
    /// quiet hours (S4 #38) allow it. No sound (NotifyIcon balloons are
    /// silent unless the app asks otherwise — it doesn't), and a failure
    /// here is cosmetic.
    /// </summary>
    public void ShowDownloadComplete(string fileName)
    {
        try
        {
            if (_icon is null) return;
            if (!NotificationAllowed("Download-complete balloon"))
                return;
            _icon.BalloonTipTitle = Strings.Get("ui.download_complete");
            _icon.BalloonTipText = fileName;
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            _log.Warn($"Download-complete balloon failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The Settings test notification (S10 #92): shown even when the
    /// quiet-hours gate would hold back an AUTOMATIC balloon — a test
    /// the user explicitly asked for that silently does nothing
    /// teaches nothing — and the current suppression is returned so
    /// the caller can SAY that automatic notifications are being held
    /// back right now (see Core's GuidedSelfTests.NotificationLine).
    /// </summary>
    public (NotificationSuppression Suppression, bool Shown) ShowTestNotification()
    {
        var suppression = _notificationGate();
        try
        {
            if (_icon is null)
                return (suppression, false);
            _icon.BalloonTipTitle = Strings.Get("ui.musedesktop_test_notification");
            _icon.BalloonTipText = Strings.Get("ui.this_is_the_test_notification_you_asked");
            _icon.ShowBalloonTip(4000);
            _log.Info(suppression == NotificationSuppression.None
                ? "Test notification shown (asked for in Settings)"
                : $"Test notification shown (asked for in Settings) while automatic notifications are held back: {NotificationPolicy.ReasonText(suppression)}");
            return (suppression, true);
        }
        catch (Exception ex)
        {
            _log.Warn($"Test notification failed: {ex.Message}");
            return (suppression, false);
        }
    }

    // ───────────────────────── icon composition (S4 #31/#37) ─────────────────────────

    /// <summary>Tray tooltip base text (the profile indicator when several profiles exist); the attention suffix is composed in.</summary>
    public void SetText(string text)
    {
        _baseText = text;
        ApplyText();
    }

    /// <summary>Repaint the icon after appearance settings changed (R6 #155) — the style delegates are read live at compose time, so a repaint is all a style change needs.</summary>
    public void RefreshAppearance() => RefreshIcon();

    /// <summary>The active profile's accent color (S4 #37): repaint the tray icon with its dot. Null key = no dot.</summary>
    public void SetProfileColor(string? colorKey)
    {
        var hex = ProfileColors.HexFor(colorKey);
        if (hex == _profileColorHex)
            return;
        _profileColorHex = hex;
        RefreshIcon();
    }

    /// <summary>
    /// The page's attention signal (S4 #31): repaint the tray icon with
    /// (or without) the badge and refresh the tooltip suffix. Driven by
    /// the main window's title watcher — the tray never invents state.
    /// </summary>
    public void SetAttention(AttentionSignal signal)
    {
        // S7 #64: while locked, attention never reaches the tray — no
        // unread-count suffix, no badge (the count is content).
        if (_locked)
            signal = AttentionSignal.None;
        if (signal == _attention)
            return;
        _attention = signal;
        RefreshIcon();
        ApplyText();
    }

    private void ApplyText()
    {
        if (_icon is null)
            return;
        var text = _locked
            ? Strings.Get("tray.tooltip_locked")
            : _baseText + TrayIconPlanner.TooltipSuffix(_attention)
              + (string.IsNullOrEmpty(_timerText) ? "" : $" · {_timerText}");
        // NotifyIcon text is capped at 127 characters; a long timer
        // line must never push the tooltip into an exception.
        _icon.Text = text.Length <= 127 ? text : text[..127];
    }

    /// <summary>
    /// The session/focus timer's tray line (R5 #141/#142): appended
    /// to the tooltip so the countdown is visible without opening the
    /// app. Null/empty clears it. Cheap to call every second — it
    /// no-ops when the text is unchanged.
    /// </summary>
    public void SetTimerText(string? text)
    {
        if (_timerText == text)
            return;
        _timerText = text;
        ApplyText();
    }

    /// <summary>
    /// A gentle reminder balloon (R5 #141/#142): the break suggestion
    /// and session-timer phase changes. Passes through the same
    /// quiet-hours / Do-Not-Disturb gate as every MuseDesktop
    /// balloon — a reminder that ignores quiet hours isn't gentle.
    /// </summary>
    public void ShowReminder(string title, string text)
    {
        try
        {
            if (_icon is null) return;
            if (!NotificationAllowed($"{title} balloon"))
                return;
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            _log.Warn($"Reminder balloon failed: {ex.Message}");
        }
    }

    private void RefreshIcon()
    {
        if (_icon is null || _baseIcon is null)
            return;
        try
        {
            // R6 #155: the composition generalizes the S4 plan — the
            // default delegates reproduce it exactly (Color style,
            // numbered badge, the profile dot in its own color).
            var composition = TrayIconPlanner.Compose(
                _trayIconStyle?.Invoke() ?? TrayIconStyle.Color,
                _trayOverlayStyle?.Invoke() ?? TrayOverlayStyle.NumberBadge,
                _profileColorHex, _attention);
            if (composition.IsPlainIcon)
            {
                _icon.Icon = _baseIcon;
                _composedIcon?.Dispose();
                _composedIcon = null;
                return;
            }
            var composed = TrayIconRenderer.Compose(_baseIcon, composition, _profileColorHex);
            var old = _composedIcon;
            _icon.Icon = composed;
            _composedIcon = composed;
            old?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn($"Tray icon compose failed: {ex.Message}");
        }
    }

    /// <summary>The window was hidden to the tray: maybe show the balloon once, and arm the suspend timer.</summary>
    public void OnHidden(bool showBalloon)
    {
        _hidden = true;
        _micBlockLogged = false;
        ArmSuspendTimer();
        if (showBalloon && !_balloonShown && _icon is not null
            && NotificationAllowed("First-hide balloon"))
        {
            _balloonShown = true;
            _icon.BalloonTipTitle = Strings.Get("ui.musedesktop_is_still_running");
            _icon.BalloonTipText = _singleClickRestore()
                ? Strings.Get("tray.balloon_hidden_single")
                : Strings.Get("tray.balloon_hidden_double");
            _icon.ShowBalloonTip(4000);
        }
    }

    /// <summary>The window is back: stop any pending suspend and wake the renderer if it slept.</summary>
    public void OnShown()
    {
        _hidden = false;
        _suspendTimer?.Stop();
        if (_suspended && _resume())
            _suspended = false;
    }

    /// <summary>Settings turned tray suspend off: drop any pending timer.</summary>
    public void CancelPendingSuspend() => _suspendTimer?.Stop();

    private void ArmSuspendTimer()
    {
        if (!_webReady() || _suspended)
            return;
        // Disabled / Never: no timer at all (S4 #34). The tick handler
        // re-decides anyway, so a setting changed mid-hide still wins.
        var decision = SuspendPolicy.Decide(
            _suspendEnabled(), _suspendTimeout(), _downloadActive(), _microphoneGranted());
        if (decision is SuspendDecision.Disabled or SuspendDecision.NeverSuspend)
            return;
        if (SuspendTimeouts.Delay(_suspendTimeout()) is not { } delay)
            return;
        _suspendTimer?.Stop();
        _suspendTimer = new DispatcherTimer { Interval = delay };
        _suspendTimer.Tick += OnSuspendTimerTick;
        _suspendTimer.Start();
    }

    private async void OnSuspendTimerTick(object? sender, EventArgs e)
    {
        _suspendTimer?.Stop();
        if (!_hidden || !_webReady() || _suspended)
            return;
        switch (SuspendPolicy.Decide(
                    _suspendEnabled(), _suspendTimeout(), _downloadActive(), _microphoneGranted()))
        {
            case SuspendDecision.SuspendNow:
                _suspended = await _trySuspendAsync();
                break;
            case SuspendDecision.BlockedByDownload:
                // Never sleep mid-download; check again shortly.
                RearmBlockedRecheck();
                break;
            case SuspendDecision.BlockedByMicrophone:
                // S4 #35: the mic was granted this session, so a voice
                // session may be live — don't risk suspending under it.
                // Logged once per hide, not on every recheck.
                if (!_micBlockLogged)
                {
                    _micBlockLogged = true;
                    _log.Info("Tray suspend skipped: the microphone was granted to Muse this session, so a voice session may be active");
                }
                RearmBlockedRecheck();
                break;
            // Disabled / NeverSuspend: the setting changed while the
            // timer was armed — let the timer die.
        }
    }

    private void RearmBlockedRecheck()
    {
        _suspendTimer = new DispatcherTimer { Interval = BlockedRecheck };
        _suspendTimer.Tick += OnSuspendTimerTick;
        _suspendTimer.Start();
    }

    public void Dispose()
    {
        _suspendTimer?.Stop();
        _clickTimer?.Stop();
        _icon?.Dispose();
        _icon = null;
        _composedIcon?.Dispose();
        _composedIcon = null;
        _baseIcon?.Dispose();
        _baseIcon = null;
    }
}
