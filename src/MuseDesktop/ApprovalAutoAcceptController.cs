using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>Native opt-in gate for every scan. No autonomous timer is installed in the page.</summary>
internal sealed class ApprovalAutoAcceptController
{
    private static readonly ConditionalWeakTable<WebView2, ApprovalAutoAcceptController> Attached = new();
    private readonly Window _window;
    private readonly WebView2 _view;
    private readonly CoreWebView2 _core;
    private readonly MainWindow _main;
    private readonly string _profileId;
    private readonly DispatcherTimer _timer;
    private bool _busy, _stopped, _wasEnabled;

    internal static void Attach(Window window, WebView2 view, string? profileId)
    {
        if (string.IsNullOrEmpty(profileId) || view.CoreWebView2 is null) return;
        Window? owner = window;
        while (owner is not null && owner is not MainWindow) owner = owner.Owner;
        var main = owner as MainWindow; // Never borrow consent from an unrelated workspace/window.
        if (main is null) return;
        if (Attached.TryGetValue(view, out var previous)) previous.Stop();
        Attached.Remove(view);
        Attached.Add(view, new ApprovalAutoAcceptController(window, view, main, profileId));
    }

    private ApprovalAutoAcceptController(Window window, WebView2 view, MainWindow main, string profileId)
    {
        _window = window; _view = view; _core = view.CoreWebView2;
        _main = main; _profileId = profileId;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTick;
        _window.Closed += OnClosed;
        _timer.Start();
    }

    private async void OnTick(object? sender, EventArgs args)
    {
        if (_busy || _stopped) return;
        _busy = true;
        try
        {
            if (!ReferenceEquals(_view.CoreWebView2, _core)) { Stop(); return; }
            var enabled = _main.CanAutoAcceptApprovals(_profileId) &&
                _window.IsVisible && _window.IsEnabled && _view.IsVisible && _view.IsEnabled;
            if (!ApprovalAutoAccept.CanRun(enabled, true, true, _core.Source) || !InputDesktopIsUnlocked())
            {
                var reset = _wasEnabled && DualMode.RestorableRoute(_core.Source) is not null;
                _wasEnabled = false;
                if (reset)
                    await _core.ExecuteScriptAsync(ApprovalAutoAccept.ResetPendingScript);
                return;
            }
            _wasEnabled = true;
            // Recheck consent immediately before dispatch. Turning off/locking
            // prevents future attempts; an already-dispatched action cannot be undone.
            if (!_main.CanAutoAcceptApprovals(_profileId) || !_window.IsVisible || !_window.IsEnabled ||
                !_view.IsVisible || !_view.IsEnabled) return;
            var result = await _core.ExecuteScriptAsync(ApprovalAutoAccept.Script);
            if (result.Length <= 64 && JsonSerializer.Deserialize<string>(result) == "clicked")
                _main.RecordAutoApprovalAttempt();
        }
        catch (ObjectDisposedException) { Stop(); }
        catch (InvalidOperationException) { Stop(); }
        catch { /* DOM/runtime changes fail closed; never log page text or an exception payload. */ }
        finally { _busy = false; }
    }

    private void OnClosed(object? sender, EventArgs e) => Stop();
    private void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _window.Closed -= OnClosed;
    }

    // Unlike WPF IsVisible, this also refuses the lock screen and UAC's
    // secure desktop, including a launch that occurred while already locked.
    private static bool InputDesktopIsUnlocked()
    {
        var desktop = OpenInputDesktop(0, false, 1 /* DESKTOP_READOBJECTS */);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(128);
            return GetUserObjectInformation(desktop, 2 /* UOI_NAME */, name, name.Capacity * 2, out _) &&
                   name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetUserObjectInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder data, int length, out int needed);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr handle);
}
