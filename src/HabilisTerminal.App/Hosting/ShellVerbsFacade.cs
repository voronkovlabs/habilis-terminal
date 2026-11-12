namespace HabilisTerminal.App.Hosting;

/// <summary>Drive verbs: Explorer's on Windows, the system's tools elsewhere (#259).</summary>
public static class ShellVerbs
{
    /// <summary>Safely ejects a removable drive, like "Eject" in Explorer.</summary>
    public static void Eject(string root) => Platform.Desktop.Current.Eject(root);
}
