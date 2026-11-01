using HabilisTerminal.Core.Interop;

namespace HabilisTerminal.Core.Operations;

/// <summary>
/// One operation per physical volume at a time (SPEC §8): operations on different disks run in parallel, on the same
/// disk they queue. Volumes are taken in a fixed order, so two operations never wait for each other.
/// </summary>
public sealed class OperationScheduler
{
    private readonly Dictionary<string, SemaphoreSlim> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    /// <summary>Volumes currently busy, for the UI ("queued").</summary>
    public bool IsBusy(string path)
    {
        lock (_sync)
        {
            return _volumes.TryGetValue(NativeFile.VolumeOf(path), out var semaphore) && semaphore.CurrentCount == 0;
        }
    }

    /// <summary>Runs <paramref name="work"/> on a worker thread once all volumes of <paramref name="paths"/> are free.</summary>
    public async Task<T> RunAsync<T>(IEnumerable<string> paths, Func<T> work, CancellationToken cancellationToken = default)
    {
        var semaphores = paths.Select(SafeVolume).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(Semaphore).ToArray();
        var taken = new List<SemaphoreSlim>();
        try
        {
            foreach (var semaphore in semaphores)
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                taken.Add(semaphore);
            }

            return await Task.Run(work, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var semaphore in taken)
            {
                semaphore.Release();
            }
        }
    }

    private static string SafeVolume(string path)
    {
        try
        {
            return NativeFile.VolumeOf(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return Path.GetPathRoot(path) ?? path;
        }
    }

    private SemaphoreSlim Semaphore(string volume)
    {
        lock (_sync)
        {
            if (!_volumes.TryGetValue(volume, out var semaphore))
            {
                semaphore = new SemaphoreSlim(1, 1);
                _volumes[volume] = semaphore;
            }

            return semaphore;
        }
    }
}
