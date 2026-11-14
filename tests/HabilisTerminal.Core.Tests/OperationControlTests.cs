using HabilisTerminal.Core.Operations;

namespace HabilisTerminal.Core.Tests;

public class OperationControlTests
{
    /// <summary>Issue #310: a progress update posted just before the end read the token after the operation had disposed it.</summary>
    [Fact]
    public void A_disposed_control_still_answers_and_ignores_the_buttons()
    {
        var control = new OperationControl();
        control.Pause();
        control.Dispose();

        Assert.False(control.Token.IsCancellationRequested);
        Assert.False(control.IsPaused);
        control.Cancel();
        control.Pause();
        control.Resume();
        Assert.False(control.Token.IsCancellationRequested);
    }

    [Fact]
    public void A_cancelled_control_keeps_saying_so_after_disposal()
    {
        var control = new OperationControl();
        control.Cancel();
        control.Dispose();

        Assert.True(control.Token.IsCancellationRequested);
    }
}
