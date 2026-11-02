using System.Formats.Tar;
using System.IO.Compression;
using HabilisTerminal.Core.Archives;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.Core.Tests;

public class ArchiveTests
{
    private static readonly DateTime Stamp = new(2025, 3, 4, 5, 6, 8, DateTimeKind.Utc);

    [Fact]
    public void The_bundled_7z_dll_loads_and_reads_the_common_formats()
    {
        var library = Assert.IsType<SevenZipLibrary>(SevenZipLibrary.Default);
        var names = library.Formats.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Superset(new HashSet<string>(["zip", "7z", "Rar", "tar", "gzip", "Iso"], StringComparer.OrdinalIgnoreCase), names);
        Assert.Contains("zip", library.Extensions);
        Assert.True(ArchiveProvider.HasArchiveExtension("data.ZIP"));
        Assert.True(ArchiveProvider.HasArchiveExtension("logs.tar.gz"));
        Assert.False(ArchiveProvider.HasArchiveExtension("report.docx")); // Ctrl+PgDn only
        Assert.False(ArchiveProvider.HasArchiveExtension("notes.txt"));
    }

    [Fact]
    public async Task A_zip_lists_as_folders_including_folders_only_implied_by_paths()
    {
        using var sandbox = new TestSandbox();
        var zip = MakeZip(sandbox, "a.zip", ("readme.txt", "hello"), ("docs/guide/intro.md", "# intro"), ("docs/notes.txt", "n"));
        await using var provider = Open(zip);

        Assert.Equal(zip, provider.Location);
        Assert.Equal(["docs/", "readme.txt"], await Names(provider));

        Assert.True(await provider.EnterAsync(new PanelEntry("docs", IsFolder: true), CancellationToken.None));
        Assert.Equal(zip + "\\docs", provider.Location);
        Assert.Equal(["guide/", "notes.txt"], await Names(provider));

        Assert.True(await provider.GoUpAsync(CancellationToken.None));
        Assert.False(await provider.GoUpAsync(CancellationToken.None)); // at the top: the host leaves the archive
        Assert.Contains(provider.Info, line => line.Label == "Files" && line.Value == "3");
    }

