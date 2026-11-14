using System.Formats.Tar;
using System.IO.Compression;
using HabilisTerminal.Plugins;

namespace Habilis.Archives.Tests;

/// <summary>Archives on a Mac through the system's libarchive (issue #281); run on the macOS runner.</summary>
public sealed class LibArchiveTests
{
    private static readonly DateTime Stamp = new(2025, 3, 4, 5, 6, 8, DateTimeKind.Utc);

    [Theory]
    [InlineData("docs/readme.txt", @"docs\readme.txt")]
    [InlineData("./docs/./a.txt", @"docs\a.txt")]
    [InlineData("/abs/x", @"abs\x")]
    [InlineData("../../etc/passwd", @"etc\passwd")]
    [InlineData("dir/", "dir")]
    [InlineData("", "")]
    public void Names_are_the_panels_and_never_climb_out(string stored, string expected) =>
        Assert.Equal(expected, LibArchiveEngine.CleanPath(stored));

    private static IArchiveEngine Engine()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "the macOS libarchive");
        return Assert.IsType<LibArchiveEngine>(LibArchiveEngine.TryLoad());
    }

    [Fact]
    public void A_zip_lists_its_files_and_folders_and_opens_only_by_extension_unless_asked()
    {
        var engine = Engine();
        using var sandbox = new TestSandbox();
        var zip = MakeZip(sandbox, "a.zip", ("readme.txt", "hello"), ("docs/guide/intro.md", "# intro"));
        var renamed = Path.Combine(sandbox.Path, "a.dat");
        File.Copy(zip, renamed);

        using var archive = engine.Open(zip, anyFormat: false)!;
        Assert.Contains("ZIP", archive.Format.Name, StringComparison.OrdinalIgnoreCase);
        Assert.False(archive.Format.CanWrite);
        Assert.Equal(["docs\\guide\\intro.md", "readme.txt"], archive.Items.Where(i => !i.IsFolder).Select(i => i.Path).Order());
        Assert.Equal(5, archive.Items.Single(i => i.Path == "readme.txt").Size);

        Assert.Null(engine.Open(renamed, anyFormat: false));
        using var bySignature = engine.Open(renamed, anyFormat: true);
        Assert.NotNull(bySignature);
        Assert.Null(engine.Open(Write(sandbox, "plain.txt", "not an archive"), anyFormat: true));
    }

    [Fact]
    public async Task Files_and_folders_copy_out_of_a_tar_gz_with_their_dates()
    {
        Engine();
        using var sandbox = new TestSandbox();
        var tgz = Path.Combine(sandbox.Path, "src.tar.gz");
        await using (var file = File.Create(tgz))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var writer = new TarWriter(gzip))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, "src/") { ModificationTime = Stamp });
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "src/main.c") { DataStream = new MemoryStream("int main;"u8.ToArray()), ModificationTime = Stamp });
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "README") { DataStream = new MemoryStream("read me"u8.ToArray()), ModificationTime = Stamp });
        }

        await using var provider = ArchiveProvider.TryOpen(tgz, anyFormat: false, _ => null)!;
        Assert.True(provider.CanWrite);
        var target = Directory.CreateDirectory(Path.Combine(sandbox.Path, "out")).FullName;
        var outcome = await provider.GetFilesAsync(
            new TransferRequest([new PanelEntry("src", IsFolder: true), new PanelEntry("README")], target, move: false, _ => throw new InvalidOperationException("no conflicts expected")),
            new ArchiveTests.CountingProgress(),
            CancellationToken.None);

        Assert.Equal(0, outcome.Failed);
        Assert.Equal("int main;", File.ReadAllText(Path.Combine(target, "src", "main.c")));
        Assert.Equal("read me", File.ReadAllText(Path.Combine(target, "README")));
        Assert.Equal(Stamp, File.GetLastWriteTimeUtc(Path.Combine(target, "README")));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(target, "*", SearchOption.AllDirectories), p => Path.GetFileName(p).Contains('\\', StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_place_inside_reads_back_with_the_systems_separator()
    {
        Engine();
        using var sandbox = new TestSandbox();
        var zip = MakeZip(sandbox, "a.zip", ("docs/sub/a.txt", "x"));

        await using var provider = ArchiveProvider.TryOpen(zip, @"docs\sub", anyFormat: false, _ => null)!;

        Assert.Equal(zip + "/docs/sub", provider.Location);
    }

    [Fact]
    public async Task A_lone_gz_shows_the_file_inside()
    {
        var engine = Engine();
        using var sandbox = new TestSandbox();
        var gz = Path.Combine(sandbox.Path, "server.log.gz");
        await using (var file = File.Create(gz))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            await gzip.WriteAsync("line one\nline two\n"u8.ToArray());
        }

        using var archive = engine.Open(gz, anyFormat: false)!;
        var item = Assert.Single(archive.Items);
        Assert.Equal("server.log", item.Path);
        Assert.Equal("gzip", archive.Format.Name);

        var target = Path.Combine(sandbox.Path, "server.log");
        Assert.Empty(archive.Extract(new Dictionary<uint, string> { [0] = target }, _ => { }, _ => { }, null, CancellationToken.None));
        Assert.Equal("line one\nline two\n", File.ReadAllText(target));
    }

    [Fact]
    public async Task An_encrypted_zip_asks_again_after_a_wrong_password()
    {
        Engine();
        using var sandbox = new TestSandbox();
        var zip = ArchiveTests.MakeEncryptedZip(sandbox, "sesame", "secret.txt", "the secret");
        var target = Directory.CreateDirectory(Path.Combine(sandbox.Path, "out")).FullName;
        var asked = new List<string>();
        var answers = new Queue<string?>(["wrong", "sesame"]);
        await using var provider = ArchiveProvider.TryOpen(zip, anyFormat: false, prompt =>
        {
            asked.Add(prompt);
            return answers.Dequeue();
        })!;

        var outcome = await provider.GetFilesAsync(ArchiveTests.CopyOut(target, "secret.txt"), new ArchiveTests.CountingProgress(), CancellationToken.None);

        Assert.Equal(new OperationOutcome(1, 0, 0), outcome);
        Assert.Equal("the secret", File.ReadAllText(Path.Combine(target, "secret.txt")));
        Assert.Equal(2, asked.Count);
        Assert.Contains("Wrong password", asked[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_7z_with_encrypted_names_says_why_it_does_not_open()
    {
        Engine();
        using var sandbox = new TestSandbox();
        var path = Path.Combine(sandbox.Path, "names.7z");
        File.WriteAllBytes(path, Convert.FromBase64String(ArchiveTests.EncryptedNames7z));

        // libarchive reads no encrypted 7z; the message is libarchive's own, not "not an archive".
        var error = Assert.ThrowsAny<IOException>(() => ArchiveProvider.TryOpen(path, anyFormat: false, _ => "sesame"));
        Assert.StartsWith("names.7z: ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x50000, 0, "zip", null)]
    [InlineData(0xE0000, 0, "7zip", null)]
    [InlineData(0x30002, 0, "paxr", null)]
    [InlineData(0x30005, 1, "paxr", "gzip")]
    [InlineData(0x30001, 2, "paxr", "bzip2")]
    [InlineData(0x30001, 6, "paxr", "xz")]
    [InlineData(0x30001, 14, null, null)]
    [InlineData(0x50000, 1, null, null)]
    [InlineData(0xD0000, 0, null, null)]
    [InlineData(0x40000, 0, null, null)]
    public void Zip_7z_and_tar_with_the_usual_compressions_are_written(int format, int filter, string? writer, string? compression) =>
        Assert.Equal((writer, compression), LibArchiveEngine.Writer(format, filter));

    [Fact]
    public async Task F5_into_a_zip_adds_files_and_folders_and_replaces_a_file_when_allowed()
    {
        Engine();
        using var sandbox = new TestSandbox();
        var zip = MakeZip(sandbox, "a.zip", ("readme.txt", "old"), ("docs/notes.txt", "n"));
        var local = Directory.CreateDirectory(Path.Combine(sandbox.Path, "local")).FullName;
        File.WriteAllText(Path.Combine(local, "readme.txt"), "new readme");
        Directory.CreateDirectory(Path.Combine(local, "pics", "empty"));
        File.WriteAllText(Path.Combine(local, "pics", "cat.txt"), "meow");
        await using var provider = ArchiveProvider.TryOpen(zip, anyFormat: false, _ => null)!;
        Assert.True(provider.CanWrite);

        var asked = new List<string>();
        var outcome = await provider.PutFilesAsync(
            [Path.Combine(local, "readme.txt"), Path.Combine(local, "pics")],
            name => { asked.Add(name); return Task.FromResult(OverwriteChoice.Overwrite); },
            new ArchiveTests.CountingProgress(),
            CancellationToken.None);

        Assert.Equal(new OperationOutcome(2, 0, 0), outcome);
        Assert.Equal(["readme.txt"], asked);
        Assert.Equal(["docs/", "pics/", "readme.txt"], await ArchiveTests.Names(provider));
        await provider.EnterAsync(new PanelEntry("pics", IsFolder: true), CancellationToken.None);
        Assert.Equal(["cat.txt", "empty/"], await ArchiveTests.Names(provider));
        await provider.GoUpAsync(CancellationToken.None);

        // Another reader (.NET's) sees the rewritten zip.
        using var check = ZipFile.OpenRead(zip);
        Assert.Equal("new readme", new StreamReader(check.GetEntry("readme.txt")!.Open()).ReadToEnd());
        Assert.Equal("n", new StreamReader(check.GetEntry("docs/notes.txt")!.Open()).ReadToEnd());
        Assert.Equal("meow", new StreamReader(check.GetEntry("pics/cat.txt")!.Open()).ReadToEnd());
        Assert.Equal(1, check.Entries.Count(e => e.FullName == "readme.txt"));
        Assert.Empty(Directory.GetFiles(sandbox.Path, "~*.tmp"));
    }

    [Fact]
    public async Task F8_and_F7_change_a_tar_gz_that_tar_still_reads()
    {
        Engine();
        using var sandbox = new TestSandbox();
        var tgz = Path.Combine(sandbox.Path, "a.tgz");
        await using (var file = File.Create(tgz))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var writer = new TarWriter(gzip))
        {
            foreach (var (name, text) in new[] { ("keep.txt", "k"), ("old/one.txt", "1"), ("old/two.txt", "2") })
            {
                await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), ModificationTime = Stamp });
            }
        }

        await using var provider = ArchiveProvider.TryOpen(tgz, anyFormat: false, _ => null)!;
        await provider.DeleteAsync([new PanelEntry("old", IsFolder: true)], new ArchiveTests.CountingProgress(), CancellationToken.None);
        Assert.Equal(["keep.txt"], await ArchiveTests.Names(provider));

        await provider.MakeFolderAsync("new", CancellationToken.None);
        await Assert.ThrowsAsync<PluginException>(() => provider.MakeFolderAsync("new", CancellationToken.None));
        Assert.Equal(["keep.txt", "new/"], await ArchiveTests.Names(provider));

        // Still a gzip'd tar, with the kept file's content and date.
        await using var stream = new GZipStream(File.OpenRead(tgz), CompressionMode.Decompress);
        using var reader = new TarReader(stream);
        var entries = new Dictionary<string, (string Text, DateTimeOffset Modified)>();
        while (reader.GetNextEntry() is { } entry)
        {
            entries[entry.Name] = (entry.DataStream is { } data ? new StreamReader(data).ReadToEnd() : string.Empty, entry.ModificationTime);
        }

        Assert.Equal(["keep.txt", "new/"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("k", entries["keep.txt"].Text);
        Assert.Equal(Stamp, entries["keep.txt"].Modified.UtcDateTime);
    }

    [Fact]
    public async Task A_7z_is_written_and_reads_back()
    {
        Engine();
        using var sandbox = new TestSandbox();
        var sevenZip = Path.Combine(sandbox.Path, "a.7z");
        File.WriteAllBytes(sevenZip, Convert.FromBase64String(Empty7z));
        await using var provider = ArchiveProvider.TryOpen(sevenZip, anyFormat: false, _ => null)!;
        Assert.True(provider.CanWrite);

        var local = Path.Combine(sandbox.Path, "note.txt");
        File.WriteAllText(local, "seven");
        await provider.PutFilesAsync([local], _ => Task.FromResult(OverwriteChoice.Overwrite), new ArchiveTests.CountingProgress(), CancellationToken.None);
        await provider.MakeFolderAsync("dir", CancellationToken.None);
        Assert.Equal(["dir/", "note.txt"], await ArchiveTests.Names(provider));

        var target = Directory.CreateDirectory(Path.Combine(sandbox.Path, "out")).FullName;
        await provider.GetFilesAsync(ArchiveTests.CopyOut(target, "note.txt"), new ArchiveTests.CountingProgress(), CancellationToken.None);
        Assert.Equal("seven", File.ReadAllText(Path.Combine(target, "note.txt")));
    }

    [Fact]
    public async Task An_encrypted_zip_is_only_read()
    {
        Engine();
        using var sandbox = new TestSandbox();
        var zip = ArchiveTests.MakeEncryptedZip(sandbox, "sesame", "secret.txt", "the secret");
        await using var provider = ArchiveProvider.TryOpen(zip, anyFormat: false, _ => "sesame")!;

        Assert.False(provider.CanWrite);
        await Assert.ThrowsAsync<PluginException>(() => provider.MakeFolderAsync("x", CancellationToken.None));
    }

    /// <summary>An empty 7z: the signature header, pointing at no next header (32 bytes).</summary>
    private const string Empty7z = "N3q8ryccAASNm9UPAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private static string Write(TestSandbox sandbox, string name, string content)
    {
        var path = Path.Combine(sandbox.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string MakeZip(TestSandbox sandbox, string name, params (string Path, string Content)[] files)
    {
        var path = Path.Combine(sandbox.Path, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (inner, content) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(inner).Open());
            writer.Write(content);
        }

        return path;
    }
}
