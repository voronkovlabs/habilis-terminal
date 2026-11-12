#if WINDOWS
namespace HabilisTerminal.App.Hosting;

/// <summary>Explorer verbs through the Shell.Application automation object.</summary>
internal static class Win32ShellVerbs
{
    private const int MyComputerFolder = 17; // ssfDRIVES

    /// <summary>Safely ejects a removable drive, like "Eject" in Explorer.</summary>
    public static void Eject(string root)
    {
        var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application is not available.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic? item = shell.NameSpace(MyComputerFolder)?.ParseName(root);
        if (item is null)
        {
            throw new InvalidOperationException($"Drive {root} was not found.");
        }

        item.InvokeVerb("Eject");
    }
}
#endif
