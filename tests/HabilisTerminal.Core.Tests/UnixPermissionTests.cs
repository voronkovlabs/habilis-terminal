using HabilisTerminal.Core.Operations;

namespace HabilisTerminal.Core.Tests;

/// <summary>Get Info's permissions off Windows (issue #274): set and cleared bits, the rest kept, recursively.</summary>
public class UnixPermissionTests
{
    [Fact]
    public void Permission_bits_change_and_the_others_stay()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new TestSandbox();
        var tree = Directory.CreateDirectory(Path.Combine(sandbox.Path, "tree")).FullName;
        var file = Path.Combine(tree, "run.sh");
        File.WriteAllText(file, "echo x");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var change = new AttributeChange
        {
            ModeSet = UnixFileMode.UserExecute | UnixFileMode.GroupExecute,
            ModeClear = UnixFileMode.OtherRead,
            Recursive = true,
        };

        using var control = new OperationControl();
        new AttributesOperation([tree], change, new ScriptedHandler(), control).Run();

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute,
            File.GetUnixFileMode(file));
    }
}
