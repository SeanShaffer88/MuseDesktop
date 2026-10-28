using System.Runtime.InteropServices;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The approval toast channel (R8): shows a real Windows toast —
/// via the vendored NotifyKit DLL — when Core's approval policy
/// fires an alert and the toast setting is on. The tray object is
/// created lazily on the first show and switched to NotifyKit's
/// toast backend; the handle is then cached for the session.
/// Display-only, exactly like the beep it rides with: a toast can
/// be looked at, never answered through this code.
///
/// Failure is quiet by design. A missing DLL, a failed create, or
/// a rejected show is logged once (through the same DiagnosticLog
/// the approval watcher uses), the channel marks itself
/// unavailable for the rest of the session, and nothing ever
/// throws into the watcher path — a broken toast channel must
/// never take the beep, the flash, or the app down with it.
/// </summary>
public sealed class NotifyKitToastNotifier : IDisposable
{
    private readonly DiagnosticLog _log;
    private IntPtr _trayHandle;
    private bool _unavailable;

    public NotifyKitToastNotifier(DiagnosticLog log) => _log = log;

    /// <summary>Show the approval toast once. Never throws.</summary>
    public void Show()
    {
        if (_unavailable)
            return;
        try
        {
            if (_trayHandle == IntPtr.Zero && !Initialize())
                return;
            var rc = NativeMethods.NoTrayShow(
                _trayHandle, ApprovalToastMessages.BuildShowJson(), out var resultJson);
            if (resultJson != IntPtr.Zero)
            {
                var result = Marshal.PtrToStringUTF8(resultJson);
                NativeMethods.NoFreeString(resultJson);
                if (rc == 0)
                    _log.Info($"Approval toast shown (NotifyKit: {result}).");
            }
            if (rc != 0)
                Fail($"NotifyKit refused the toast show (rc {rc}): {ReadLastError()}");
        }
        catch (Exception ex)
        {
            // Includes DllNotFoundException when the vendored DLL is
            // missing from beside the exe.
            Fail($"Approval toast failed: {ex.Message}");
        }
    }

    /// <summary>Create the NotifyKit tray object and switch it to the toast backend; false (after logging) when either step fails.</summary>
    private bool Initialize()
    {
        var rc = NativeMethods.NoTrayCreate(ApprovalToastMessages.BuildTrayConfig(), out _trayHandle);
        if (rc != 0 || _trayHandle == IntPtr.Zero)
        {
            Fail($"NotifyKit tray create failed (rc {rc}): {ReadLastError()}");
            return false;
        }
        rc = NativeMethods.NoTraySetBackend(_trayHandle, "toast");
        if (rc != 0)
        {
            Fail($"NotifyKit toast backend refused (rc {rc}): {ReadLastError()}");
            return false;
        }
        return true;
    }

    /// <summary>Log the failure and close the channel for the session: one log line per failure class, never one per card.</summary>
    private void Fail(string message)
    {
        _log.Warn($"{message} Toast alerts are off for the rest of this session.");
        _unavailable = true;
    }

    /// <summary>NotifyKit's last-error text for the calling thread; empty when even that is unavailable (e.g. the DLL never loaded).</summary>
    private static string ReadLastError()
    {
        try
        {
            var p = NativeMethods.NoLastError();
            return p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Remove the tray object. Called on window close; safe to call more than once, and safe when the channel never initialized.</summary>
    public void Dispose()
    {
        if (_trayHandle == IntPtr.Zero)
            return;
        var handle = _trayHandle;
        _trayHandle = IntPtr.Zero;
        try { NativeMethods.NoTrayRemove(handle); }
        catch { /* shutting down: a tray that won't remove is not worth a sound */ }
    }

    /// <summary>
    /// The NotifyKit v0.2.0 exports, exactly as its header declares
    /// them. Strings cross as UTF-8 JSON both ways; strings the DLL
    /// hands back (results) are freed with NoFreeString, while the
    /// last-error string is borrowed and never freed.
    /// </summary>
    private static class NativeMethods
    {
        [DllImport("notify_kit.dll", EntryPoint = "no_tray_create", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int NoTrayCreate(string configJson, out IntPtr handle);

        [DllImport("notify_kit.dll", EntryPoint = "no_tray_set_backend", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int NoTraySetBackend(IntPtr handle, string backend);

        [DllImport("notify_kit.dll", EntryPoint = "no_tray_show", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int NoTrayShow(IntPtr handle, string showJson, out IntPtr outJson);

        [DllImport("notify_kit.dll", EntryPoint = "no_tray_remove", CallingConvention = CallingConvention.Cdecl)]
        public static extern int NoTrayRemove(IntPtr handle);

        [DllImport("notify_kit.dll", EntryPoint = "no_last_error", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr NoLastError();

        [DllImport("notify_kit.dll", EntryPoint = "no_free_string", CallingConvention = CallingConvention.Cdecl)]
        public static extern void NoFreeString(IntPtr p);
    }
}
