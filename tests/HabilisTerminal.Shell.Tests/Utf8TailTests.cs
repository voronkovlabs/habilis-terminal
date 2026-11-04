using HabilisTerminal.Shell.ConPty;

namespace HabilisTerminal.Shell.Tests;

public class Utf8TailTests
{
    [Theory]
    [InlineData(new byte[] { 0x41, 0x42 }, 0)] // ASCII
    [InlineData(new byte[] { 0xD1, 0x8F }, 0)] // a whole two-byte character
    [InlineData(new byte[] { 0x41, 0xD1 }, 1)] // cut after its first byte
    [InlineData(new byte[] { 0xE2, 0x9C }, 2)] // three bytes, two arrived
    [InlineData(new byte[] { 0xE2, 0x9C, 0x93 }, 0)]
    [InlineData(new byte[] { 0xF0, 0x9F, 0x98 }, 3)] // four bytes, three arrived
    [InlineData(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }, 0)]
    [InlineData(new byte[] { }, 0)]
    public void A_character_cut_by_a_read_is_kept_for_the_next(byte[] bytes, int expected) =>
        Assert.Equal(expected, Utf8.IncompleteTail(bytes));
}
