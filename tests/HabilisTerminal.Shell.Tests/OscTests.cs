using HabilisTerminal.Shell.Integration;

namespace HabilisTerminal.Shell.Tests;

public class OscTests
{
    private static List<string> ScanAll(params string[] chunks)
    {
        var scanner = new OscScanner();
        var found = new List<string>();
        foreach (var chunk in chunks)
        {
            scanner.Scan(chunk, found.Add);
        }

        return found;
    }

    [Fact]
    public void Scanner_finds_bel_and_st_terminated_sequences()
    {
        var found = ScanAll("text\u001b]133;A\aprompt\u001b]9;9;\"C:\\x\"\u001b\\more");

        Assert.Equal(["133;A", "9;9;\"C:\\x\""], found);
    }

    [Fact]
    public void Scanner_joins_sequences_split_across_chunks()
    {
        var found = ScanAll("a\u001b", "]13", "3;D;", "1\u001b", "\\b");

        Assert.Equal(["133;D;1"], found);
    }

    [Fact]
    public void Scanner_ignores_csi_and_aborted_sequences()
    {
        var found = ScanAll("\u001b[31mred\u001b[0m\u001b]broken\u001b[2J\u001b]133;B\a");

        Assert.Equal(["133;B"], found);
    }

    [Theory]
    [InlineData("133;A", typeof(ShellEvent.PromptStarted))]
    [InlineData("133;B", typeof(ShellEvent.InputStarted))]
    [InlineData("133;C", typeof(ShellEvent.CommandStarted))]
    public void Decoder_maps_semantic_prompt_marks(string payload, Type expected)
    {
        Assert.IsType(expected, OscDecoder.Decode(payload));
    }

    [Theory]
    [InlineData("133;D;0", 0)]
    [InlineData("133;D;42;aid=1", 42)]
    [InlineData("133;D", null)]
    public void Decoder_reads_exit_code(string payload, int? expected)
    {
        var finished = Assert.IsType<ShellEvent.CommandFinished>(OscDecoder.Decode(payload));
        Assert.Equal(expected, finished.ExitCode);
    }

    [Theory]
    [InlineData("9;9;\"C:\\Users\\me\"", @"C:\Users\me")]
    [InlineData("9;9;C:\\Plain", @"C:\Plain")]
    [InlineData("9;9;\"\\\\server\\share\\dir\"", @"\\server\share\dir")]
    [InlineData("9;9;\"/Users/me/a b\"", "/Users/me/a b")]
    public void Decoder_reads_current_directory(string payload, string expected)
    {
        var changed = Assert.IsType<ShellEvent.DirectoryChanged>(OscDecoder.Decode(payload));
        Assert.Equal(expected, changed.Path);
    }

    [WindowsFact]
    public void A_file_uri_is_a_windows_path_on_windows()
    {
        Assert.Equal(@"C:\Program Files", Assert.IsType<ShellEvent.DirectoryChanged>(OscDecoder.Decode("7;file://host/C:/Program%20Files")).Path);
        Assert.Equal(@"\\server\share\dir", Assert.IsType<ShellEvent.DirectoryChanged>(OscDecoder.Decode("7;file://server/share/dir")).Path);
    }

    [UnixFact]
    public void A_file_uri_is_a_posix_path_off_windows() =>
        Assert.Equal("/Users/me/a b", Assert.IsType<ShellEvent.DirectoryChanged>(OscDecoder.Decode("7;file://mac.local/Users/me/a%20b")).Path);

    [Fact]
    public void Decoder_reads_habilis_messages()
    {
        var message = Assert.IsType<ShellEvent.Message>(OscDecoder.Decode("7731;habi;ack;12;error;Access denied"));

        Assert.Equal("ack", message.Name);
        Assert.Equal(["12", "error", "Access denied"], message.Arguments);
    }

    [Theory]
    [InlineData("0;window title")]
    [InlineData("8;;https://example.com")]
    [InlineData("9;4;1;50")]
    public void Decoder_ignores_other_sequences(string payload)
    {
        Assert.Null(OscDecoder.Decode(payload));
    }
}
