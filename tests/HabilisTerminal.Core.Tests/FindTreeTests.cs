using System.Text;
using HabilisTerminal.Core.Search;

namespace HabilisTerminal.Core.Tests;

/// <summary>Issue #415: the results tab's lines and tree, as Visual Studio's Find Results show them.</summary>
public class FindTreeTests
{
    private static IReadOnlyList<FoundLine>? LinesOf(string content, string text, bool matchCase = false, bool wholeWords = false, bool regex = false)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return FoundLines.Read(stream, text, matchCase, wholeWords, regex);
    }

    [Fact]
    public void Lines_hold_the_number_column_and_the_match()
    {
        var lines = LinesOf("first\n    var test = 1;\nno\nTestCase and test\n", "test")!;
        Assert.Equal(2, lines.Count);
        Assert.Equal(new FoundLine(2, 9, "var test = 1;", 4, 4), lines[0]);
        Assert.Equal(4, lines[1].Number);
        Assert.Equal(1, lines[1].Column);
        Assert.Equal("Test", lines[1].Text.Substring(lines[1].MatchStart, lines[1].MatchLength));
    }

    [Fact]
    public void Case_whole_words_and_expressions_are_honoured()
    {
        Assert.Single(LinesOf("Test\ntest\n", "test", matchCase: true)!);
        Assert.Equal([2], LinesOf("testing\na test here\n", "test", wholeWords: true)!.Select(l => l.Number));
        var regex = LinesOf("id = 42\nname = x\n", @"\d+", regex: true)!;
        Assert.Equal("42", regex[0].Text.Substring(regex[0].MatchStart, regex[0].MatchLength));
    }

    [Fact]
    public void A_long_line_is_cut_around_the_match_and_binary_has_no_lines()
    {
        var line = new string('a', 1000) + "NEEDLE" + new string('b', 1000);
        var found = Assert.Single(LinesOf(line, "needle")!);
        Assert.True(found.Text.Length < 300);
        Assert.StartsWith("…", found.Text);
        Assert.Equal("NEEDLE", found.Text.Substring(found.MatchStart, found.MatchLength));
        Assert.Equal(1001, found.Column);

        using var binary = new MemoryStream([0, 1, 2, 0, 0, 0, 0xFF, 0, 0x10, 0, 0, 0]);
        Assert.Null(FoundLines.Read(binary, "x", false, false, false));
    }

    [Fact]
    public void The_tree_groups_by_folder_counts_matches_and_lists_lines()
    {
        var tree = new FindTree([@"D:\src"]);
        tree.Add(new FoundItem(@"D:\src\Core\App\Info.cs", false) { Lines = [new FoundLine(16, 43, "a", 0, 1), new FoundLine(17, 55, "b", 0, 1)] });
        tree.Add(new FoundItem(@"D:\src\Core\Cqs\Handler.cs", false) { Lines = [new FoundLine(175, 35, "c", 0, 1)] });

        var rows = tree.Rows();
        Assert.Equal(
            ["Folder 0 D:\\src 3", "Folder 1 Core 3", "Folder 2 App 2", "File 3 Info.cs 2", "Line 4 a 1", "Line 4 b 1", "Folder 2 Cqs 1", "File 3 Handler.cs 1", "Line 4 c 1"],
            rows.Select(r => $"{r.Kind} {r.Depth} {r.Text} {r.Count}"));
        Assert.Equal(3, tree.Lines);
        Assert.Equal(2, tree.Files);
        Assert.Equal(175, rows[^1].Line!.Number);
    }

    [Fact]
    public void Folding_hides_what_is_below_and_survives_new_results()
    {
        var tree = new FindTree([@"C:\w"]);
        tree.Add(new FoundItem(@"C:\w\a\x.txt", false) { Lines = [new FoundLine(1, 1, "l", 0, 1)] });
        var folder = tree.Rows().Single(r => r.Text == "a");
        tree.SetFolded(folder.Key, true);
        tree.Add(new FoundItem(@"C:\w\a\y.txt", false));
        tree.Add(new FoundItem(@"C:\w\b.txt", false));

        var rows = tree.Rows();
        Assert.Equal([@"C:\w", "a", "b.txt"], rows.Select(r => r.Text));
        Assert.False(rows[1].Expanded);
        Assert.True(rows[1].CanExpand);
        Assert.Equal(2, rows[1].Count);

        tree.FoldFiles(false);
        Assert.Equal(6, tree.Rows().Count);
        tree.FoldFiles(true);
        Assert.DoesNotContain(tree.Rows(), r => r.Kind == FindRowKind.Line);
    }

    [Fact]
    public void Several_roots_and_found_folders_have_their_own_rows()
    {
        var tree = new FindTree([@"C:\", @"D:\"]);
        tree.Add(new FoundItem(@"C:\one.txt", false));
        tree.Add(new FoundItem(@"D:\dir", true));
        var rows = tree.Rows();
        Assert.Equal(["C:", "one.txt", "D:", "dir"], rows.Select(r => r.Text));
        Assert.True(rows[3].Item!.IsFolder);
    }
}
