using System.Globalization;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.Core.Tests;

public class PanelStateTests
{
    private static readonly DateTime Day = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static FileEntry File(string name, long size = 0, FileAttributes attributes = FileAttributes.Normal) =>
        new(name, attributes, size, Day, Day, Day);

    private static FileEntry Dir(string name) => new(name, FileAttributes.Directory, 0, Day, Day, Day);

    private static PanelState Create(string path, params FileEntry[] entries)
    {
        var state = new PanelState { Culture = CultureInfo.InvariantCulture };
        state.SetListing(path, entries);
        return state;
    }

    private static string[] Names(PanelState state) => Enumerable.Range(0, state.Count).Select(i => state.ItemAt(i).Name).ToArray();

    [Fact]
    public void New_panels_start_in_medium_view_like_far()
    {
        Assert.Equal(PanelViewMode.Medium, new PanelState().ViewMode);
        Assert.Equal(PanelViewMode.Medium, new Settings.PanelSnapshot().ViewMode);
    }

    [Fact]
    public void Selection_totals_include_folders_counted_with_f3()
    {
        var state = Create(@"C:\work", File("a.txt", 10), Dir("counted"), Dir("not-counted"), Dir("unselected"));
        state.Selection.Toggle("a.txt");
        state.Selection.Toggle("counted");
        state.Selection.Toggle("not-counted");
        Assert.Equal(["counted", "not-counted"], state.SelectedFolders());

        state.FolderSizes["counted"] = new Viewing.DirectorySize.Result(500, 4, 1, 0);
        state.FolderSizes["unselected"] = new Viewing.DirectorySize.Result(9000, 9, 0, 0);

        Assert.Equal((3, 5, 510L), state.SummarizeSelection());
    }

    [Fact]
    public void Parent_entry_is_first_except_at_the_root()
    {
        Assert.Equal(["..", "sub", "a.txt"], Names(Create(@"C:\work", File("a.txt"), Dir("sub"))));
        Assert.Equal(["sub"], Names(Create(@"C:\", Dir("sub"))));
        Assert.Equal(["x"], Names(Create(@"\\server\share", Dir("x"))));
        Assert.Equal(["..", "x"], Names(Create(@"\\server\share\dir", Dir("x"))));
    }

    [Fact]
    public void Refresh_keeps_cursor_on_the_same_name_and_selection_of_existing_names()
    {
        var state = Create(@"C:\w", File("a"), File("b"), File("c"));
        state.TrySetCursor("b");
        state.Selection.Toggle("a");
        state.Selection.Toggle("c");

        state.SetListing(@"C:\w", [File("0"), File("a"), File("b")]);

        Assert.Equal("b", state.Current.Name);
        Assert.Equal(["a"], state.Selection.Names);
    }

    [Fact]
    public void New_directory_clears_selection_and_focuses_the_requested_name()
    {
        var state = Create(@"C:\w\sub", File("x"));
        state.Selection.Toggle("x");

        state.SetListing(@"C:\w", [Dir("other"), Dir("sub")], focusName: "sub");

        Assert.Equal("sub", state.Current.Name);
        Assert.Equal(0, state.Selection.Count);
    }

    [Fact]
    public void Sort_key_twice_reverses_and_keeps_cursor()
    {
        var state = Create(@"C:\w", File("small", 1), File("big", 100), File("mid", 50));
        state.TrySetCursor("mid");

        state.ApplySortKey(SortMode.Size);
        Assert.Equal(["..", "small", "mid", "big"], Names(state));
        state.ApplySortKey(SortMode.Size);

        Assert.Equal(["..", "big", "mid", "small"], Names(state));
        Assert.Equal("mid", state.Current.Name);
    }

    [Fact]
    public void Hidden_and_system_entries_can_be_hidden()
    {
        var state = Create(@"C:\w", File("seen"), File("secret", attributes: FileAttributes.Hidden), File("sys", attributes: FileAttributes.System));

        state.SetShowHidden(false);

        Assert.Equal(["..", "seen"], Names(state));
    }

    [Fact]
    public void Selected_first_moves_selection_to_the_top()
    {
        var state = Create(@"C:\w", File("a"), File("b"), File("c"));
        state.Selection.Toggle("c");

        state.SetSelectedFirst(true);

        Assert.Equal(["..", "c", "a", "b"], Names(state));
    }

    [Fact]
    public void Ins_skips_parent_and_moves_down()
    {
        var state = Create(@"C:\w", File("a"), File("b"));

        state.ToggleCurrentAndMoveDown();
        state.ToggleCurrentAndMoveDown();

        Assert.Equal(["a"], state.Selection.Names);
        Assert.Equal("b", state.Current.Name);
    }

    [Fact]
    public void Target_entries_are_the_selection_or_the_current_entry()
    {
        var state = Create(@"C:\w", File("a"), File("b"));
        state.TrySetCursor("b");
        Assert.Equal(["b"], state.TargetEntries().Select(e => e.Name));

        state.Selection.Toggle("a");
        Assert.Equal(["a"], state.TargetEntries().Select(e => e.Name));

        state.Selection.Clear();
        state.SetCursor(0);
        Assert.Empty(state.TargetEntries());
    }

    [Fact]
    public void Quick_search_finds_by_prefix_with_wraparound_and_wildcards()
    {
        var state = Create(@"C:\w", File("alpha.txt"), File("beta.txt"), File("alpine.md"), File("gamma.cs"));

        Assert.Equal("alpha.txt", state.ItemAt(state.FindNext("al", 0)).Name);
        Assert.Equal("alpine.md", state.ItemAt(state.FindNext("al", 2)).Name);
        Assert.Equal("alpha.txt", state.ItemAt(state.FindNext("AL", 4)).Name);
        Assert.Equal("alpine.md", state.ItemAt(state.FindNext("*.md", 0)).Name);
        Assert.Equal(-1, state.FindNext("zzz", 0));
    }

    [Fact]
    public void Folder_history_moves_revisited_entries_to_the_front()
    {
        var history = new FolderHistory(capacity: 3);
        history.Add(@"C:\a");
        history.Add(@"C:\b");
        history.Add(@"C:\A");
        history.Add(@"C:\c");
        history.Add(@"C:\d");

        Assert.Equal([@"C:\d", @"C:\c", @"C:\A"], history.RecentFirst);
    }
}
