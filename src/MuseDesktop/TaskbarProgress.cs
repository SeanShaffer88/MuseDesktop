using System.Runtime.InteropServices;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// Mirrors download progress on the Windows taskbar button (S2 #19)
/// via ITaskbarList3. Normal state with a value while downloads run,
/// Paused (yellow) when only paused downloads remain, Error (red) after
/// a failure until a new download starts, and NoProgress when idle.
/// Every call is failure-tolerant: a taskbar that won't cooperate is a
/// log line, never a crash or a blocked download.
/// </summary>
public sealed class TaskbarProgress
{
    private enum TbpFlag
    {
        NoProgress = 0,
        Indeterminate = 1,
        Normal = 2,
        Error = 4,
        Paused = 8,
    }

    // Only the vtable prefix through SetProgressState is declared: COM
    // slots are positional, so these land exactly where the real
    // ITaskbarList3 expects them (ITaskbarList -> ITaskbarList2 -> 3).
    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEF90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(IntPtr hwnd, TbpFlag flags);
    }

    /// <summary>CLSID_TaskbarList — activated via Type.GetTypeFromCLSID so the cast to the declared-prefix interface is a plain runtime COM cast.</summary>
    private static readonly Guid TaskbarListClsid = new("56FDF344-FD6D-11D0-958A-006097C9A090");

    private const ulong Scale = 1000;

    private readonly IntPtr _hwnd;
    private readonly DiagnosticLog _log;
    private ITaskbarList3? _taskbar;

    public TaskbarProgress(IntPtr hwnd, DiagnosticLog log)
    {
        _hwnd = hwnd;
        _log = log;
        try
        {
            var type = Type.GetTypeFromCLSID(TaskbarListClsid)
                       ?? throw new InvalidOperationException("TaskbarList COM class not registered");
            _taskbar = (ITaskbarList3)(Activator.CreateInstance(type)
                       ?? throw new InvalidOperationException("TaskbarList activation returned null"));
            _taskbar.HrInit();
        }
        catch (Exception ex)
        {
            _log.Warn($"Taskbar progress unavailable: {ex.Message}");
            _taskbar = null;
        }
    }

    /// <summary>Show determinate progress (0..1) in the Normal state.</summary>
    public void Set(double fraction) =>
        Run(t =>
        {
            t.SetProgressState(_hwnd, TbpFlag.Normal);
            t.SetProgressValue(_hwnd, (ulong)(Math.Clamp(fraction, 0, 1) * Scale), Scale);
        });

    /// <summary>Show progress without a known fraction.</summary>
    public void SetIndeterminate() =>
        Run(t => t.SetProgressState(_hwnd, TbpFlag.Indeterminate));

    /// <summary>Show the Paused (yellow) state, keeping a known value.</summary>
    public void SetPaused(double? fraction) =>
        Run(t =>
        {
            t.SetProgressState(_hwnd, TbpFlag.Paused);
            if (fraction is { } f)
                t.SetProgressValue(_hwnd, (ulong)(Math.Clamp(f, 0, 1) * Scale), Scale);
        });

    /// <summary>Show the Error (red) state after a failed download.</summary>
    public void SetError() =>
        Run(t => t.SetProgressState(_hwnd, TbpFlag.Error));

    /// <summary>Idle: no progress on the taskbar button.</summary>
    public void Clear() =>
        Run(t => t.SetProgressState(_hwnd, TbpFlag.NoProgress));

    private void Run(Action<ITaskbarList3> action)
    {
        if (_taskbar is null) return;
        try { action(_taskbar); }
        catch (Exception ex)
        {
            _log.Warn($"Taskbar progress update failed: {ex.Message}");
            _taskbar = null;
        }
    }
}
