namespace HabilisTerminal.Core.FileSystem;

/// <summary>
/// Folders the Finder shows as one file (issue #274): programs, documents of Pages or Keynote, libraries. On a Mac Enter
/// opens them, as a double click in the Finder does; Ctrl+PgDn still goes inside.
/// </summary>
public static class MacPackages
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".app", ".bundle", ".plugin", ".kext", ".prefPane", ".qlgenerator", ".mdimporter", ".saver", ".appex", ".xpc",
        ".pkg", ".mpkg", ".rtfd", ".pages", ".numbers", ".key", ".photoslibrary", ".musiclibrary", ".tvlibrary",
        ".imovielibrary", ".fcpbundle", ".logicx", ".band", ".playground", ".xcodeproj", ".xcworkspace", ".xcarchive",
        ".sparsebundle", ".scptd", ".wdgt", ".lpdf",
    };

    public static bool IsPackage(string name) => Extensions.Contains(Path.GetExtension(name));
}
