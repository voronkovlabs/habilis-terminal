namespace HabilisTerminal.Core.Viewing;

public enum OfficeApp
{
    Word,
    Excel,
    PowerPoint,
}

/// <summary>Which Office application opens a file inside the window (issue #101), and which files it can create.</summary>
public static class OfficeFiles
{
    private static readonly Dictionary<string, OfficeApp> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = OfficeApp.Word,
        [".docx"] = OfficeApp.Word,
        [".docm"] = OfficeApp.Word,
        [".xls"] = OfficeApp.Excel,
        [".xlsx"] = OfficeApp.Excel,
        [".xlsm"] = OfficeApp.Excel,
        [".xlsb"] = OfficeApp.Excel,
        [".ppt"] = OfficeApp.PowerPoint,
        [".pptx"] = OfficeApp.PowerPoint,
        [".pptm"] = OfficeApp.PowerPoint,
    };

    public static OfficeApp? AppFor(string path) =>
        Extensions.TryGetValue(Path.GetExtension(path), out var app) ? app : null;

    /// <summary>Shift+F4 creates the current formats only; the older ones and macro files open but are not created.</summary>
    public static bool CanCreate(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx";

    public static string Name(OfficeApp app) => app switch
    {
        OfficeApp.Word => "Word",
        OfficeApp.Excel => "Excel",
        _ => "PowerPoint",
    };
}
