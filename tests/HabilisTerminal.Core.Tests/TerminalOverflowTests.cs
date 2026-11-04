using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.Core.Tests;

public class TerminalOverflowTests
{
    [Fact]
    public void Rows_beyond_the_free_space_under_the_prompt_overflow()
    {
        var overflow = new TerminalOverflow();
        overflow.Feed("before any command\n\n\n");
        Assert.Equal(0, overflow.OverflowRows);

        overflow.CommandStarted(cursorRow: 6, consoleRows: 10, consoleColumns: 20); // rows 7..9 are free
        overflow.Feed("1\r\n2\r\n3\r\n");
        Assert.Equal(0, overflow.OverflowRows);
        overflow.Feed("4\r\n5\r\n");
        Assert.Equal(2, overflow.OverflowRows);

        overflow.CommandFinished();
        Assert.Equal(0, overflow.OverflowRows);
    }

    [Fact]
    public void Long_lines_wrap_and_escape_sequences_are_not_text()
    {
        var overflow = new TerminalOverflow();
        overflow.CommandStarted(cursorRow: 9, consoleRows: 10, consoleColumns: 10); // nothing free
        overflow.Feed("\x1b[31m0123456789\x1b[0m"); // exactly one row: no wrap yet
        Assert.Equal(0, overflow.OverflowRows);
        overflow.Feed("abcdefghij" + "klm\n"); // wraps twice, then the line feed
        Assert.Equal(3, overflow.OverflowRows);
        overflow.Feed("\x1b]0;a title with\nnewline\a\x1b]133;D\x1b\\");
        Assert.Equal(3, overflow.OverflowRows);
    }

    [Fact]
    public void A_progress_bar_redrawing_in_place_does_not_overflow()
    {
        var overflow = new TerminalOverflow();
        overflow.CommandStarted(cursorRow: 5, consoleRows: 10, consoleColumns: 80); // rows 6..9 are free
        for (var percent = 0; percent < 100; percent++)
        {
            overflow.Feed($"\r{percent}%|####|\n\x1b[A"); // tqdm: a line feed and back up
            overflow.Feed($"\x1b[2K\r{percent}%\x1b[1F\x1b[1E");
        }

        Assert.Equal(0, overflow.OverflowRows);
    }

    [Fact]
    public void Cursor_moves_count_and_clearing_the_screen_gives_the_rows_back()
    {
        var overflow = new TerminalOverflow();
        overflow.CommandStarted(cursorRow: 8, consoleRows: 10, consoleColumns: 80);
        overflow.Feed("\x1b[2J\x1b[H"); // cls: the cursor goes home
        overflow.Feed("1\r\n2\r\n3\r\n4\r\n5\r\n");
        Assert.Equal(0, overflow.OverflowRows);

        overflow.Feed("\x1b[10;1H\n\n");
        Assert.Equal(2, overflow.OverflowRows);

        // It never gives back what it has grown, and the next row down the grown screen is no overflow.
        overflow.Feed("\x1b[1;1H");
        Assert.Equal(2, overflow.OverflowRows);
        overflow.Feed("\x1b[12;1H");
        Assert.Equal(2, overflow.OverflowRows);
        overflow.Feed("\n");
        Assert.Equal(3, overflow.OverflowRows);
    }

    [Fact]
    public void The_alternate_screen_is_noticed_and_its_output_is_not_counted()
    {
        var overflow = new TerminalOverflow();
        overflow.CommandStarted(cursorRow: 9, consoleRows: 10, consoleColumns: 80);
        overflow.Feed("\x1b[?1049h");
        Assert.True(overflow.AlternateScreen);
        overflow.Feed("\n\n\n\n");
        Assert.Equal(0, overflow.OverflowRows);
        overflow.Feed("\x1b[?1049l\n");
        Assert.False(overflow.AlternateScreen);
        Assert.Equal(1, overflow.OverflowRows);
    }
}
