namespace HabilisTerminal.Core.Benchmarks;

/// <summary>
/// A generated directory with many empty files under %TEMP%\habi-bench. Kept between runs on purpose
/// (creating 100k files takes a while); remove it with <c>--clean</c>.
/// </summary>
internal sealed class LargeDirectory
{
    private static readonly string[] Extensions = ["txt", "cs", "log", "json", "png", "dll", "md", "xml"];

    public LargeDirectory(int fileCount, int directoryCount = 200)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "habi-bench", $"dir-{fileCount}");
        var marker = System.IO.Path.Combine(Path, ".complete");
        if (File.Exists(marker))
        {
            return;
        }

        Directory.CreateDirectory(Path);
        for (var i = 0; i < directoryCount; i++)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Path, $"folder {i}"));
        }

        Parallel.For(0, fileCount, i =>
        {
            var name = $"file_{i % 997}_{i}.{Extensions[i % Extensions.Length]}";
            using var _ = File.Create(System.IO.Path.Combine(Path, name));
        });

        File.WriteAllText(marker, string.Empty);
    }

    public string Path { get; }

    public static void Clean()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "habi-bench");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
