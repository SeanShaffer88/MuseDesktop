using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The global show/hide hotkey (S11 #96): RegisterHotKey on the MAIN
/// WINDOW's handle. That handle is the right host — it exists from
/// SourceInitialized until the process exits, because closing the
/// window hides it to the tray instead of destroying it; the
/// single-instance wake event can't host a hotkey (it isn't a window),
/// and a separate message-only window would be a second lifetime to
/// keep in sync for no benefit. Windows delivers WM_HOTKEY to the
/// registered window whether it is visible, hidden, or behind other
/// apps — which is the whole point of the feature.
///
/// All decisions live in Core (<see cref="GlobalHotkey"/>); this class
/// only owns the P/Invoke, the HwndSource hook, and the status line
/// Settings displays. Registration failing (another app owns the
/// combination) is a state, never an exception: it is logged plainly
/// and shown in Settings.
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly DiagnosticLog _log;
    private readonly Action _pressed;
    private readonly int _registrationId;
    private readonly Func<AppSettings, HotkeyResolution> _resolver;
    private readonly Func<ShortcutGesture, string> _activeText;
    private readonly string _featureName;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _registered;
    private bool _disposed;

    /// <summary>What Settings shows about the hotkey right now (live registration state, not the saved clone's).</summary>
    public string StatusText { get; private set; } = "";

    /// <summary>
    /// One RegisterHotKey registration on the main window's handle.
    /// The defaults are the S11 show/hide hotkey exactly; R5's
    /// quick-note hotkey (#148) passes its own registration id,
    /// resolver (Core's QuickNoteHotkey), and wording — the P/Invoke,
    /// the hook, and the failure handling are shared, because a
    /// registration that fails is the same state for both.
    /// </summary>
    public GlobalHotkeyManager(DiagnosticLog log, Action pressed,
        int registrationId = HotkeyNative.RegistrationId,
        Func<AppSettings, HotkeyResolution>? resolver = null,
        Func<ShortcutGesture, string>? activeText = null,
        string featureName = "global hotkey")
    {
        _log = log;
        _pressed = pressed;
        _registrationId = registrationId;
        _resolver = resolver ?? (settings => GlobalHotkey.Resolve(
            settings.GlobalHotkeyEnabled, settings.GlobalHotkeyGesture, settings.ShortcutOverrides));
        _activeText = activeText ?? (gesture =>
            Strings.Format("hotkey.active_show_hide", gesture.DisplayText()));
        _featureName = featureName;
    }

    /// <summary>Attach the message hook. Call once the window's handle exists (SourceInitialized).</summary>
    public void Attach(Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
    }

    /// <summary>True when this app's main window is the foreground window (the toggle's "in front" test).</summary>
    public bool IsWindowForeground => _handle != IntPtr.Zero && GetForegroundWindow() == _handle;

    /// <summary>
    /// (Re)apply the saved settings: unregister whatever was there,
    /// resolve Core's policy, register when it says Ready. Called at
    /// startup and after every Settings OK, so a binding change takes
    /// effect immediately — including freeing the old combination.
    /// </summary>
    public void Apply(AppSettings settings)
    {
        if (_disposed || _handle == IntPtr.Zero)
            return;
        Unregister();
        var resolution = _resolver(settings);
        switch (resolution.Kind)
        {
            case HotkeyResolutionKind.Disabled:
                StatusText = Strings.Format("hotkey.off", _featureName);
                return;
            case HotkeyResolutionKind.BlockedByConflict:
                StatusText = Strings.Format("hotkey.clash",
                    resolution.Gesture.DisplayText(), resolution.ConflictOwner, _featureName);
                _log.Warn($"Global hotkey not registered: {resolution.Gesture.DisplayText()} " +
                          $"is already used by {resolution.ConflictOwner} inside the app");
                return;
        }
        if (!HotkeyNative.TryVirtualKey(resolution.Gesture.Key, out var vk))
        {
            StatusText = Strings.Format("hotkey.cant_register", resolution.Gesture.DisplayText());
            _log.Warn($"Global hotkey not registered: no virtual key for '{resolution.Gesture.Key}'");
            return;
        }
        try
        {
            if (RegisterHotKey(_handle, _registrationId,
                    HotkeyNative.ModifiersFor(resolution.Gesture), vk))
            {
                _registered = true;
                StatusText = _activeText(resolution.Gesture);
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                StatusText = Strings.Format("hotkey.register_failed_other_app", resolution.Gesture.DisplayText());
                _log.Warn($"Global hotkey {resolution.Gesture.DisplayText()} could not be registered " +
                          $"(Win32 error {error}) — another app may own that combination");
            }
        }
        catch (Exception ex)
        {
            StatusText = Strings.Format("hotkey.register_failed_log", _featureName);
            _log.Warn($"Global hotkey registration failed: {ex.Message}");
        }
    }

    private void Unregister()
    {
        if (!_registered)
            return;
        _registered = false;
        try { UnregisterHotKey(_handle, _registrationId); }
        catch { /* teardown must not hang on the API */ }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == HotkeyNative.WmHotkey && wParam.ToInt32() == _registrationId)
        {
            handled = true;
            _pressed();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Unregister();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }
}
