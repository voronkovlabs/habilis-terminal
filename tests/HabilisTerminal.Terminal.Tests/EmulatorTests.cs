using HabilisTerminal.Terminal;

namespace HabilisTerminal.Terminal.Tests;

/// <summary>The VT emulator behind the console view (issue #253).</summary>
public sealed class EmulatorTests
{
    private const string Esc = "\u001b";

    private static TerminalEmulator Make(int columns = 20, int rows = 5) => new(columns, rows, scrollback: 100);

    private static string[] Screen(TerminalEmulator t) => Enumerable.Range(0, t.Rows).Select(r => t.ScreenLine(r).Text()).ToArray();

    [Fact]
    public void Text_wraps_and_scrolls_into_the_scrollback()
    {
        var t = Make(5, 2);
        t.Feed("abcdefg\r\nxy\r\nz");

        Assert.Equal(["xy", "z"], Screen(t));
        Assert.Equal(2, t.ScrollbackCount);
        Assert.Equal("abcde", t.Line(0).Text());
        Assert.True(t.Line(0).Wrapped);
        Assert.Equal("abcdefg" + Environment.NewLine + "xy", t.TextBetween(0, 0, 2, 5));
    }

    [Fact]
    public void The_last_column_waits_before_wrapping()
    {
        var t = Make(5, 2);
        t.Feed("abcde");
        Assert.Equal((4, 0), (t.CursorX, t.CursorY));
        t.Feed("\r\n");
        Assert.Equal(["abcde", ""], Screen(t));
    }

    [Fact]
    public void Cursor_movement_and_erasing()
    {
        var t = Make(10, 3);
        t.Feed("0123456789" + Esc + "[2;3Hab" + Esc + "[1;5H" + Esc + "[K" + Esc + "[3;1Hzz" + Esc + "[1D" + Esc + "[1K");

        Assert.Equal(["0123", "  ab", ""], Screen(t));
        t.Feed(Esc + "[2J");
        Assert.Equal(["", "", ""], Screen(t));
    }

    [Fact]
    public void Colours_in_every_form()
    {
        var t = Make();
        t.Feed(Esc + "[1;31;42ma" + Esc + "[38;5;200;48;2;1;2;3mb" + Esc + "[38:2::10:20:30mc" + Esc + "[0;94md" + Esc + "[7;4me" + Esc + "[me");

        var cells = t.ScreenLine(0).Cells;
        Assert.Equal(new CellStyle(TermColor.Palette(1), TermColor.Palette(2), TextAttributes.Bold), cells[0].Style);
        Assert.Equal(TermColor.Palette(200), cells[1].Style.Foreground);
        Assert.Equal(TermColor.Rgb(1, 2, 3), cells[1].Style.Background);
        Assert.Equal(TermColor.Rgb(10, 20, 30), cells[2].Style.Foreground);
        Assert.Equal(TermColor.Palette(12), cells[3].Style.Foreground);
        Assert.Equal(TextAttributes.Inverse | TextAttributes.Underline, cells[4].Style.Flags);
        Assert.Equal(CellStyle.Plain, cells[5].Style);
    }

    [Fact]
    public void Erasing_keeps_the_background()
    {
        var t = Make(4, 1);
        t.Feed(Esc + "[44m" + Esc + "[2K");
        Assert.All(t.ScreenLine(0).Cells, c => Assert.Equal(TermColor.Palette(4), c.Style.Background));
    }

    [Fact]
    public void Wide_characters_take_two_cells()
    {
        var t = Make(5, 2);
        t.Feed("a中b");
        var cells = t.ScreenLine(0).Cells;
        Assert.Equal("中", cells[1].Text);
        Assert.True(cells[2].IsWideTail);
        Assert.Equal("b", cells[3].Text);
        Assert.Equal("a中b", t.ScreenLine(0).Text());

        // One that does not fit at the end of a line goes to the next.
        t.Feed("\r\nabcd中");
        Assert.Equal(["abcd", "中"], Screen(t)[^2..]);

        // Overwriting half of it blanks the other half.
        t.Feed(Esc + "[2;2Hx");
        Assert.Equal(" x", t.ScreenLine(1).Text());
    }

    [Fact]
    public void Combining_marks_join_the_previous_cell()
    {
        var t = Make();
        t.Feed("e\u0301x");
        Assert.Equal("e\u0301", t.ScreenLine(0).Cells[0].Text);
        Assert.Equal("x", t.ScreenLine(0).Cells[1].Text);
    }

