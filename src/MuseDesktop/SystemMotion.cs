using System.Runtime.InteropServices;

namespace MuseDesktop;

/// <summary>
/// The Windows animation-effects preference (v0.10.0, S6 #55), read via
/// the documented SystemParametersInfo query SPI_GETCLIENTAREAANIMATION
/// (0x1042) — the same value the Settings "Animation effects" toggle
/// drives. Combined with the in-app preference by Core's MotionPolicy.
/// Any failure reads as "animations on", the Windows default.
/// </summary>
public static class SystemMotion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    public static bool AnimationsEnabled()
    {
        try
        {
            var enabled = 1;
            return !SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0) || enabled != 0;
        }
        catch
        {
            return true;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);
}
