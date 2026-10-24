using System.Windows.Threading;
using MuseDesktop.Core;
using Forms = System.Windows.Forms;

namespace MuseDesktop;

/// <summary>
/// The tray icon and everything that hangs off it: the context menu,
/// the first-hide balloon, and the v0.2.0 suspend timer that puts the
/// WebView2 renderer to sleep after the window has been hidden a while
/// (v0.4.0 — extracted from the main window). The WebView2 operations
/// themselves stay with the window and arrive as delegates.
/// </summary>
public sealed class TrayController : IDisposable
{
    // After this long hidden, the renderer sleeps.
    private static readonly TimeSpan SuspendDelay = TimeSpan.FromMinutes(5);

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

    private Forms.NotifyIcon? _icon;
    private Forms.ToolStripMenuItem? _switchItem;
    private DispatcherTimer? _suspendTimer;
    private bool _hidden;
    private bool _suspended;
    private bool _balloonShown;

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
        Action<string> switchProfile)
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
    }

    public bool IsSuspended => _suspended;

    /// <summary>Create the icon. Failure is logged, never fatal — the window must still start.</summary>
    public void Build()
    {
        try
        {
            _icon = new Forms.NotifyIcon
            {
                Text = "MuseDesktop",
                Visible = true,
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "")
                       ?? System.Drawing.SystemIcons.Application,
            };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open Muse", null, (_, _) => _dispatcher.Invoke(_showWindow));
            menu.Items.Add("Reload", null, (_, _) => _dispatcher.Invoke(_reload));
            // Switch profile (S3 #25): rebuilt on every open so it always
            // reflects the current list/active profile.
            _switchItem = new Forms.ToolStripMenuItem("Switch profile");
            menu.Items.Add(_switchItem);
            menu.Opening += (_, _) => RebuildSwitchMenu();
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => _dispatcher.Invoke(_exitApp));
            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += (_, _) => _dispatcher.Invoke(_showWindow);
        }
        catch (Exception ex)
        {
            _log.Warn($"Tray creation failed: {ex.Message}");
            _icon = null;
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

            var profiles = _profiles();
            var activeId = _activeProfileId();
            _switchItem.Enabled = profiles.Count > 1;
            foreach (var p in profiles)
            {
                var id = p.Id;
                var item = new Forms.ToolStripMenuItem(Profiles.MenuLabel(p))
                {
                    Checked = id == activeId,
                    Enabled = id != activeId,
                };
                if (ProfileColors.HexFor(p.DisplayColor) is { } hex)
                    item.Image = ColorSwatch(hex);
                item.Click += (_, _) => _dispatcher.Invoke(() => _switchProfile(id));
                _switchItem.DropDownItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Tray switch-profile menu failed: {ex.Message}");
        }
    }

    private static System.Drawing.Bitmap ColorSwatch(string hex)
    {
        var bmp = new System.Drawing.Bitmap(12, 12);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new System.Drawing.SolidBrush(System.Drawing.ColorTranslator.FromHtml(hex));
        g.FillEllipse(brush, 1, 1, 10, 10);
        return bmp;
    }

    /// <summary>
    /// Download-complete balloon (S2 #18): shown only when the caller has
    /// checked the opt-in setting and that the window is hidden. No sound
    /// (NotifyIcon balloons are silent unless the app asks otherwise — it
    /// doesn't), and a failure here is cosmetic.
    /// </summary>
    public void ShowDownloadComplete(string fileName)
    {
        try
        {
            if (_icon is null) return;
            _icon.BalloonTipTitle = "Download complete";
            _icon.BalloonTipText = fileName;
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            _log.Warn($"Download-complete balloon failed: {ex.Message}");
        }
    }

    /// <summary>Tray tooltip (the profile indicator when several profiles exist).</summary>
    public void SetText(string text)
    {
        if (_icon is not null)
            _icon.Text = text;
    }

    /// <summary>The window was hidden to the tray: maybe show the balloon once, and arm the suspend timer.</summary>
    public void OnHidden(bool showBalloon)
    {
        _hidden = true;
        ArmSuspendTimer();
        if (showBalloon && !_balloonShown && _icon is not null)
        {
            _balloonShown = true;
            _icon.BalloonTipTitle = "MuseDesktop is still running";
            _icon.BalloonTipText = "Muse keeps working in the cloud. Double-click the tray icon to come back.";
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
        if (!_suspendEnabled() || !_webReady() || _suspended)
            return;
        _suspendTimer?.Stop();
        _suspendTimer = new DispatcherTimer { Interval = SuspendDelay };
        _suspendTimer.Tick += OnSuspendTimerTick;
        _suspendTimer.Start();
    }

    private async void OnSuspendTimerTick(object? sender, EventArgs e)
    {
        _suspendTimer?.Stop();
        if (!_hidden || !_webReady() || _suspended)
            return;
        if (_downloadActive())
        {
            // Never sleep mid-download; check again shortly.
            _suspendTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            _suspendTimer.Tick += OnSuspendTimerTick;
            _suspendTimer.Start();
            return;
        }
        _suspended = await _trySuspendAsync();
    }

    public void Dispose()
    {
        _suspendTimer?.Stop();
        _icon?.Dispose();
        _icon = null;
    }
}
