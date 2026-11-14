using System.Text.Json.Nodes;
using HabilisTerminal.Core.Reporting;

namespace HabilisTerminal.Core.Tests;

/// <summary>Reports go to a public tracker: no full paths, no ids of the user's own plugins (issue #248).</summary>
public class ReportPrivacyTests
{
    [Theory]
    [InlineData(@"Could not find file 'G:\work\client\ticket-42\25752\tmp'.", @"Could not find file '<path>\tmp'.")]
    [InlineData(@"   at App.Run() in D:\src\app\Main.cs:line 12", @"   at App.Run() in <path>\Main.cs:line 12")]
    [InlineData(@"File name: '\\server\share\team\notes.txt'", @"File name: '<path>\notes.txt'")]
    [InlineData(@"%USERPROFILE%\Documents\Project X\plan.md is locked", @"<path>\plan.md is locked")]
    [InlineData(@"A drive alone: C:\", @"A drive alone: C:\")]
    [InlineData("No path at all: 12:30, a:b", "No path at all: 12:30, a:b")]
    [InlineData("Could not find file '/Users/me/work/client/plan.md'.", "Could not find file '<path>/plan.md'.")]
    [InlineData("   at App.Run() in /Users/runner/work/app/Main.cs:line 12", "   at App.Run() in <path>/Main.cs:line 12")]
    [InlineData("%USERPROFILE%/Documents/Project-X/plan.md", "<path>/plan.md")]
    [InlineData("~/work/notes.txt is locked", "<path>/notes.txt is locked")]
    [InlineData("See https://github.com/a/b/issues/1 and 1/2/2026, and/or /tmp", "See https://github.com/a/b/issues/1 and 1/2/2026, and/or /tmp")]
    public void A_path_keeps_only_its_last_part(string text, string expected) =>
        Assert.Equal(expected, ReportMasking.HidePaths(text));

    [Fact]
    public void Only_the_plugins_of_this_repository_are_named()
    {
        Assert.Equal("github", ReportMasking.Plugin("github"));
        Assert.Equal("third-party plugin", ReportMasking.Plugin("acme.secret"));
        Assert.Equal("third-party plugin", ReportMasking.Plugin(null));
    }

    [Fact]
    public void The_public_plugin_list_is_the_repositorys_plugins()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "plugins")) || !File.Exists(Path.Combine(root, "HabilisTerminal.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("The repository was not found.");
        }

        var ids = Directory.EnumerateFiles(Path.Combine(root, "plugins"), "plugin.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(f => JsonNode.Parse(File.ReadAllText(f))!["id"]!.GetValue<string>())
            .Order(StringComparer.Ordinal);

        Assert.Equal(ids, ReportMasking.PublicPluginIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_crash_report_shows_no_full_path_in_its_title_or_body()
    {
        var report = CrashReport.FromException(new FileNotFoundException(@"Could not find file 'G:\work\client\ticket-42\tmp'."), "appdomain", "0.1.60", "Windows 11");

        var draft = IssueText.Crash(report, new ReportMasking(null, null, null));

        Assert.DoesNotContain("client", draft.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("client", draft.Body, StringComparison.Ordinal);
        Assert.Contains(@"<path>\tmp", draft.Body, StringComparison.Ordinal);
    }
}
