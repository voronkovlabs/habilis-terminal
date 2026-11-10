using System.Formats.Tar;
using System.IO.Compression;
using HabilisTerminal.Plugins;

namespace Habilis.Archives.Tests;

public class ArchiveTests
{
    private static readonly DateTime Stamp = new(2025, 3, 4, 5, 6, 8, DateTimeKind.Utc);

    /// <summary>A 7z with encrypted names, password "sesame", holding inside.txt with "hello". Made with 7-Zip.</summary>
    private const string EncryptedNames7z = "N3q8ryccAASNCHnvkAAAAAAAAAAvAAAAAAAAAIR7NrnJfgVuMOXUEEBQ/WnSFj3zafZta04OZ6HpWR2zZCx9XegfPzNvqya1WXPY7tHbA6nKgBt52xmO3UYf+khKjYRUb8BUJNo2T8rDknPt2UvW26eZDFn1OuY+/NR0GHUa5H7NS9dAo8qJM/XYQBqKdbVdcGe567ZCycqz87ojE1NWuBoCaZlJXKx5CZGAbWpA4RMXBhABCYCAAAcLAQABJAbxBwESUw88gm4v0zRo6C9e3uuZcj02DHIKAYkjHO4AAA==";

    /// <summary>A zip with the older cipher: the plugin writes no passwords, and .NET's zip neither.</summary>
    private static string MakeEncryptedZip(TestSandbox sandbox, string password, string name, string content)
    {
        uint[] table = Enumerable.Range(0, 256).Select(n =>
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            return c;
        }).ToArray();
        uint Step(uint crc, byte b) => table[(crc ^ b) & 0xFF] ^ (crc >> 8);

        uint k0 = 0x12345678, k1 = 0x23456789, k2 = 0x34567890;
        void Update(byte b)
        {
            k0 = Step(k0, b);
            k1 = ((k1 + (k0 & 0xFF)) * 134775813) + 1;
            k2 = Step(k2, (byte)(k1 >> 24));
        }

        byte Encrypt(byte b)
        {
            var t = (ushort)(k2 | 2);
            var result = (byte)(b ^ (byte)((t * (t ^ 1)) >> 8));
            Update(b);
            return result;
        }

        foreach (var b in System.Text.Encoding.UTF8.GetBytes(password))
        {
            Update(b);
        }

        var plain = System.Text.Encoding.UTF8.GetBytes(content);
        var crc = plain.Aggregate(0xFFFFFFFF, Step) ^ 0xFFFFFFFF;
        byte[] header = [.. Enumerable.Range(0, 11).Select(i => (byte)i), (byte)(crc >> 24)];
        var data = header.Concat(plain).Select(Encrypt).ToArray();
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var path = Path.Combine(sandbox.Path, "locked.zip");
        using var writer = new BinaryWriter(File.Create(path));

