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

    private Forms.NotifyIcon? _icon;
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
        Func<bool> resume)
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
