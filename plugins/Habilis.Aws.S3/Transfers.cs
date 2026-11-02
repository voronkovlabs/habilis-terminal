using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.S3;

/// <summary>
/// The shared state of one download or upload: the request slots (at most "parallel transfers" requests at once,
/// whether files or parts of large files), the questions about existing targets, and the tally.
/// </summary>
internal sealed class TransferRun : IDisposable
{
    private readonly Func<string, Task<OverwriteChoice>> _askOverwrite;
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _question = new(1, 1);
    private readonly ConcurrentQueue<string> _problems = new();
    private OverwriteChoice? _always;
    private int _succeeded;
    private int _failed;

    public TransferRun(Func<string, Task<OverwriteChoice>> askOverwrite, int parallel, IOperationProgress progress, CancellationToken cancellationToken)
    {
        _askOverwrite = askOverwrite;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Slots = new SemaphoreSlim(parallel, parallel);
        Progress = progress;
    }

    /// <summary>Cancelled by the host's Cancel, or by answering Cancel to an overwrite question.</summary>
    public CancellationToken Token => _stop.Token;

    /// <summary>One slot per running request.</summary>
    public SemaphoreSlim Slots { get; }

    public IOperationProgress Progress { get; }

    public bool CancelledByUser { get; private set; }

    public int Succeeded => Volatile.Read(ref _succeeded);

    public int Failed => Volatile.Read(ref _failed);

    public IReadOnlyCollection<string> Problems => _problems;

    /// <summary>
    /// The target exists: may it be replaced? One question at a time; "all" answers stick. Cancel stops the whole
    /// operation (the items not done yet count as skipped).
    /// </summary>
    public async Task<bool> MayOverwriteAsync(string name)
    {
        if (_always is { } always)
        {
            return always == OverwriteChoice.OverwriteAll;
        }

        await _question.WaitAsync(Token).ConfigureAwait(false);
        try
        {
            if (_always is { } decided)
            {
                return decided == OverwriteChoice.OverwriteAll;
            }

            var choice = await _askOverwrite(name).ConfigureAwait(false);
            switch (choice)
            {
                case OverwriteChoice.OverwriteAll or OverwriteChoice.SkipAll:
                    _always = choice;
                    return choice == OverwriteChoice.OverwriteAll;
                case OverwriteChoice.Overwrite:
                    return true;
                case OverwriteChoice.Cancel:
                    CancelledByUser = true;
                    await _stop.CancelAsync().ConfigureAwait(false);
                    return false;
                default:
                    return false;
            }
        }
        finally
        {
            _question.Release();
        }
    }

    public void Done()
    {
        Interlocked.Increment(ref _succeeded);
        Progress.CompleteItem();
    }

    public void Fail(string message)
    {
        Interlocked.Increment(ref _failed);
        _problems.Enqueue(message);
    }

    /// <summary>A problem that is not an item of its own, e.g. a multipart upload that could not be aborted.</summary>
    public void Note(string message) => _problems.Enqueue(message);

    /// <summary>The summary for the host: items neither done nor failed count as skipped.</summary>
    public OperationOutcome Outcome(int total, string verb)
    {
        var skipped = Math.Max(0, total - Succeeded - Failed);
        string? message = null;
        if (CancelledByUser)
        {
            message = $"Cancelled: {Succeeded} {verb}, {skipped} skipped{(Failed > 0 ? $", {Failed} failed" : string.Empty)}.";
        }
        else if (!_problems.IsEmpty)
        {
            message = $"{Failed} failed, {Succeeded} {verb}{(skipped > 0 ? $", {skipped} skipped" : string.Empty)}. {_problems.First()}";
        }

        return new OperationOutcome(Succeeded, skipped, Failed, message);
    }

    public void Dispose()
    {
        _stop.Dispose();
        _question.Dispose();
        Slots.Dispose();
    }
}

/// <summary>Bytes sent for one request, as the AWS SDK reports them, counted at most once even if a request is retried.</summary>
internal sealed class SentBytes(IOperationProgress progress, long expected)
{
    private readonly Lock _lock = new();
    private long _reported;

    public void Add(long bytes)
    {
        long add;
        lock (_lock)
        {
            add = Math.Min(bytes, expected - _reported);
            if (add <= 0)
            {
                return;
            }

            _reported += add;
        }

        progress.AddBytes(add);
    }

    /// <summary>The request finished: whatever was not reported yet.</summary>
    public void Finish() => Add(long.MaxValue);
}

internal static class Parts
{
    /// <summary>S3 allows at most 10,000 parts per multipart upload.</summary>
    public const int MaximumCount = 10_000;

    /// <summary>The part size for a file: the configured one, or larger when the file would need too many parts.</summary>
    public static long SizeFor(long fileSize, long partSize) => Math.Max(partSize, (fileSize + MaximumCount - 1) / MaximumCount);

    public static int CountFor(long fileSize, long partSize) => fileSize <= partSize ? 1 : (int)((fileSize + partSize - 1) / partSize);

    /// <summary>
    /// Runs <paramref name="work"/> for 0..count-1 at once. The first failure cancels the others and is rethrown
    /// (not the cancellations it caused).
    /// </summary>
    public static async Task AllAsync(int count, Func<int, CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            try
            {
                await work(i, stop.Token).ConfigureAwait(false);
            }
            catch
            {
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }).ToList();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception) when (FirstFailure(tasks) is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    private static Exception? FirstFailure(List<Task> tasks) =>
        tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions).FirstOrDefault(e => e is not OperationCanceledException);
}
