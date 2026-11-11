using Avalonia.Threading;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Operations;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.App.Plugins;

/// <summary>
/// A plugin's <see cref="IOperationProgress"/> on an operation card. The card appears on the first report (a quick
/// command never flashes one) and is updated on the UI thread at most ten times a second, however often parallel
/// transfers report. Thread-safe.
/// </summary>
internal sealed class PluginProgress(Dispatcher dispatcher, Func<OperationToasts.Card> createCard) : IOperationProgress
{
    private readonly object _gate = new();
    private OperationToasts.Card? _card;
    private bool _closed;
    private long _totalItems;
    private long _totalBytes;
    private long _doneItems;
    private long _doneBytes;
    private string? _current;
    private int _queued;

    public void SetTotals(long? items, long? bytes)
    {
        lock (_gate)
        {
            _totalItems = items ?? 0;
            _totalBytes = bytes ?? 0;
        }

        Queue();
    }

    public void Begin(string item)
    {
        lock (_gate)
        {
            _current = item;
        }

        Queue();
    }

    public void AddBytes(long bytes)
    {
        Interlocked.Add(ref _doneBytes, bytes);
        Queue();
    }

    public void CompleteItem()
    {
        Interlocked.Increment(ref _doneItems);
        Queue();
    }

    /// <summary>Shows the card after this delay even if the plugin has reported nothing by then (issue #116).</summary>
    public void ShowAfter(TimeSpan delay) =>
        _ = Task.Delay(delay).ContinueWith(_ => Queue(), TaskScheduler.Default);

    /// <summary>Removes the card, if one was shown. Call on the UI thread when the operation ends.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }

        _card?.Close();
        _card = null;
    }

    private void Queue()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1)
        {
            return;
        }

        _ = Task.Delay(100).ContinueWith(_ => dispatcher.Post(() =>
        {
            _queued = 0;
            OperationProgress snapshot;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                snapshot = new OperationProgress(Interlocked.Read(ref _doneBytes), _totalBytes, (int)Interlocked.Read(ref _doneItems), (int)_totalItems, _current);
            }

            _card ??= createCard();
            _card.Update(snapshot);
        }), TaskScheduler.Default);
    }
}
