using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Tests;

public class RunnableFilesTests
{
    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("BUILD.CMD", true)]
    [InlineData("run.bat", true)]
    [InlineData("deploy.ps1", true)]
    [InlineData("readme.md", false)]
    [InlineData("photo.jpg", false)]
    [InlineData("archive.tar.gz", false)]
    [InlineData("Makefile", false)]
    [InlineData("trailing.", false)]
    public void Programs_and_scripts_run_other_files_open(string name, bool runnable)
    {
        Assert.Equal(runnable, RunnableFiles.IsRunnable(name, ".COM;.EXE;.BAT;.CMD"));
    }

    [Fact]
    public void An_empty_pathext_uses_the_windows_default_and_custom_entries_count()
    {
        Assert.True(RunnableFiles.IsRunnable("tool.com", null));
        Assert.True(RunnableFiles.IsRunnable("snap-in.msc", ""));
        Assert.True(RunnableFiles.IsRunnable("script.py", ".EXE; .PY ;"));
        Assert.False(RunnableFiles.IsRunnable("tool.com", ".EXE"));
    }
}
