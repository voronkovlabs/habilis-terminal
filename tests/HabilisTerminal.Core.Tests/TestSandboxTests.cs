namespace HabilisTerminal.Core.Tests;

public class TestSandboxTests
{
    [Fact]
    public void Sandbox_is_created_under_temp_and_removed_on_dispose()
    {
        string path;
        using (var sandbox = new TestSandbox())
        {
            path = sandbox.Path;
            Assert.StartsWith(Path.GetTempPath(), path, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(path));

            File.WriteAllText(Path.Combine(path, "a.txt"), "a");
            Assert.True(sandbox.Contains(Path.Combine(path, "a.txt")));
            Assert.False(sandbox.Contains(Path.GetTempPath()));
        }

        Assert.False(Directory.Exists(path));
    }
}
