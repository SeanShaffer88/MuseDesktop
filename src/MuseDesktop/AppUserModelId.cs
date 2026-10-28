using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MuseDesktop.Core;

namespace MuseDesktop;

/// <summary>
/// The toast identity errand (R8). Windows delivers a toast only
/// for an AppUserModelID it knows, and for an unpackaged app the
/// durable way Windows learns the id is a Start Menu shortcut
/// stamped with it. So at startup (when the toast setting is on)
/// this ensures the per-user shortcut
/// "%APPDATA%\Microsoft\Windows\Start Menu\Programs\MuseDesktop.lnk"
/// exists for the running executable — creating it when missing —
/// and stamps it with <see cref="ApprovalToastMessages.AppId"/>.
///
/// Everything here is best-effort: any failure is one warning in
/// the log and the app carries on without toasts-from-shortcut
/// registration (NotifyKit reports its own side if a show then
/// fails). Nothing is shown to the user and nothing blocks.
/// </summary>
public static class AppUserModelId
{
    /// <summary>The shortcut's file name under the Start Menu Programs folder.</summary>
    public const string ShortcutFileName = "MuseDesktop.lnk";

    // PKEY_AppUserModel_ID: the property the toast platform reads
    // off a shortcut to decide which app a notification belongs to.
    private static readonly Guid AppUserModelPropertySet = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint AppUserModelPropertyId = 5;

    private const ushort VtLpwstr = 31; // VT_LPWSTR

    /// <summary>CLSID_ShellLink — activated via Type.GetTypeFromCLSID, like TaskbarProgress's taskbar list.</summary>
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    /// <summary>
    /// Ensure the Start Menu shortcut exists and carries the app
    /// identity. Runs on a background thread; never throws.
    /// </summary>
    public static void Ensure(DiagnosticLog log)
    {
        object? linkObject = null;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                log.Warn("Toast identity setup skipped: the executable path is unknown.");
                return;
            }
            var programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (string.IsNullOrEmpty(programsDir))
            {
                log.Warn("Toast identity setup skipped: the Start Menu Programs folder is unknown.");
                return;
            }
            Directory.CreateDirectory(programsDir);
            var linkPath = Path.Combine(programsDir, ShortcutFileName);
            var creating = !File.Exists(linkPath);

            var linkType = Type.GetTypeFromCLSID(ShellLinkClsid)
                           ?? throw new InvalidOperationException("ShellLink COM class not registered");
            linkObject = Activator.CreateInstance(linkType)
                         ?? throw new InvalidOperationException("ShellLink activation returned null");
            var link = (IShellLinkW)linkObject;
            var persist = (IPersistFile)linkObject;

            if (creating)
            {
                ThrowOnFailure(link.SetPath(exe), "SetPath");
                ThrowOnFailure(link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? ""), "SetWorkingDirectory");
                ThrowOnFailure(link.SetDescription("MuseDesktop"), "SetDescription");
                ThrowOnFailure(link.SetIconLocation(exe, 0), "SetIconLocation");
            }
            else
            {
                // Load what is there: saving an unloaded link would
                // overwrite the user's shortcut with an empty one.
                ThrowOnFailure(persist.Load(linkPath, 0), "Load");
            }

            StampAppId(linkObject);
            ThrowOnFailure(persist.Save(linkPath, true), "Save");
            if (creating)
                log.Info($"Start Menu shortcut created for toast notifications: {linkPath}");
        }
        catch (Exception ex)
        {
            log.Warn($"Toast identity setup failed: {ex.Message}");
        }
        finally
        {
            if (linkObject is not null)
                Marshal.FinalReleaseComObject(linkObject);
        }
    }

    /// <summary>Set PKEY_AppUserModel_ID on the link through its property store and commit it (persisting happens in Save).</summary>
    private static void StampAppId(object linkObject)
    {
        var store = (IPropertyStore)linkObject;
        var key = new PropertyKey { FormatId = AppUserModelPropertySet, PropertyId = AppUserModelPropertyId };
        var value = new PropVariant { Vt = VtLpwstr, Value = Marshal.StringToCoTaskMemUni(ApprovalToastMessages.AppId) };
        try
        {
            ThrowOnFailure(store.SetValue(ref key, ref value), "SetValue(AppUserModelID)");
            ThrowOnFailure(store.Commit(), "Commit(AppUserModelID)");
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.Value);
        }
    }

    private static void ThrowOnFailure(int hr, string step)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{step} failed (HRESULT 0x{hr:X8})");
    }

    // Only the vtable prefix through SetPath is declared: COM slots
    // are positional, so these land exactly where the real
    // IShellLinkW expects them (the TaskbarProgress convention).
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath(StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription(StringBuilder pszName, int cch);
        [PreserveSig] int SetDescription(string pszName);
        [PreserveSig] int GetWorkingDirectory(StringBuilder pszDir, int cch);
        [PreserveSig] int SetWorkingDirectory(string pszDir);
        [PreserveSig] int GetArguments(StringBuilder pszArgs, int cch);
        [PreserveSig] int SetArguments(string pszArgs);
        [PreserveSig] int GetHotkey(out ushort pwHotkey);
        [PreserveSig] int SetHotkey(ushort wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation(StringBuilder pszIconPath, int cch, out int piIcon);
        [PreserveSig] int SetIconLocation(string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath(string pszPath, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath(string pszFile);
    }

    // IPersistFile in full (it extends IPersist's single slot).
    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load(string pszFileName, uint dwMode);
        [PreserveSig] int Save(string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        [PreserveSig] int SaveCompleted(string pszFileName);
        [PreserveSig] int GetCurFile(out IntPtr ppszFileName);
    }

    // IPropertyStore in full.
    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pKey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // PROPVARIANT: only the string arm is used; the union is sized
    // to the full 16 bytes so the struct matches the native layout.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public IntPtr Value;
    }
}
