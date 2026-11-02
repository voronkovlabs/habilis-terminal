using Habilis.Aws.Tests;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.S3.Tests;

/// <summary>A folder under %TEMP%\habi-tests, deleted after the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "habi-tests", "s3-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string this[string relative] => System.IO.Path.Combine(Path, relative);

    /// <summary>Every file below the folder, relative, sorted.</summary>
    public IReadOnlyList<string> Files() =>
        Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories).Select(f => System.IO.Path.GetRelativePath(Path, f)).Order(StringComparer.Ordinal).ToList();

    public string Write(string relative, byte[] data)
    {
        var path = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class S3Test
{
    public static async Task<S3Panel> OpenAsync(FakeHost host, IS3Api api, string address = "s3:")
    {
        var plugin = new S3Plugin { Connect = _ => api };
        plugin.Initialize(host);
        return (S3Panel)(await plugin.OpenAddressAsync(address, CancellationToken.None))!;
    }

    public static async Task<S3Panel> OpenAsync(IS3Api api, string address = "s3:", S3Settings? settings = null)
    {
        var host = new FakeHost();
        (settings ?? new S3Settings()).Save(host.Settings);
        return await OpenAsync(host, api, address);
    }

    public static async Task<PanelEntry> EntryAsync(IPanelProvider panel, string name) =>
        (await Listing.AllAsync(panel)).Single(e => e.Name == name);

    public static byte[] Bytes(int size, int seed = 1)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static Func<string, Task<OverwriteChoice>> Answer(OverwriteChoice choice, List<string>? asked = null) => name =>
    {
        asked?.Add(name);
        return Task.FromResult(choice);
    };

    public static Task<OperationOutcome> DownloadAsync(S3Panel panel, IReadOnlyList<PanelEntry> entries, string folder, FakeProgress progress, Func<string, Task<OverwriteChoice>>? ask = null, CancellationToken cancellationToken = default) =>
        panel.GetFilesAsync(new TransferRequest(entries, folder, move: false, ask ?? Answer(OverwriteChoice.Overwrite)), progress, cancellationToken);
}
