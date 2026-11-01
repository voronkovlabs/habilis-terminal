using System.Text;
using HabilisTerminal.Core.Editing;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.Core.Tests;

public class TextFileTests
{
    public static TheoryData<string, byte[]> RoundTripFiles()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = "Первая строка\r\nSecond line\r\n";
        return new TheoryData<string, byte[]>
        {
            { "utf8.txt", Encoding.UTF8.GetBytes(text) },
            { "utf8-bom.txt", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)] },
            { "utf16.txt", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)] },
            { "utf16be.txt", [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)] },
            { "lf.txt", Encoding.UTF8.GetBytes("a\nb\nc\n") },
            { "mixed.txt", Encoding.UTF8.GetBytes("a\r\nb\nc\rd") },
            { "ascii-no-newline.txt", Encoding.ASCII.GetBytes("just text") },
        };
    }

    [Theory]
    [MemberData(nameof(RoundTripFiles))]
    public void Saving_without_edits_keeps_files_byte_identical(string name, byte[] bytes)
    {
        using var sandbox = new TestSandbox();
        var path = Path.Combine(sandbox.Path, name);
        File.WriteAllBytes(path, bytes);

        var file = TextFile.Load(path);
        file.Save(file.Text);

        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Ansi_files_round_trip_after_choosing_the_code_page()
    {
        using var sandbox = new TestSandbox();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1251).GetBytes("Кириллица 1251\r\n");
        var path = Path.Combine(sandbox.Path, "1251.txt");
        File.WriteAllBytes(path, bytes);

        var file = TextFile.Load(path).Reinterpret(TextEncoding.CodePage(1251, "Windows-1251"));
        Assert.Equal("Кириллица 1251\r\n", file.Text);
        file.Save(file.Text);

        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("a\r\nb\r\nc\n", LineEnding.CrLf)]
    [InlineData("a\nb\nc\r\n", LineEnding.Lf)]
    [InlineData("a\rb\rc", LineEnding.Cr)]
    public void Dominant_line_ending(string text, LineEnding expected)
    {
        Assert.Equal(expected, TextFile.DominantLineEnding(text));
    }

    [Fact]
    public void Save_keeps_attributes_and_creation_time_and_leaves_no_temp_files()
    {
        using var sandbox = new TestSandbox();
        var path = Path.Combine(sandbox.Path, "keep.txt");
        File.WriteAllText(path, "old");
        var created = new DateTime(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Archive);

        TextFile.Load(path).Save("new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal(created, File.GetCreationTimeUtc(path));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        Assert.Single(Directory.GetFiles(sandbox.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Save_as_writes_a_new_file()
    {
        using var sandbox = new TestSandbox();
        var path = Path.Combine(sandbox.Path, "a.txt");
        File.WriteAllText(path, "a");

        var saved = TextFile.Load(path).Save("b", Path.Combine(sandbox.Path, "b.txt"));

        Assert.Equal("a", File.ReadAllText(path));
        Assert.Equal("b", File.ReadAllText(saved.Path));
    }

    [Fact]
    public void External_changes_are_detected()
    {
        using var sandbox = new TestSandbox();
        var path = Path.Combine(sandbox.Path, "watched.txt");
        File.WriteAllText(path, "one");
        var file = TextFile.Load(path);

        Assert.False(file.ChangedOnDisk());
        File.WriteAllText(path, "changed by someone else");
        Assert.True(file.ChangedOnDisk());
    }

    [Fact]
    public void Read_only_files_cannot_be_saved_until_the_attribute_is_cleared()
    {
        using var sandbox = new TestSandbox();
        var path = Path.Combine(sandbox.Path, "ro.txt");
        File.WriteAllText(path, "x");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var file = TextFile.Load(path);

        Assert.ThrowsAny<Exception>(() => file.Save("y"));
        File.SetAttributes(path, FileAttributes.Normal);
        file.Save("y");

        Assert.Equal("y", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(sandbox.Path));
    }
}
