using System.Diagnostics;
using System.Text;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.Core.Tests;

public class ViewerDocumentTests
{
    private static string WriteBytes(TestSandbox sandbox, string name, byte[] bytes)
    {
        var path = Path.Combine(sandbox.Path, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static List<string> AllLines(ViewerDocument document)
    {
        var lines = new List<string>();
        var position = document.ContentStart;
        while (position < document.Length)
        {
            lines.Add(document.ReadLine(position, out position));
        }

        return lines;
    }

    [Fact]
    public void Detects_encodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal(TextEncoding.Utf8.Name, TextEncoding.Detect([0xEF, 0xBB, 0xBF, 0x41]).Name);
        Assert.Equal(TextEncoding.Utf16LittleEndian.Name, TextEncoding.Detect([0xFF, 0xFE, 0x41, 0x00]).Name);
        Assert.Equal(TextEncoding.Utf16BigEndian.Name, TextEncoding.Detect([0xFE, 0xFF, 0x00, 0x41]).Name);
        Assert.Equal(TextEncoding.Utf16LittleEndian.Name, TextEncoding.Detect(Encoding.Unicode.GetBytes("plain text without bom")).Name);
        Assert.Equal(TextEncoding.Utf8.Name, TextEncoding.Detect(Encoding.UTF8.GetBytes("Привет, мир")).Name);
        Assert.Equal(TextEncoding.Ansi.Name, TextEncoding.Detect(Encoding.GetEncoding(1251).GetBytes("Привет, мир")).Name);
    }

    [Fact]
    public void Text_and_binary_are_told_apart()
    {
        Assert.True(TextEncoding.LooksLikeText(Encoding.UTF8.GetBytes("line 1\r\nline 2\tTab")));
        Assert.True(TextEncoding.LooksLikeText(Encoding.Unicode.GetBytes("utf-16 text")));
        Assert.False(TextEncoding.LooksLikeText([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00]));
    }

    [Fact]
    public void Lines_split_on_crlf_lf_and_cr_and_navigate_both_ways()
    {
        using var sandbox = new TestSandbox();
        var path = WriteBytes(sandbox, "mixed.txt", Encoding.UTF8.GetBytes("one\r\ntwo\nthree\rfour"));
        using var document = ViewerDocument.Open(path);

        Assert.Equal(["one", "two", "three", "four"], AllLines(document));
        var third = document.NextLineStart(document.NextLineStart(0));
        Assert.Equal("three", document.ReadLine(third, out _));
        Assert.Equal("two", document.ReadLine(document.PreviousLineStart(third), out _));
        Assert.Equal(0, document.PreviousLineStart(document.PreviousLineStart(third)));
        Assert.Equal(third, document.LineStartOf(third + 2));
    }

    [Fact]
    public void Utf16_and_bom_are_handled()
    {
        using var sandbox = new TestSandbox();
        var path = WriteBytes(sandbox, "utf16.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("первая\r\nвторая")]);
        using var document = ViewerDocument.Open(path);

        Assert.Equal(2, document.ContentStart);
        Assert.Equal(["первая", "вторая"], AllLines(document));
    }

    [Fact]
    public void Encoding_can_be_switched_to_1251_and_866()
    {
        using var sandbox = new TestSandbox();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path1251 = WriteBytes(sandbox, "a.txt", Encoding.GetEncoding(1251).GetBytes("Кириллица"));
        var path866 = WriteBytes(sandbox, "b.txt", Encoding.GetEncoding(866).GetBytes("Кириллица"));
        using var doc1251 = ViewerDocument.Open(path1251);
        using var doc866 = ViewerDocument.Open(path866);

        doc1251.Encoding = TextEncoding.CodePage(1251, "Windows-1251");
        doc866.Encoding = TextEncoding.CodePage(866, "CP866");

        Assert.Equal("Кириллица", doc1251.ReadLine(0, out _));
        Assert.Equal("Кириллица", doc866.ReadLine(0, out _));
    }

    [Fact]
    public void Search_finds_forward_backward_case_insensitive_and_across_chunks()
    {
        using var sandbox = new TestSandbox();
        var text = new string('x', (1 << 20) - 3) + "NeedLE" + new string('y', 100) + "needle";
        var path = WriteBytes(sandbox, "big.txt", Encoding.UTF8.GetBytes(text));
        using var document = ViewerDocument.Open(path);

        var first = document.Find("needle", 0, forward: true, matchCase: false);
        Assert.Equal((1 << 20) - 3, first);
        var second = document.Find("needle", first!.Value + 1, forward: true, matchCase: false);
        Assert.Equal(text.LastIndexOf("needle", StringComparison.Ordinal), second);
        Assert.Equal(second, document.Find("needle", first.Value + 1, forward: true, matchCase: true));
        Assert.Equal(first, document.Find("needle", second!.Value, forward: false, matchCase: false));
        Assert.Null(document.Find("absent", 0, forward: true, matchCase: false));
    }

    [Fact]
    public void Search_reports_byte_offsets_after_multibyte_characters()
    {
        using var sandbox = new TestSandbox();
        var path = WriteBytes(sandbox, "ru.txt", Encoding.UTF8.GetBytes("Привет мир"));
        using var document = ViewerDocument.Open(path);

        Assert.Equal(Encoding.UTF8.GetByteCount("Привет "), document.Find("мир", 0, forward: true, matchCase: true));
    }

    [Fact]
    public void Line_numbers_and_go_to_line()
    {
        using var sandbox = new TestSandbox();
        var path = WriteBytes(sandbox, "lines.txt", Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}"))));
        using var document = ViewerDocument.Open(path);

        var offset = document.OffsetOfLine(42);
        Assert.Equal("line 42", document.ReadLine(offset, out _));
        Assert.Equal(42, document.LineNumberOf(offset));
    }

    [Fact]
    public void Empty_files_open()
    {
        using var sandbox = new TestSandbox();
        var path = WriteBytes(sandbox, "empty.txt", []);
        using var document = ViewerDocument.Open(path);

        Assert.Equal(0, document.Length);
        Assert.Empty(AllLines(document));
        Assert.Null(document.Find("x", 0, true, false));
    }

    [Fact]
    public void Very_long_lines_are_split_for_display()
    {
        using var sandbox = new TestSandbox();
        var path = WriteBytes(sandbox, "long.txt", Encoding.ASCII.GetBytes(new string('a', ViewerDocument.MaxLineBytes * 3)));
        using var document = ViewerDocument.Open(path);

        Assert.Equal(3, AllLines(document).Count);
    }

    [Fact]
    public void Large_files_open_instantly_and_reach_the_end()
    {
        using var sandbox = new TestSandbox();
        var path = Path.Combine(sandbox.Path, "large.log");
        using (var stream = File.Create(path))
        {
            var line = Encoding.ASCII.GetBytes("2026-11-01 12:00:00 INFO something happened in the system\n");
            for (var i = 0; i < 2_000_000; i++)
            {
                stream.Write(line);
            }

            stream.Write("THE END"u8);
        }

        var stopwatch = Stopwatch.StartNew();
        using var document = ViewerDocument.Open(path);
        var last = document.PreviousLineStart(document.Length);
        var text = document.ReadLine(last, out _);
        stopwatch.Stop();

        Assert.Equal("THE END", text);
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Directory_size_counts_files_without_entering_links()
    {
        using var sandbox = new TestSandbox();
        var tree = Directory.CreateDirectory(Path.Combine(sandbox.Path, "tree", "deep")).FullName;
        File.WriteAllBytes(Path.Combine(tree, "a.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(sandbox.Path, "tree", "b.bin"), new byte[500]);
        var outside = Directory.CreateDirectory(Path.Combine(sandbox.Path, "outside")).FullName;
        File.WriteAllBytes(Path.Combine(outside, "big.bin"), new byte[100_000]);
        CopyMoveTests.RunCmd($"mklink /J \"{Path.Combine(sandbox.Path, "tree", "link")}\" \"{outside}\"");

        var result = DirectorySize.Compute(Path.Combine(sandbox.Path, "tree"));

        Assert.Equal(1500, result.Bytes);
        Assert.Equal(2, result.Files);
        Assert.Equal(2, result.Folders);
    }

    [Fact]
    public void Directory_size_reports_running_totals_and_stops_when_cancelled()
    {
        using var sandbox = new TestSandbox();
        for (var i = 0; i < 3; i++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(sandbox.Path, "tree", $"d{i}")).FullName;
            File.WriteAllBytes(Path.Combine(folder, "f.bin"), new byte[100]);
        }

        var reports = new List<DirectorySize.Result>();
        var result = DirectorySize.Compute(Path.Combine(sandbox.Path, "tree"), new SyncProgress(reports.Add));
        Assert.Equal(result, reports[^1]);
        Assert.Equal(reports.Select(r => r.Files).Order(), reports.Select(r => r.Files));

        using var cancel = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() =>
            DirectorySize.Compute(Path.Combine(sandbox.Path, "tree"), new SyncProgress(_ => cancel.Cancel()), cancel.Token));
    }

    private sealed class SyncProgress(Action<DirectorySize.Result> report) : IProgress<DirectorySize.Result>
    {
        public void Report(DirectorySize.Result value) => report(value);
    }
}
