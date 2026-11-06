using HabilisTerminal.Core.Editing;

namespace HabilisTerminal.Core.Tests;

public class LongLinesTests
{
    private static readonly string AtLimit = new('x', LongLines.ColorLimit);

    [Fact]
    public void Text_with_ordinary_lines_keeps_its_colors()
    {
        Assert.False(LongLines.TooLongToColor(string.Empty));
        Assert.False(LongLines.TooLongToColor("short\r\nlines\n"));
        Assert.False(LongLines.TooLongToColor(AtLimit));
        Assert.False(LongLines.TooLongToColor($"{AtLimit}\n{AtLimit}\r\n{AtLimit}"));
        Assert.False(LongLines.TooLongToColor(string.Join('\n', Enumerable.Repeat("line of a large file", 100_000))));
    }

    private static bool Huge(params int[] lines)
    {
        using var stream = new MemoryStream();
        foreach (var length in lines)
        {
            stream.Write(new byte[length].AsSpan());
            stream.WriteByte((byte)'\n');
        }

        stream.Position = 0;
        return LongLines.HasHugeLine(stream);
    }

    [Fact]
    public void A_huge_line_is_found_across_read_blocks()
    {
        Assert.False(Huge());
        Assert.False(Huge(LongLines.EditorLimit));
        Assert.False(Huge(LongLines.EditorLimit, LongLines.EditorLimit, 5));
        Assert.True(Huge(LongLines.EditorLimit + 1));
        Assert.True(Huge(5, LongLines.EditorLimit + 1, 5));
    }

    [Fact]
    public void A_huge_last_line_without_a_line_end_counts()
    {
        using var stream = new MemoryStream(new byte[LongLines.EditorLimit + 1]);
        Assert.True(LongLines.HasHugeLine(stream));
    }

    [Theory]
    [InlineData("{0}x")]
    [InlineData("first\n{0}x")]
    [InlineData("{0}x\nlast")]
    [InlineData("first\r\n{0}x\r\nlast")]
    public void One_line_over_the_limit_turns_them_off(string shape) =>
        Assert.True(LongLines.TooLongToColor(string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, AtLimit)));
}