    [Fact]
    public async Task F5_copies_files_and_folders_out_with_their_times_and_asks_before_overwriting()
    {
        using var sandbox = new TestSandbox();
        var zip = MakeZip(sandbox, "a.zip", ("readme.txt", "hello"), ("docs/guide/intro.md", "# intro"), ("docs/notes.txt", "n"));
        var target = Directory.CreateDirectory(Path.Combine(sandbox.Path, "out")).FullName;
        await using var provider = Open(zip);

        var progress = new CountingProgress();
        var outcome = await provider.GetFilesAsync(
            new TransferRequest([new PanelEntry("docs", IsFolder: true), new PanelEntry("readme.txt")], target, move: false, _ => throw new InvalidOperationException("no conflicts expected")),
            progress,
            CancellationToken.None);

        Assert.Equal(new OperationOutcome(3, 0, 0), outcome);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "readme.txt")));
        Assert.Equal("# intro", File.ReadAllText(Path.Combine(target, "docs", "guide", "intro.md")));
        // Zip stores local times, so the expected time is the one 7-Zip lists, not the stamp itself.
        using (var listed = SevenZipLibrary.Default!.Open(zip, anyFormat: false))
        {
            var notes = listed!.Items.Single(i => i.Path == "docs\\notes.txt");
            Assert.Equal(notes.ModifiedUtc, File.GetLastWriteTimeUtc(Path.Combine(target, "docs", "notes.txt")));
        }

        Assert.Equal((3, 3, 5L + 7 + 1), (progress.Items, progress.Completed, progress.Bytes));

        File.WriteAllText(Path.Combine(target, "readme.txt"), "mine");
        var asked = new List<string>();
        outcome = await provider.GetFilesAsync(
            new TransferRequest([new PanelEntry("readme.txt")], target, move: false, name => { asked.Add(name); return Task.FromResult(OverwriteChoice.Skip); }),
            new CountingProgress(),
            CancellationToken.None);

        Assert.Equal(new OperationOutcome(0, 1, 0), outcome);
        Assert.Equal([Path.Combine(target, "readme.txt")], asked);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "readme.txt")));
    }

    [Fact]
    public async Task An_archive_inside_opens_in_place_and_up_returns_to_its_folder()
    {
        using var sandbox = new TestSandbox();
        var tar = Path.Combine(sandbox.Path, "inner.tar");
        await using (var stream = File.Create(tar))
        await using (var writer = new TarWriter(stream))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "src/main.c") { DataStream = new MemoryStream("int main;"u8.ToArray()) };
            await writer.WriteEntryAsync(entry);
        }

        var zip = Path.Combine(sandbox.Path, "outer.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(tar, "pack/inner.tar");
        }

        await using var provider = Open(zip);
        await provider.EnterAsync(new PanelEntry("pack", IsFolder: true), CancellationToken.None);
        Assert.True(await provider.EnterAsync(new PanelEntry("inner.tar"), CancellationToken.None));
        Assert.Equal(zip + "\\pack\\inner.tar", provider.Location);
        Assert.Equal(["src/"], await Names(provider));

        Assert.True(await provider.GoUpAsync(CancellationToken.None));
        Assert.Equal(zip + "\\pack", provider.Location);
        Assert.Equal(["inner.tar"], await Names(provider));
    }

    [Fact]
    public async Task Folder_history_reopens_a_location_inside_an_archive_and_ctrl_pgdn_ignores_the_extension()
    {
        using var sandbox = new TestSandbox();
        var zip = MakeZip(sandbox, "a.zip", ("docs/notes.txt", "n"));
        await using (var provider = ArchiveProvider.TryOpenLocation(zip + "\\docs", _ => null))
        {
            Assert.NotNull(provider);
            Assert.Equal(zip + "\\docs", provider.Location);
        }

        var renamed = Path.Combine(sandbox.Path, "a.dat");
        File.Copy(zip, renamed);
        Assert.Null(ArchiveProvider.TryOpen(renamed, anyFormat: false, _ => null));
        await using var bySignature = ArchiveProvider.TryOpen(renamed, anyFormat: true, _ => null);
        Assert.Equal("zip", bySignature?.Title.Split(':')[0]);
        Assert.Null(ArchiveProvider.TryOpenLocation(Path.Combine(sandbox.Path, "missing.zip", "x"), _ => null));
    }

    [Fact]
    public async Task F5_into_a_zip_adds_files_and_folders_and_replaces_a_file_when_allowed()
    {
        using var sandbox = new TestSandbox();
        var zip = MakeZip(sandbox, "a.zip", ("readme.txt", "old"), ("docs/notes.txt", "n"));
        var local = Directory.CreateDirectory(Path.Combine(sandbox.Path, "local")).FullName;
        File.WriteAllText(Path.Combine(local, "readme.txt"), "new readme");
        Directory.CreateDirectory(Path.Combine(local, "pics", "empty"));
        File.WriteAllText(Path.Combine(local, "pics", "cat.txt"), "meow");
        await using var provider = Open(zip);
        Assert.True(provider.CanWrite);

        var asked = new List<string>();
        var outcome = await provider.PutFilesAsync(
            [Path.Combine(local, "readme.txt"), Path.Combine(local, "pics")],
            name => { asked.Add(name); return Task.FromResult(OverwriteChoice.Overwrite); },
            new CountingProgress(),
            CancellationToken.None);

        Assert.Equal(new OperationOutcome(2, 0, 0), outcome);
        Assert.Equal(["readme.txt"], asked);
        Assert.Equal(["docs/", "pics/", "readme.txt"], await Names(provider));
        await provider.EnterAsync(new PanelEntry("pics", IsFolder: true), CancellationToken.None);
        Assert.Equal(["cat.txt", "empty/"], await Names(provider));
        await provider.GoUpAsync(CancellationToken.None);

        // The zip itself was rewritten: another reader sees the same, and the replaced file's new content.
        using var check = ZipFile.OpenRead(zip);
        Assert.Equal("new readme", new StreamReader(check.GetEntry("readme.txt")!.Open()).ReadToEnd());
        Assert.Equal("n", new StreamReader(check.GetEntry("docs/notes.txt")!.Open()).ReadToEnd());
        Assert.Equal(1, check.Entries.Count(e => e.FullName == "readme.txt"));
        Assert.Empty(Directory.GetFiles(sandbox.Path, "~*.tmp"));
    }

    [Fact]
    public async Task F8_F7_and_F6_change_a_tar_and_an_archive_inside_another_stays_read_only()
    {
        using var sandbox = new TestSandbox();
        var tar = Path.Combine(sandbox.Path, "a.tar");
        await using (var stream = File.Create(tar))
        await using (var writer = new TarWriter(stream))
        {
            foreach (var (name, text) in new[] { ("keep.txt", "k"), ("old/one.txt", "1"), ("old/two.txt", "2"), ("move.txt", "m") })
            {
                await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)) });
            }
        }

        await using var provider = Open(tar);
        await provider.DeleteAsync([new PanelEntry("old", IsFolder: true)], new CountingProgress(), CancellationToken.None);
        Assert.Equal(["keep.txt", "move.txt"], await Names(provider));

        await provider.MakeFolderAsync("new", CancellationToken.None);
        await Assert.ThrowsAsync<PluginException>(() => provider.MakeFolderAsync("new", CancellationToken.None));
        Assert.Equal(["keep.txt", "move.txt", "new/"], await Names(provider));

        var target = Directory.CreateDirectory(Path.Combine(sandbox.Path, "out")).FullName;
        var outcome = await provider.GetFilesAsync(new TransferRequest([new PanelEntry("move.txt")], target, move: true, _ => Task.FromResult(OverwriteChoice.Overwrite)), new CountingProgress(), CancellationToken.None);
        Assert.Equal(new OperationOutcome(1, 0, 0), outcome);
        Assert.Equal("m", File.ReadAllText(Path.Combine(target, "move.txt")));
        Assert.Equal(["keep.txt", "new/"], await Names(provider));

        // A tar inside a zip opens, but changing it would mean rewriting the outer archive too.
        var zip = Path.Combine(sandbox.Path, "outer.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(tar, "a.tar");
        }

        await using var outer = Open(zip);
        await outer.EnterAsync(new PanelEntry("a.tar"), CancellationToken.None);
        Assert.False(outer.CanWrite);
        await Assert.ThrowsAsync<PluginException>(() => outer.MakeFolderAsync("x", CancellationToken.None));
    }

    private static ArchiveProvider Open(string path) =>
        ArchiveProvider.TryOpen(path, anyFormat: false, _ => null) ?? throw new InvalidOperationException($"{path} did not open.");

    private static async Task<List<string>> Names(ArchiveProvider provider)
    {
        var names = new List<string>();
        await foreach (var batch in provider.ListAsync(CancellationToken.None))
        {
            names.AddRange(batch.Select(e => e.IsFolder ? e.Name + "/" : e.Name));
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static string MakeZip(TestSandbox sandbox, string name, params (string Path, string Text)[] files)
    {
        var path = Path.Combine(sandbox.Path, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, text) in files)
        {
            var entry = archive.CreateEntry(entryPath);
            entry.LastWriteTime = new DateTimeOffset(Stamp);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(text);
        }

        return path;
    }

    private sealed class CountingProgress : IOperationProgress
    {
        public int Items { get; private set; }

        public int Completed { get; private set; }

        public long Bytes { get; private set; }

        public void SetTotals(long? items, long? bytes) => Items = (int)(items ?? 0);

        public void Begin(string item)
        {
        }

        public void AddBytes(long bytes) => Bytes += bytes;

        public void CompleteItem() => Completed++;
    }
}
