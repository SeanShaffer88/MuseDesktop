using MuseDesktop.Core;
using Xunit;

namespace MuseDesktop.Core.Tests;

public class WindowsSupportTests
{
    [Theory]
    [InlineData(true, 22000, "X64", "X64", true)]
    [InlineData(true, 26100, "x64", "x64", true)]
    [InlineData(true, 19045, "X64", "X64", false)]
    [InlineData(true, 21999, "X64", "X64", false)]
    [InlineData(true, 26100, "Arm64", "X64", false)]
    [InlineData(true, 26100, "Arm64", "Arm64", false)]
    [InlineData(true, 26100, "X64", "X86", false)]
    [InlineData(false, 26100, "X64", "X64", false)]
    public void SupportedPlatformRequiresWindows11GenerationAndNativeX64(
        bool windows, int build, string osArch, string processArch, bool supported)
    {
        Assert.Equal(supported, WindowsSupport.IsSupported(windows,
            new Version(10, 0, build), osArch, processArch));
    }

    [Fact]
    public void ShippingPipelinesAndInstallerEnforceTheX64OnlyTarget()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MuseDesktop.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var root = dir.FullName;
        var ci = File.ReadAllText(Path.Combine(root, ".github/workflows/ci.yml"));
        var release = File.ReadAllText(Path.Combine(root, ".github/workflows/release.yml"));
        var installer = File.ReadAllText(Path.Combine(root, "installer/MuseDesktop.nsi"));
        var project = File.ReadAllText(Path.Combine(root, "src/MuseDesktop/MuseDesktop.csproj"));
        Assert.DoesNotContain("build-arm64:", ci);
        Assert.DoesNotContain("arch: arm64", release);
        Assert.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", project);
        Assert.Contains("<PlatformTarget>x64</PlatformTarget>", project);
        Assert.Contains("CurrentBuildNumber", installer);
        Assert.Contains("$0 < 22000", installer);
        Assert.Contains("$0 != \"AMD64\"", installer);
    }
}
