using HabilisTerminal.Core.Reporting;

namespace HabilisTerminal.Core.Tests;

/// <summary>The red panel messages are kept and go with reports, masked (issue #292). One class: the log is static.</summary>
public sealed class PanelErrorsTests
{
    private static readonly DateTime Noon = new(2026, 11, 14, 12, 0, 0);

    [Fact]
    public void Messages_are_kept_on_one_line_and_a_repeat_counts_once()
    {
        using var sandbox = new TestSandbox();
        PanelErrors.Initialize(Path.Combine(sandbox.Path, "panel-errors.log"));

        PanelErrors.Record("Left", "Cannot open 'a':\r\naccess denied", Noon);
        PanelErrors.Record("Left", "Cannot open 'a': access denied", Noon.AddSeconds(2));
        PanelErrors.Record("Right", "Cannot open 'a': access denied", Noon.AddSeconds(3));
        PanelErrors.Record("Left", "Cannot open 'a': access denied", Noon.AddSeconds(30));
        PanelErrors.Record("Left", "   ", Noon.AddSeconds(31));

        Assert.Equal(
            [("Left", "Cannot open 'a': access denied"), ("Right", "Cannot open 'a': access denied"), ("Left", "Cannot open 'a': access denied")],
            PanelErrors.Recent().Select(e => (e.Panel, e.Message)));
    }

    [Fact]
    public void The_log_survives_a_restart_and_keeps_only_the_latest()
    {
        using var sandbox = new TestSandbox();
        var file = Path.Combine(sandbox.Path, "state", "panel-errors.log");
        PanelErrors.Initialize(file);
        for (var i = 0; i < PanelErrors.Kept + 5; i++)
        {
            PanelErrors.Record("Left", $"error {i}", Noon.AddMinutes(i));
        }

        PanelErrors.Initialize(file);

        var all = PanelErrors.Recent(int.MaxValue);
        Assert.Equal(PanelErrors.Kept, all.Count);
        Assert.Equal("error 5", all[0].Message);
        Assert.Equal(new PanelError(Noon.AddMinutes(PanelErrors.Kept + 4), "Left", $"error {PanelErrors.Kept + 4}"), all[^1]);
        Assert.True(File.ReadAllLines(file).Length <= PanelErrors.Kept + 1);
    }

    [Fact]
    public void A_report_shows_the_latest_with_paths_and_names_hidden()
    {
        using var sandbox = new TestSandbox();
        PanelErrors.Initialize(Path.Combine(sandbox.Path, "panel-errors.log"));
        Assert.Equal(string.Empty, PanelErrors.Section(new ReportMasking(null, null, null)));

        PanelErrors.Record("Right", @"Could not find file 'G:\work\client\secret-project\plan.md'.", Noon);
        PanelErrors.Record("Left", "Cannot go to /Users/sam/work/acme/notes.txt | try again", Noon.AddSeconds(10));

        var section = PanelErrors.Section(new ReportMasking(null, "sam", "SAMS-PC"));

        Assert.Contains("## Panel errors", section, StringComparison.Ordinal);
        Assert.Contains(@"| 2026-11-14 12:00:00 | Right | Could not find file '<path>\plan.md'. |", section, StringComparison.Ordinal);
        Assert.Contains(@"| Left | Cannot go to <path>/notes.txt \| try again |", section, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-project", section, StringComparison.Ordinal);
        Assert.DoesNotContain("acme", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Feedback_carries_the_section_after_the_versions()
    {
        var draft = IssueText.Feedback("Hi", "text", "1.0", "Windows 11", new ReportMasking(null, null, null), "### Recent panel errors\n| x |");
        Assert.EndsWith("### Recent panel errors\n| x |", draft.Body.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("panel errors", IssueText.Feedback("Hi", "text", "1.0", "Windows 11", new ReportMasking(null, null, null)).Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_reads_back_as_written()
    {
        var error = new PanelError(Noon, "Left", "a\\b: no");
        Assert.Equal(error, PanelErrors.Parse(PanelErrors.Format(error)));
        Assert.Null(PanelErrors.Parse("not a log line"));
    }
}
