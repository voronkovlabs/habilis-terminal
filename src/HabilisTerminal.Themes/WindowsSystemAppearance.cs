using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HabilisTerminal.Themes;

/// <summary>Windows light/dark mode, high contrast and accent colour, from the registry and user32.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemAppearance : ISystemAppearance
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    public bool AppsUseLightTheme =>
        Registry.GetValue($@"HKEY_CURRENT_USER\{PersonalizeKey}", "AppsUseLightTheme", 0) is int value && value != 0;

    /// <summary>The accent colour, or null when it cannot be read.</summary>
    public ThemeColor? AccentColor
    {
        get
        {
            // AccentColor is stored as 0xAABBGGRR.
            if (Registry.GetValue($@"HKEY_CURRENT_USER\{DwmKey}", "AccentColor", null) is int abgr)
            {
                var value = unchecked((uint)abgr);
                return new ThemeColor((byte)value, (byte)(value >> 8), (byte)(value >> 16));
            }

            return null;
        }
    }

    /// <summary>Windows high contrast is on (WPF's SystemParameters.HighContrast, which Avalonia has no twin of; issue #253).</summary>
    public bool IsHighContrast()
    {
        var info = new HighContrastInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<HighContrastInfo>() };
        return SystemParametersInfo(SPI_GETHIGHCONTRAST, info.Size, ref info, 0) && (info.Flags & HCF_HIGHCONTRASTON) != 0;
    }

    private const uint SPI_GETHIGHCONTRAST = 0x0042;
    private const uint HCF_HIGHCONTRASTON = 0x0001;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr DefaultScheme;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrastInfo info, uint winIni);
}
