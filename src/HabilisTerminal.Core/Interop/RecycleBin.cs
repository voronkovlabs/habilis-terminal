using HabilisTerminal.Core.Platform;

namespace HabilisTerminal.Core.Interop;

/// <summary>
/// Moving items to the Recycle Bin (the trash elsewhere) without shell UI, through <see cref="Platforms.Current"/>.
/// The Windows shell would silently delete permanently when recycling is impossible, so
/// <see cref="CannotRecycleReason"/> is checked first and the user is asked (SPEC §8).
/// </summary>
public static class RecycleBin
{
    /// <summary>The longest path the shell's file operation accepts.</summary>
    public const int MaxShellPath = 259;

    /// <summary>Why the item cannot go to the Recycle Bin, or null when it can.</summary>
    public static string? CannotRecycleReason(string path) => Platforms.Current.RecycleBin.CannotRecycleReason(path);

    /// <summary>Moves one item to the Recycle Bin. Call only after <see cref="CannotRecycleReason"/> returned null.</summary>
    /// <exception cref="IOException">The system reported an error.</exception>
    public static void Recycle(string path) => Platforms.Current.RecycleBin.Recycle(path);
}