    [Fact]
    public void Emoji_outside_the_basic_plane()
    {
        var t = Make();
        t.Feed("😀!");
        Assert.Equal("😀", t.ScreenLine(0).Cells[0].Text);
        Assert.Equal("!", t.ScreenLine(0).Cells[2].Text);
    }

    [Fact]
    public void Scroll_region_keeps_the_rest_in_place()
    {
        var t = Make(5, 4);
        t.Feed("1\r\n2\r\n3\r\n4" + Esc + "[2;3r" + Esc + "[3;1H\n\n");
        Assert.Equal(["1", "", "", "4"], Screen(t));
        Assert.Equal(0, t.ScrollbackCount);

        t.Feed(Esc + "[r" + Esc + "[2;1HA" + Esc + "[1L");
        Assert.Equal(["1", "", "A", ""], Screen(t));
        t.Feed(Esc + "[1M");
        Assert.Equal(["1", "A", "", ""], Screen(t));
        Assert.Equal(0, t.ScrollbackCount);
    }

    [Fact]
    public void Reverse_index_at_the_top_scrolls_down()
    {
        var t = Make(5, 3);
        t.Feed("a\r\nb\r\nc" + Esc + "[H" + Esc + "M");
        Assert.Equal(["", "a", "b"], Screen(t));
    }

    [Fact]
    public void Alternate_screen_comes_back_with_the_main_one()
    {
        var t = Make(20, 3);
        t.Feed("prompt> less");
        t.Feed(Esc + "[?1049h" + Esc + "[Hpage one");
        Assert.True(t.AlternateScreen);
        Assert.Equal("page one", t.ScreenLine(0).Text());
        t.Feed(Esc + "[?1049l");
        Assert.False(t.AlternateScreen);
        Assert.Equal("prompt> less", t.ScreenLine(0).Text());
        Assert.Equal((12, 0), (t.CursorX, t.CursorY));
    }

    [Fact]
    public void Queries_are_answered()
    {
        var t = Make();
        var replies = new List<string>();
        t.Reply += replies.Add;
        t.Feed(Esc + "[3;7H" + Esc + "[6n" + Esc + "[c");
        Assert.Equal([Esc + "[3;7R" + Esc + "[?1;2c"], replies);
    }

    [Fact]
    public void Modes_a_console_view_needs()
    {
        var t = Make();
        t.Feed(Esc + "[?9001h" + Esc + "[?2004h" + Esc + "[?25l" + Esc + "[?1h" + Esc + "[?1004h" + Esc + "[5 q");
        Assert.True(t.Win32InputMode);
        Assert.True(t.BracketedPaste);
        Assert.False(t.CursorVisible);
        Assert.True(t.ApplicationCursorKeys);
        Assert.True(t.FocusEvents);
        Assert.Equal(CursorShape.Bar, t.CursorShape);
    }

    [Fact]
    public void Titles_and_unknown_strings()
    {
        var t = Make();
        string? title = null;
        t.TitleChanged += s => title = s;
        t.Feed(Esc + "]0;PowerShell\u0007" + Esc + "]633;A" + Esc + "\\" + Esc + "P1$r" + Esc + "\\ok");
        Assert.Equal("PowerShell", title);
        Assert.Equal("ok", t.ScreenLine(0).Text());
    }

    [Fact]
    public void Sequences_split_across_reads()
    {
        var t = Make();
        foreach (var c in Esc + "[31mred" + Esc + "]0;t\u0007中")
        {
            t.Feed(c.ToString());
        }

        Assert.Equal("red中", t.ScreenLine(0).Text());
        Assert.Equal(TermColor.Palette(1), t.ScreenLine(0).Cells[0].Style.Foreground);
    }

    [Fact]
    public void Insert_delete_and_erase_characters()
    {
        var t = Make(10, 1);
        t.Feed("abcdef" + Esc + "[1;3H" + Esc + "[2@" + Esc + "[1;8H" + Esc + "[1P" + Esc + "[1;1H" + Esc + "[1X");
        Assert.Equal(" b  cde", t.ScreenLine(0).Text());
        t.Feed(Esc + "[4hZ" + Esc + "[4l");
        Assert.Equal("Z b  cde", t.ScreenLine(0).Text());
    }

    [Fact]
    public void Tabs_and_line_drawing()
    {
        var t = Make(20, 2);
        t.Feed("a\tb" + Esc + "(0\r\nlqk" + Esc + "(B");
        Assert.Equal("a       b", t.ScreenLine(0).Text());
        Assert.Equal("┌─┐", t.ScreenLine(1).Text());
    }

