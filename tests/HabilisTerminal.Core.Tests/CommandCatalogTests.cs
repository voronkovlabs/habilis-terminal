using System.Text.RegularExpressions;
using HabilisTerminal.Core.Commands;

namespace HabilisTerminal.Core.Tests;

public partial class CommandCatalogTests
{
    private static readonly CommandContext OnFile = new() { Cursor = CursorKind.File, CursorName = "a.txt" };

    private static IReadOnlyList<string> Ids(CommandContext context) =>
        [.. CommandCatalog.All.Where(c => c.AppliesWhen(context)).Select(c => c.Id)];

    private static IReadOnlyList<RankedCommand> Rank(string query, CommandContext? context = null, IReadOnlyList<string>? recent = null)
    {
        context ??= OnFile;
        return CommandSearch.Rank(query, CommandCatalog.All, context, recent ?? [], CommandCatalog.SectionsFirst(context));
    }

    [Fact]
    public void Ids_are_unique_and_keys_are_written_the_same_way()
    {
        var ids = CommandCatalog.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var keys in CommandCatalog.All.Select(c => c.Keys).OfType<string>())
        {
            Assert.Matches(KeyPattern(), keys);
        }
    }

    [Fact]
    public void A_key_in_the_same_place_belongs_to_one_command()
    {
        // F2 is the viewer's and the editor's in their own pages; in the same section a key must not repeat.
        var repeated = CommandCatalog.All.Where(c => c.Keys is not null).GroupBy(c => (c.Section, c.Keys)).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(repeated);
    }

    [Fact]
    public void Help_holds_every_command_with_a_key_and_the_notes()
    {
        var rows = CommandCatalog.HelpSections.SelectMany(s => s.Rows).ToList();
        foreach (var command in CommandCatalog.All.Where(c => c.Keys is not null))
        {
            Assert.Contains(rows, r => r.Keys == command.Keys);
        }

        Assert.Contains(rows, r => r.Keys == "Typing");
    }

    [Fact]
    public void A_file_offers_what_a_file_can_do_and_a_parent_row_does_not()
    {
        Assert.Contains("files.edit", Ids(OnFile));
        Assert.Contains("files.copy", Ids(OnFile));
        Assert.Contains("go.enter", Ids(OnFile));

        var onParent = Ids(new CommandContext { Cursor = CursorKind.Parent });
        Assert.DoesNotContain("files.copy", onParent);
        Assert.DoesNotContain("files.edit", onParent);
        Assert.Contains("files.mkdir", onParent);
        Assert.Contains("go.open", onParent);
    }

    [Fact]
    public void The_titles_say_what_is_selected()
    {
        var copy = CommandCatalog.Get("files.copy");
        Assert.Equal("Copy \"a.txt\"", copy.TitleIn(OnFile));
        Assert.Equal("Copy 3 items", copy.TitleIn(OnFile with { Marked = 3 }));
        Assert.Equal("Delete to Recycle Bin \"a.txt\"", CommandCatalog.Get("files.delete").TitleIn(OnFile));
        Assert.Equal("Delete \"a.txt\"", CommandCatalog.Get("files.delete").TitleIn(OnFile with { DeleteToRecycleBin = false }));
        Assert.Equal("Open as archive", CommandCatalog.Get("go.enter").TitleIn(OnFile));
        Assert.Equal("Enter the folder", CommandCatalog.Get("go.enter").TitleIn(OnFile with { Cursor = CursorKind.Folder }));
    }

    [Fact]
    public void Rename_works_on_the_item_under_the_cursor_whatever_is_marked()
    {
        var rename = CommandCatalog.Get("files.rename");
        Assert.Equal("F2", rename.Keys);
        Assert.Equal("Rename \"a.txt\"", rename.TitleIn(OnFile with { Marked = 3 }));
        Assert.Contains("files.rename", Ids(OnFile with { Cursor = CursorKind.Folder, CursorName = "sub" }));
        Assert.DoesNotContain("files.rename", Ids(new CommandContext { Cursor = CursorKind.Parent }));
        Assert.DoesNotContain("files.rename", Ids(OnFile with { ActiveIsPlugin = true }));
        Assert.Contains("Shift+F6", CommandCatalog.HostKeys);
    }

    [Fact]
    public void A_plugin_panel_hides_what_it_does_not_support()
    {
        var plain = OnFile with { ActiveIsPlugin = true };
        var ids = Ids(plain);
        foreach (var hidden in new[] { "files.copy", "files.move", "files.delete", "files.mkdir", "files.edit", "files.attributes", "panel.hidden", "go.root" })
        {
            Assert.DoesNotContain(hidden, ids);
        }

        Assert.Contains("files.view", ids);
        var able = Ids(plain with { CanGet = true, CanDelete = true, CanMakeFolder = true });
        Assert.Contains("files.copy", able);
        Assert.Contains("files.delete", able);
        Assert.Contains("files.mkdir", able);
        Assert.DoesNotContain("files.move", able);
        Assert.Contains("files.move", Ids(plain with { CanGet = true, CanMove = true }));
    }

    [Fact]
    public void Copying_between_two_plugin_panels_is_not_offered_and_into_one_needs_it_to_take_files()
    {
        Assert.DoesNotContain("files.copy", Ids(OnFile with { ActiveIsPlugin = true, CanGet = true, OtherIsPlugin = true, OtherCanPut = true }));
        Assert.DoesNotContain("files.copy", Ids(OnFile with { OtherIsPlugin = true }));
        Assert.Contains("files.copy", Ids(OnFile with { OtherIsPlugin = true, OtherCanPut = true }));
    }

    [Fact]
    public void Each_page_shows_its_own_commands()
    {
        var viewer = Ids(new CommandContext { Area = CommandArea.Viewer });
        Assert.Contains("viewer.wrap", viewer);
        Assert.Contains("window.close", viewer);
        Assert.DoesNotContain("editor.save", viewer);
        Assert.DoesNotContain("files.copy", viewer);

        var editor = Ids(new CommandContext { Area = CommandArea.Editor });
        Assert.Contains("editor.save", editor);
        Assert.DoesNotContain("viewer.wrap", editor);
    }

    [Fact]
    public void The_console_offers_its_own_and_keeps_the_panels_while_they_show()
    {
        var console = OnFile with { Area = CommandArea.Console };
        Assert.Contains("console.paste", Ids(console));
        Assert.Contains("files.copy", Ids(console));
        Assert.DoesNotContain("console.paste", Ids(OnFile));

        var noPanels = Ids(console with { PanelsVisible = false });
        Assert.DoesNotContain("files.copy", noPanels);
        Assert.Contains("panels.toggle", noPanels);
        Assert.Equal(CommandCatalog.CommandLine, Rank(string.Empty, console)[0].Command.Section);
    }

    [Fact]
    public void Tab_commands_need_more_than_one_tab() =>
        Assert.Contains("tabs.next", Ids(OnFile with { ConsoleTabs = 2 }).Except(Ids(OnFile)));

    [Theory]
    [InlineData("Show hidden files", "shf", true)]
    [InlineData("Show hidden files", "show hid", true)]
    [InlineData("Show hidden files", "SHOW", true)]
    [InlineData("Show hidden files", "fsh", false)]
    [InlineData("Copy", "copyx", false)]
    public void Letters_must_come_in_order(string title, string query, bool matches) =>
        Assert.Equal(matches, CommandSearch.Score(title, query) is not null);

    [Fact]
    public void Word_starts_rank_above_letters_inside_words()
    {
        Assert.True(CommandSearch.Score("Show hidden files", "shf") > CommandSearch.Score("Mashfeet", "shf"));
        Assert.Equal("Show hidden files", Rank("shf", OnFile)[0].Title);
    }

    [Fact]
    public void Nothing_typed_gives_the_recent_first_then_what_matters_here()
    {
        var ranked = Rank(string.Empty, OnFile, ["panel.reread", "panels.swap"]);
        Assert.Equal(["panel.reread", "panels.swap"], ranked.Take(2).Select(r => r.Command.Id));
        Assert.Equal(CommandCatalog.Files, ranked[2].Command.Section);
    }

    [Fact]
    public void A_key_finds_its_command() => Assert.Equal("files.copy", Rank("F5")[0].Command.Id);

    [Fact]
    public void What_does_not_apply_is_not_found() =>
        Assert.DoesNotContain(Rank("edit", new CommandContext { Cursor = CursorKind.Parent }), r => r.Command.Id == "files.edit");

    [GeneratedRegex(@"^(Gray [+−*]|((Ctrl|Alt|Shift)\+)*(F\d{1,2}|[A-Z0-9]|Enter|Tab|Up|Down|Left|Right|PgUp|PgDn|Del|Ins|Esc|\\|\[|\]))$")]
    private static partial Regex KeyPattern();
}
