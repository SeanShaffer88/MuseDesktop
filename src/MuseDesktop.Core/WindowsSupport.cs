namespace MuseDesktop.Core;

/// <summary>Windows 11 generation (build 22000+) on native x64 only; Windows-on-ARM emulation is excluded.</summary>
public static class WindowsSupport
{
    public const int MinimumBuild = 22000;

    public static bool IsSupported(bool isWindows, Version version, string osArchitecture, string processArchitecture) =>
        isWindows && version >= new Version(10, 0, MinimumBuild)
        && string.Equals(osArchitecture, "X64", StringComparison.OrdinalIgnoreCase)
        && string.Equals(processArchitecture, "X64", StringComparison.OrdinalIgnoreCase);
}
