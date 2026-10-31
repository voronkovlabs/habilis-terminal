namespace HabilisTerminal.Shell.Tests;

public class PwshLocatorTests
{
    [Fact]
    public void Find_returns_existing_pwsh_executable()
    {
        var path = PwshLocator.Find();

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(PwshLocator.ExecutableName, Path.GetFileName(path), ignoreCase: true);
    }
}
