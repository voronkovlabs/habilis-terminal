namespace TerminalCommander.Core.Tests;

/// <summary>
/// A unique temporary directory for a single test. Every test that touches the file system
/// must work inside a sandbox; the directory is deleted on dispose.
/// </summary>
public sealed class TestSandbox : IDisposable
{
    public static readonly string RootPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tcmd-tests");

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

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
