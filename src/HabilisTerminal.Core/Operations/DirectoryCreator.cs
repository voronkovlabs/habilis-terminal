using HabilisTerminal.Core.Safety;

namespace HabilisTerminal.Core.Operations;

/// <summary>F7 (SPEC §8): nested paths at once, several names separated by <c>;</c>, existing directories are fine.</summary>
public static class DirectoryCreator
{
    /// <summary>Creates the directories and returns their full paths (the first one gets the cursor).</summary>
    /// <exception cref="ArgumentException">A name is not allowed.</exception>
    /// <exception cref="IOException">A file with that name exists, or the directory cannot be created.</exception>
    public static IReadOnlyList<string> Create(string baseDirectory, string input, bool multipleNames)
    {
        var names = (multipleNames ? input.Split(';') : [input])
            .Select(n => n.Trim().Trim('"'))
            .Where(n => n.Length > 0)
            .ToArray();
        if (names.Length == 0)
        {
            throw new ArgumentException("Enter a folder name.");
        }

        foreach (var name in names)
        {
            var relative = Path.IsPathRooted(name) ? Path.GetRelativePath(Path.GetPathRoot(name)!, name) : name;
            if (NameRules.ValidateRelativePath(relative) is { } error)
            {
                throw new ArgumentException($"{name}: {error}");
            }
        }

        var created = new List<string>();
        foreach (var name in names)
        {
            var full = Path.GetFullPath(Path.Combine(baseDirectory, name));
            DestructiveOperationGuard.Check(full);
            if (File.Exists(full))
            {
                throw new IOException($"A file named '{name}' already exists.");
            }

            Directory.CreateDirectory(full);
            created.Add(full);
        }

        return created;
    }
}
