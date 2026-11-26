using System.Runtime.ExceptionServices;

namespace HabilisTerminal.Core.Operations;

/// <summary>
/// Threads that copy files while the walk over the tree goes on (issue #392). The walk posts one action per file with
/// the folder it writes into, and waits when many are pending. Each thread takes a file from the folder the fewest
/// threads write into: NTFS serialises file creation within one folder, so threads spread over folders copy faster.
/// The first failure (an error the user cancelled on, a cancellation) stops the rest and is thrown to the walk.
/// </summary>
internal sealed class CopyWorkers : IDisposable
{
    private const int MaxPending = 4096;

    private readonly object _gate = new();
    private readonly List<Folder> _folders = [];
    private readonly Dictionary<string, Folder> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Thread[] _threads;
    private readonly CancellationToken _token;
    private readonly CancellationTokenRegistration _wake;
    private ExceptionDispatchInfo? _failure;
    private int _pending;
    private bool _completed;
    private bool _stop;
    private bool _joined;

    public CopyWorkers(int count, CancellationToken token)
    {
        _token = token;
        _wake = token.Register(() =>
        {
            lock (_gate)
            {
                Monitor.PulseAll(_gate);
            }
        });
        _threads = [.. Enumerable.Range(1, count).Select(i => new Thread(Work) { IsBackground = true, Name = $"Habilis copy {i}" })];
        foreach (var thread in _threads)
        {
            thread.Start();
        }
    }

    /// <summary>Queues a file's copy into <paramref name="folder"/>; waits while too many are pending.</summary>
    public void Post(string folder, Action work)
    {
        lock (_gate)
        {
            while (_pending >= MaxPending && _failure is null)
            {
                _token.ThrowIfCancellationRequested();
                Monitor.Wait(_gate);
            }

            _token.ThrowIfCancellationRequested();
            _failure?.Throw();
            if (!_byPath.TryGetValue(folder, out var queue))
            {
                queue = new Folder(folder);
                _byPath[folder] = queue;
                _folders.Add(queue);
            }

            queue.Items.Enqueue(work);
            _pending++;
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>Waits for every posted file; throws the first failure.</summary>
    public void Complete()
    {
        Join();
        Volatile.Read(ref _failure)?.Throw();
    }

    /// <summary>Drops what has not started and waits for what has.</summary>
    public void Abort()
    {
        lock (_gate)
        {
            _stop = true;
        }

        Join();
    }

    public void Dispose()
    {
        Abort();
        _wake.Dispose();
    }

    private void Join()
    {
        if (_joined)
        {
            return;
        }

        _joined = true;
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }

        foreach (var thread in _threads)
        {
            thread.Join();
        }
    }

    private void Work()
    {
        while (Take() is { } taken)
        {
            try
            {
                taken.Work();
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _failure ??= ExceptionDispatchInfo.Capture(ex);
                    Monitor.PulseAll(_gate);
                }
            }
            finally
            {
                lock (_gate)
                {
                    taken.Folder.Busy--;
                    Forget(taken.Folder);
                }
            }
        }
    }

    /// <summary>The next file, from the folder with the fewest threads in it; null when there is nothing more to do.</summary>
    private (Action Work, Folder Folder)? Take()
    {
        lock (_gate)
        {
            while (true)
            {
                if (_stop || _failure is not null || _token.IsCancellationRequested)
                {
                    return null;
                }

                if (_pending > 0)
                {
                    Folder? best = null;
                    foreach (var folder in _folders)
                    {
                        if (folder.Items.Count > 0 && (best is null || folder.Busy < best.Busy))
                        {
                            best = folder;
                            if (best.Busy == 0)
                            {
                                break;
                            }
                        }
                    }

                    var work = best!.Items.Dequeue();
                    best.Busy++;
                    _pending--;
                    Monitor.PulseAll(_gate); // the walk may post again
                    return (work, best);
                }

                if (_completed)
                {
                    return null;
                }

                Monitor.Wait(_gate);
            }
        }
    }

    private void Forget(Folder folder)
    {
        if (folder.Items.Count == 0 && folder.Busy == 0)
        {
            _folders.Remove(folder);
            _byPath.Remove(folder.Path);
        }
    }

    private sealed class Folder(string path)
    {
        public string Path { get; } = path;

        public Queue<Action> Items { get; } = new();

        public int Busy { get; set; }
    }
}
