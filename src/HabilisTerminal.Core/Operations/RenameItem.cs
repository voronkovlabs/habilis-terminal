using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Operations;

/// <summary>F2: a new name for one file or folder, in the same folder (issue #159).</summary>
public static class RenameItem
{
    /// <summary>The part of a name to select for editing: without the extension for a file, all of it for a folder or a name such as ".gitignore".</summary>
    public static int EditableLength(string name, bool isFolder)
    {
        var dot = name.LastIndexOf('.');
        return isFolder || dot <= 0 ? name.Length : dot;
    }

    /// <summary>Renames; false when the name is the same, so nothing was done. Only the case may change: that is a rename too.</summary>
    /// <exception cref="ArgumentException">The name is not allowed: the message says why.</exception>
    /// <exception cref="IOException">Something has that name already, or the file is in use.</exception>
    /// <exception cref="UnauthorizedAccessException">Not allowed to.</exception>
    public static bool Rename(string folder, string oldName, string newName)
    {
        var name = newName.Trim();
        if (name == oldName)
        {
            return false;
        }

        if (NameRules.Validate(name) is { } problem)
        {
            throw new ArgumentException(problem, nameof(newName));
        }

        var source = LongPath.ToExtended(Path.Combine(folder, oldName));
        var target = LongPath.ToExtended(Path.Combine(folder, name));
        var isFolder = Directory.Exists(source);
        if (!string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase) && Path.Exists(target))
        {
            throw new IOException($"'{name}' already exists.");
        }

        if (isFolder)
        {
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target);
        }

        return true;
    }
}
