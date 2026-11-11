using Avalonia.Threading;
using HabilisTerminal.Core.Operations;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// A wait that may turn out long (issue #116): an operation card with an animated bar and Cancel, shown only when the
/// work is still going after a moment, so quick work never flashes one. Create and use it on the UI thread.
/// </summary>
internal sealed class WaitCard : IDisposable
{
    private static readonly TimeSpan ShowAfter = TimeSpan.FromMilliseconds(400);

    private readonly OperationControl _control = new();
    private readonly DispatcherTimer _timer = new() { Interval = ShowAfter };
    private OperationToasts.Card? _card;
    private string? _counts;

    public WaitCard(OperationToasts toasts, string title)
    {
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _card = toasts.Add(title, _control, canPause: false);
            _card.ShowWaiting(_counts);
        };
        _timer.Start();
    }

    public CancellationToken Token => _control.Token;

    public bool Cancelled => _control.Token.IsCancellationRequested;

    /// <summary>What has arrived so far ("1,200 items"): a wait with no known total has no percent to show.</summary>
    public void SetCounts(string counts)
    {
        _counts = counts;
        _card?.ShowWaiting(counts);
    }

    public void Dispose()
    {
        _timer.Stop();
        _card?.Close();
        _control.Dispose();
    }
}
