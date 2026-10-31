namespace HabilisTerminal.Core.Tests;

/// <summary>
/// A unique temporary directory for a single test. Every test that touches the file system
/// must work inside a sandbox; the directory is deleted on dispose.
/// </summary>
public sealed class TestSandbox : IDisposable
{
    public static readonly string RootPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "habi-tests");

    public TestSandbox()
    {
        Path = System.IO.Path.Combine(RootPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public bool Contains(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        return full.StartsWith(Path + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => DeleteTree(new DirectoryInfo(Path));

    /// <summary>
    /// Deletes a tree without entering links. <c>Directory.Delete(path, recursive: true)</c> fails with
    /// "access denied" when the tree contains a junction (.NET 10), so links are removed one by one.
    /// </summary>
    private static void DeleteTree(DirectoryInfo directory)
    {
        if (!directory.Exists)
        {
            return;
        }

        foreach (var item in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            item.Attributes &= ~FileAttributes.ReadOnly;
            if (item is DirectoryInfo child && (child.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                DeleteTree(child);
            }
            else
            {
                // Files, file links, and directory links / junctions (deletes the link, never the target).
                item.Delete();
            }
        }

        directory.Delete();
    }
}
