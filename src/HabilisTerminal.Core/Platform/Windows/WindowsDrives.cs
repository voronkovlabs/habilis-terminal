using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HabilisTerminal.Core.Platform.Windows;

/// <summary>Known folders through the shell and mapped network drives through WNet.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDrives : IDriveSource
{
    private const int ConnectUpdateProfile = 0x1;

    public bool CanDisconnect => true;

    public string? DownloadsFolder() => KnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"));

    public void DisconnectNetworkDrive(string root)
    {
        var name = root.TrimEnd('\\');
        var result = WNetCancelConnection2(name, ConnectUpdateProfile, true);
        if (result != 0)
        {
            throw new IOException(new System.ComponentModel.Win32Exception(result).Message);
        }
    }

    private static string? KnownFolderPath(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pointer) != 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string lpName, int dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fForce);
}