        void Common()
        {
            writer.Write((ushort)20);
            writer.Write((ushort)1);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0x5A21);
            writer.Write(crc);
            writer.Write(data.Length);
            writer.Write(plain.Length);
            writer.Write((ushort)nameBytes.Length);
            writer.Write((ushort)0);
        }

        writer.Write(0x04034B50);
        Common();
        writer.Write(nameBytes);
        writer.Write(data);
        var central = (int)writer.BaseStream.Position;
        writer.Write(0x02014B50);
        writer.Write((ushort)20);
        Common();
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(nameBytes);
        var centralSize = (int)writer.BaseStream.Position - central;
        writer.Write(0x06054B50);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(centralSize);
        writer.Write(central);
        writer.Write((ushort)0);
        return path;
    }

    private static TransferRequest CopyOut(string target, string name) =>
        new([new PanelEntry(name)], target, move: false, _ => Task.FromResult(OverwriteChoice.Overwrite));

    [Fact]
    public async Task A_wrong_password_is_asked_again_and_says_so()
    {
        using var sandbox = new TestSandbox();
        var zip = MakeEncryptedZip(sandbox, "sesame", "secret.txt", "the secret");
        var target = Directory.CreateDirectory(Path.Combine(sandbox.Path, "out")).FullName;
        var asked = new List<string>();
        var answers = new Queue<string?>(["wrong", "also wrong", "sesame"]);
        await using var provider = ArchiveProvider.TryOpen(zip, anyFormat: false, prompt =>
        {
            asked.Add(prompt);
            return answers.Dequeue();
        })!;

        var outcome = await provider.GetFilesAsync(CopyOut(target, "secret.txt"), new CountingProgress(), CancellationToken.None);

        Assert.Equal(new OperationOutcome(1, 0, 0), outcome);
        Assert.Equal("the secret", File.ReadAllText(Path.Combine(target, "secret.txt")));
        Assert.Equal(3, asked.Count);
        Assert.Contains("is encrypted", asked[0], StringComparison.Ordinal);
        Assert.Contains("Wrong password", asked[1], StringComparison.Ordinal);
        Assert.Contains("Wrong password", asked[2], StringComparison.Ordinal);

        // The right one is remembered.
        File.Delete(Path.Combine(target, "secret.txt"));
        Assert.Equal(new OperationOutcome(1, 0, 0), await provider.GetFilesAsync(CopyOut(target, "secret.txt"), new CountingProgress(), CancellationToken.None));
        Assert.Equal(3, asked.Count);
    }

    [Fact]
    public async Task Giving_up_after_a_wrong_password_reports_it_and_leaves_no_file()
    {
        using var sandbox = new TestSandbox();
        var zip = MakeEncryptedZip(sandbox, "sesame", "secret.txt", "the secret");
        var target = Directory.CreateDirectory(Path.Combine(sandbox.Path, "out")).FullName;
        var answers = new Queue<string?>(["wrong", null]);
        await using var provider = ArchiveProvider.TryOpen(zip, anyFormat: false, _ => answers.Dequeue())!;

        var outcome = await provider.GetFilesAsync(CopyOut(target, "secret.txt"), new CountingProgress(), CancellationToken.None);

        Assert.Equal(1, outcome.Failed);
        Assert.Equal("Wrong password.", outcome.Message);
        Assert.Empty(Directory.EnumerateFiles(target));
    }

    [Fact]
    public async Task Cancelling_the_first_question_says_no_password_was_given()
    {
        using var sandbox = new TestSandbox();
        var zip = MakeEncryptedZip(sandbox, "sesame", "secret.txt", "the secret");
        var target = Directory.CreateDirectory(Path.Combine(sandbox.Path, "out")).FullName;
        await using var provider = ArchiveProvider.TryOpen(zip, anyFormat: false, _ => null)!;

        var outcome = await provider.GetFilesAsync(CopyOut(target, "secret.txt"), new CountingProgress(), CancellationToken.None);

        Assert.Equal(1, outcome.Failed);
        Assert.Equal("No password was given.", outcome.Message);
    }

    [Fact]
    public async Task Encrypted_names_ask_again_after_a_wrong_password_and_cancelling_is_not_an_error()
    {
        using var sandbox = new TestSandbox();
        var path = Path.Combine(sandbox.Path, "names.7z");
        File.WriteAllBytes(path, Convert.FromBase64String(EncryptedNames7z));
        var asked = new List<string>();
        var answers = new Queue<string?>(["wrong", "sesame"]);
        await using (var provider = ArchiveProvider.TryOpen(path, anyFormat: false, prompt =>
        {
            asked.Add(prompt);
            return answers.Dequeue();
        }))
        {
            Assert.NotNull(provider);
            Assert.Equal(["inside.txt"], await Names(provider));
        }

        Assert.Contains("is encrypted", asked[0], StringComparison.Ordinal);
        Assert.Contains("Wrong password", asked[1], StringComparison.Ordinal);
        Assert.Throws<OperationCanceledException>(() => ArchiveProvider.TryOpen(path, anyFormat: false, _ => null));
    }

    [Fact]
    public void The_bundled_7z_dll_loads_and_reads_the_common_formats()
    {
        var library = Assert.IsType<SevenZipLibrary>(SevenZipLibrary.Default);
        var names = library.Formats.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Superset(new HashSet<string>(["zip", "7z", "Rar", "tar", "gzip", "Iso"], StringComparer.OrdinalIgnoreCase), names);
        Assert.Contains("zip", library.Extensions);
        Assert.Contains("zip", ArchiveProvider.ExtensionsOpenedOnEnter());
        Assert.DoesNotContain("doc", ArchiveProvider.ExtensionsOpenedOnEnter());
        Assert.True(ArchiveProvider.HasArchiveExtension("data.ZIP"));
        Assert.True(ArchiveProvider.HasArchiveExtension("logs.tar.gz"));
        Assert.False(ArchiveProvider.HasArchiveExtension("report.docx")); // Ctrl+PgDn only
        Assert.False(ArchiveProvider.HasArchiveExtension("notes.txt"));

        // 7-Zip reads these, but they are documents and programs: they open in their program (issue #138).
        Assert.Contains("doc", library.Extensions);
        foreach (var name in new[] { "old.doc", "old.xls", "old.ppt", "app.jar", "app.apk", "setup.exe", "setup.msi", "disk.img", "book.epub" })
        {
            Assert.False(ArchiveProvider.HasArchiveExtension(name), name);
        }

        foreach (var name in new[] { "a.7z", "a.rar", "a.tar", "a.tgz", "a.bz2", "a.xz", "a.cab", "a.iso" })
        {
            Assert.True(ArchiveProvider.HasArchiveExtension(name), name);
        }
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
        await using (var provider = ArchiveProvider.TryOpen(zip, "docs", anyFormat: true, _ => null))
        {
            Assert.NotNull(provider);
            Assert.Equal(zip + "\\docs", provider.Location);
        }

        // A place that is gone: the top of the archive.
        await using (var provider = ArchiveProvider.TryOpen(zip, @"gone\x", anyFormat: true, _ => null))
        {
            Assert.Equal(zip, provider?.Location);
        }

        var renamed = Path.Combine(sandbox.Path, "a.dat");
        File.Copy(zip, renamed);
        Assert.Null(ArchiveProvider.TryOpen(renamed, anyFormat: false, _ => null));
        await using var bySignature = ArchiveProvider.TryOpen(renamed, anyFormat: true, _ => null);
        Assert.Equal("zip", bySignature?.Title.Split(':')[0]);
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

    [Fact]
    public async Task Search_gives_every_entry_with_its_content()
    {
        using var sandbox = new TestSandbox();
        var zip = MakeZip(sandbox, "a.zip", ("readme.txt", "hello"), ("docs/guide.txt", "find the needle"));
        var found = new Dictionary<string, string>();
        await foreach (var entry in ArchivesPlugin.SearchArchive(SevenZipLibrary.Default!, zip, CancellationToken.None))
        {
            if (entry.IsFolder)
            {
                continue;
            }

            Assert.NotNull(entry.OpenAsync);
            Assert.NotNull(entry.Modified); // zip keeps local DOS time: only its being stable matters, for the cache
            await using var stream = await entry.OpenAsync(CancellationToken.None);
            using var reader = new StreamReader(stream);
            found[entry.InnerPath] = await reader.ReadToEndAsync();
        }

        Assert.Equal("hello", found["readme.txt"]);
        Assert.Equal("find the needle", found[@"docs\guide.txt"]);

        // The temporary copies are gone once read.
        Assert.DoesNotContain(Directory.GetFiles(Path.GetTempPath(), "habi-search-*"), f => File.GetCreationTimeUtc(f) > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Search_of_a_file_that_is_no_archive_gives_nothing()
    {
        using var sandbox = new TestSandbox();
        var path = Path.Combine(sandbox.Path, "fake.zip");
        await File.WriteAllTextAsync(path, "not an archive");
        var count = 0;
        await foreach (var unused in ArchivesPlugin.SearchArchive(SevenZipLibrary.Default!, path, CancellationToken.None))
        {
            count++;
        }

        Assert.Equal(0, count);
    }
}