    [Fact]
    public void Resize_keeps_the_text_near_the_cursor()
    {
        var t = Make(10, 4);
        t.Feed("one\r\ntwo\r\nthree");
        t.Resize(5, 2);
        Assert.Equal(["two", "three"], Screen(t));
        Assert.Equal((4, 1), (t.CursorX, t.CursorY));
        Assert.Equal("one", t.Line(0).Text());

        t.Resize(8, 3);
        Assert.Equal(["two", "three", ""], Screen(t));
    }

    [Fact]
    public void Clear_scrollback_and_full_reset()
    {
        var t = Make(5, 2);
        t.Feed("1\r\n2\r\n3\r\n4");
        Assert.Equal(2, t.ScrollbackCount);
        t.Feed(Esc + "[3J");
        Assert.Equal(0, t.ScrollbackCount);
        t.Feed(Esc + "[31m" + Esc + "c" + "x");
        Assert.Equal(["x", ""], Screen(t));
        Assert.Equal(CellStyle.Plain, t.ScreenLine(0).Cells[0].Style);
    }

    [Fact]
    public void Repeat_the_last_character()
    {
        var t = Make();
        t.Feed("-" + Esc + "[4b");
        Assert.Equal("-----", t.ScreenLine(0).Text());
    }

    [Fact]
    public void A_typical_conpty_frame()
    {
        // ConPTY starts by hiding the cursor, painting the screen with absolute moves and erases, then showing it.
        var t = Make(30, 3);
        t.Feed(Esc + "[?25l" + Esc + "[2J" + Esc + "[m" + Esc + "[H" + "PS C:\\> " + Esc + "[93mGet-ChildItem" + Esc + "[K" + Esc + "[m"
            + "\r\n" + Esc + "[K\r\n" + Esc + "[K" + Esc + "[1;22H" + Esc + "[?25h");
        Assert.Equal(["PS C:\\> Get-ChildItem", "", ""], Screen(t));
        Assert.Equal((21, 0), (t.CursorX, t.CursorY));
        Assert.True(t.CursorVisible);
        Assert.Equal(TermColor.Palette(11), t.ScreenLine(0).Cells[8].Style.Foreground);
    }

    [Fact]
    public void Scrollback_is_trimmed()
    {
        var t = new TerminalEmulator(5, 2, scrollback: 10);
        for (var i = 0; i < 600; i++)
        {
            t.Feed(i + "\r\n");
        }

        Assert.InRange(t.ScrollbackCount, 10, 10 + 256);
        Assert.Equal("599", t.ScreenLine(0).Text());
        Assert.True(t.DroppedLines > 0);
    }

    /// <summary>Issue #312: the cursor row where a command was accepted, for the console that grows.</summary>
    [Fact]
    public void The_command_mark_gives_the_cursor_row_once()
    {
        var emulator = new TerminalEmulator(20, 10);
        emulator.Feed("one\r\ntwo\r\n$ ls\r\n\u001b]133;C\afile\r\n");
        Assert.Equal((3, 10, 20), emulator.TakeCommandMark());
        Assert.Null(emulator.TakeCommandMark());
    }

    /// <summary>Issue #312: on a Unix pty the console grown over the panels shows the lines above, not blank rows below.</summary>
    [Fact]
    public void A_taller_screen_takes_its_new_rows_from_the_scrollback()
    {
        var t = new TerminalEmulator(10, 4, scrollback: 50) { GrowIntoScrollback = true };
        t.Feed(string.Join("\r\n", Enumerable.Range(1, 8)) + "\r\n$ ");
        Assert.Equal(["6", "7", "8", "$"], Screen(t).Select(s => s.Trim()).ToArray());

        t.Resize(10, 7);

        Assert.Equal(["3", "4", "5", "6", "7", "8", "$"], Screen(t).Select(s => s.Trim()).ToArray());
        Assert.Equal(6, t.CursorY);
    }

    [Fact]
    public void Without_growing_into_the_scrollback_new_rows_come_blank_below()
    {
        var t = new TerminalEmulator(10, 4, scrollback: 50);
        t.Feed(string.Join("\r\n", Enumerable.Range(1, 8)) + "\r\n$ ");
        t.Resize(10, 7);

        Assert.Equal(["6", "7", "8", "$", "", "", ""], Screen(t).Select(s => s.Trim()).ToArray());
        Assert.Equal(3, t.CursorY);
    }
}
