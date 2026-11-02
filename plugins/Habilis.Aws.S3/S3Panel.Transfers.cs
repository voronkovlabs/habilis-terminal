using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.Win32.SafeHandles;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.S3;

/// <summary>Downloads and uploads: parallel, large files in parts, no partial file left behind locally or in S3.</summary>
internal sealed partial class S3Panel
{
    /// <summary>A download is written to "name.habi-part" and renamed when complete.</summary>
    public const string PartialSuffix = ".habi-part";

    private const int BufferSize = 81920;

    /// <summary>
    /// F5 (and F3 through a temporary copy): objects, folders with everything under them, and whole buckets, into
    /// the local folder. Keys become local names through <see cref="KeyNames"/>.
    /// </summary>
    public async Task<OperationOutcome> GetFilesAsync(TransferRequest request, IOperationProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        var settings = _settings;
        var plan = new DownloadPlan();
        foreach (var entry in request.Entries)
        {
            if (entry.Tag is S3Ref target)
            {
                await PlanDownloadAsync(target, plan, progress, cancellationToken).ConfigureAwait(false);
            }
        }

        progress.SetTotals(plan.Files.Count, plan.Files.Sum(f => f.Size));
        using var run = new TransferRun(request.AskOverwrite, settings.MaxParallel, progress, cancellationToken);
        foreach (var clash in plan.Clashes)
        {
            run.Fail(clash);
        }

        foreach (var folder in plan.Folders)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(request.LocalFolder, folder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                run.Note($"{folder}: the folder could not be created. {ex.Message}");
            }
        }

        await Parallel.ForEachAsync(plan.Files, new ParallelOptions { MaxDegreeOfParallelism = settings.MaxParallel }, async (file, _) =>
            await DownloadFileAsync(file, request.LocalFolder, settings.PartSize, run).ConfigureAwait(false)).ConfigureAwait(false);
        return await FinishAsync(run, plan.Files.Count + plan.Clashes.Count, "downloaded", "Copy", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// F5 / F6 into the panel: local files and folder trees into the current folder. Local names become keys as they
    /// are, with "/" between folders; an empty local folder becomes a "folder/" marker so it shows in S3 too.
    /// </summary>
    public async Task<OperationOutcome> PutFilesAsync(IReadOnlyList<string> localPaths, Func<string, Task<OverwriteChoice>> askOverwrite, IOperationProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localPaths);
        ArgumentNullException.ThrowIfNull(progress);
        if (_location.Bucket is not { } bucket)
        {
            throw new PluginException("Files cannot be uploaded into the bucket list: open a bucket (or a folder in it) first. Creating buckets is not supported.");
        }

        var settings = _settings;
        var region = await RegionOfAsync(bucket, cancellationToken).ConfigureAwait(false);
        var plan = PlanUpload(localPaths, _location.Prefix);
        progress.SetTotals(plan.Files.Count + plan.Folders.Count, plan.Files.Sum(f => f.Size));
        using var run = new TransferRun(askOverwrite, settings.MaxParallel, progress, cancellationToken);
        foreach (var problem in plan.Problems)
        {
            run.Fail(problem);
        }

        foreach (var note in plan.Notes)
        {
            run.Note(note);
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = settings.MaxParallel };
        await Parallel.ForEachAsync(plan.Files, options, async (file, _) =>
            await UploadFileAsync(file, region, bucket, settings.PartSize, run).ConfigureAwait(false)).ConfigureAwait(false);
        await Parallel.ForEachAsync(plan.Folders, options, async (key, _) =>
            await UploadFolderMarkerAsync(key, region, bucket, run).ConfigureAwait(false)).ConfigureAwait(false);
        return await FinishAsync(run, plan.Files.Count + plan.Folders.Count + plan.Problems.Count, "uploaded", "Copy", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What to download for one entry: the object, or every key under the folder or bucket.</summary>
    private async Task PlanDownloadAsync(S3Ref target, DownloadPlan plan, IOperationProgress progress, CancellationToken cancellationToken)
    {
        var region = await RegionOfAsync(target.Bucket, cancellationToken).ConfigureAwait(false);
        if (target.Kind == S3Kind.Object)
        {
            var name = target.Key[(target.Key.LastIndexOf('/') + 1)..];
            plan.AddFile(new DownloadItem(region, target.Bucket, target.Key, target.Size, target.ETag, KeyNames.LocalName(name)));
            return;
        }

        // A folder "a/b/" downloads as "b"; a bucket as its name, with all its keys.
        var prefix = target.Kind == S3Kind.Bucket ? string.Empty : target.Key;
        var root = target.Kind == S3Kind.Bucket ? KeyNames.LocalName(target.Bucket) : KeyNames.LocalName(KeyNames.ChildSegment(KeyNames.ParentPrefix(prefix), prefix, isFolder: true));
        plan.Folders.Add(root);
        progress.Begin($"s3://{target.Bucket}/{prefix}");
        var listing = Api.ListObjectsAsync(region, target.Bucket, prefix, recursive: true, cancellationToken);
        await foreach (var page in Guard(listing, $"Listing s3://{target.Bucket}/{prefix}", "s3:ListBucket", cancellationToken).ConfigureAwait(false))
        {
            foreach (var info in page.Objects)
            {
                var relative = info.Key[prefix.Length..];
                if (relative.Length == 0)
                {
                    // The marker of the folder itself: the root is made anyway.
                }
                else if (relative.EndsWith('/'))
                {
                    // A folder marker ("sub/", or "/" for an empty-named folder): made even if it stays empty.
                    plan.Folders.Add(Path.Combine(root, KeyNames.LocalPath(relative[..^1])));
                }
                else
                {
                    plan.AddFile(new DownloadItem(region, target.Bucket, info.Key, info.Size, info.ETag, Path.Combine(root, KeyNames.LocalPath(relative))));
                }
            }
        }
    }

    /// <summary>One object into "name.habi-part" (in parallel ranged parts when large), then renamed to its name.</summary>
    private async Task DownloadFileAsync(DownloadItem item, string localFolder, long partSize, TransferRun run)
    {
        var token = run.Token;
        if (token.IsCancellationRequested)
        {
            return;
        }

        var target = Path.Combine(localFolder, item.LocalPath);
        var partial = target + PartialSuffix;
        var started = false;
        var finished = false;
        try
        {
            if (Directory.Exists(target))
            {
                throw new IOException($"A folder named {item.LocalPath} is in the way.");
            }

            if (File.Exists(target) && !await run.MayOverwriteAsync(item.LocalPath).ConfigureAwait(false))
            {
                return;
            }

            run.Progress.Begin(item.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            started = true;
            using (var file = File.OpenHandle(partial, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous, item.Size))
            {
                var count = Parts.CountFor(item.Size, partSize);
                await Parts.AllAsync(count, (i, partToken) =>
                {
                    var offset = i * partSize;
                    return DownloadRangeAsync(file, item, offset, count == 1 ? item.Size : Math.Min(partSize, item.Size - offset), run, partToken);
                }, token).ConfigureAwait(false);
            }

            File.Move(partial, target, overwrite: true);
            finished = true;
            run.Done();
        }
        catch (Exception ex) when (IsCancel(ex, token))
        {
            // Cancelled: counted as skipped; the partial file goes below.
        }
        catch (Exception ex)
        {
            run.Fail(Describe(ex, $"Downloading {item.Key}", "s3:GetObject").Message);
        }
        finally
        {
            if (started && !finished)
            {
                TryDelete(partial);
            }
        }
    }

    /// <summary>
    /// <paramref name="length"/> bytes from <paramref name="offset"/>, streamed into the file at the same offset
    /// through one small buffer.
    /// </summary>
    private async Task DownloadRangeAsync(SafeFileHandle file, DownloadItem item, long offset, long length, TransferRun run, CancellationToken cancellationToken)
    {
        await run.Slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var stream = await Api.OpenReadAsync(item.Region, item.Bucket, item.Key, offset, length, item.ETag, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var done = 0L;
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (done + read > length)
                    {
                        throw new IOException($"S3 sent more data than expected for {item.Key}.");
                    }

                    await RandomAccess.WriteAsync(file, buffer.AsMemory(0, read), offset + done, cancellationToken).ConfigureAwait(false);
                    done += read;
                    run.Progress.AddBytes(read);
                }

                if (done != length)
                {
                    throw new IOException(string.Create(CultureInfo.InvariantCulture, $"The download of {item.Key} ended early ({done:N0} of {length:N0} bytes)."));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            run.Slots.Release();
        }
    }

    /// <summary>Lists local files and folders to upload, with their keys.</summary>
    internal static UploadPlan PlanUpload(IReadOnlyList<string> localPaths, string prefix)
    {
        var plan = new UploadPlan();
        foreach (var localPath in localPaths)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localPath));
            var name = Path.GetFileName(path);
            if (name.Length == 0)
            {
                plan.Problems.Add($"{localPath}: a whole drive cannot be uploaded; open it and select its folders and files.");
            }
            else if (Directory.Exists(path))
            {
                Walk(new DirectoryInfo(path), prefix + name + "/", plan);
            }
            else if (File.Exists(path))
            {
                plan.Files.Add(new UploadItem(path, prefix + name, new FileInfo(path).Length));
            }
            else
            {
                plan.Problems.Add($"{localPath}: not found.");
            }
        }

        return plan;
    }

    private static void Walk(DirectoryInfo folder, string keyPrefix, UploadPlan plan)
    {
        var empty = true;
        try
        {
            // Hidden and system files too, as a copy would take them.
            foreach (var info in folder.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                empty = false;
                if (info is not DirectoryInfo sub)
                {
                    plan.Files.Add(new UploadItem(info.FullName, keyPrefix + info.Name, ((FileInfo)info).Length));
                }
                else if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // A junction or folder link may point back up the tree; its target is not uploaded.
                    plan.Notes.Add($"{sub.FullName}: a link to another folder, not uploaded.");
                }
                else
                {
                    Walk(sub, keyPrefix + sub.Name + "/", plan);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            plan.Problems.Add($"{folder.FullName}: cannot be read. {ex.Message}");
            return;
        }

        if (empty)
        {
            plan.Folders.Add(keyPrefix);
        }
    }

    /// <summary>One file: a single request when small, else a multipart upload in parallel parts.</summary>
    private async Task UploadFileAsync(UploadItem item, string region, string bucket, long partSize, TransferRun run)
    {
        var token = run.Token;
        if (token.IsCancellationRequested)
        {
            return;
        }

        try
        {
            S3ObjectInfo? existing;
            await run.Slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                existing = await Api.HeadObjectAsync(region, bucket, item.Key, token).ConfigureAwait(false);
            }
            finally
            {
                run.Slots.Release();
            }

            if (existing is not null && !await run.MayOverwriteAsync(item.Key).ConfigureAwait(false))
            {
                return;
            }

            run.Progress.Begin(item.Path);
            if (item.Size <= partSize)
            {
                var sent = new SentBytes(run.Progress, item.Size);
                await run.Slots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await Api.PutObjectAsync(region, bucket, item.Key, item.Path, sent.Add, token).ConfigureAwait(false);
                }
                finally
                {
                    run.Slots.Release();
                }

                sent.Finish();
            }
            else
            {
                await UploadMultipartAsync(item, region, bucket, Parts.SizeFor(item.Size, partSize), run).ConfigureAwait(false);
            }

            run.Done();
        }
        catch (Exception ex) when (IsCancel(ex, token))
        {
            // Cancelled: counted as skipped.
        }
        catch (Exception ex)
        {
            run.Fail(Describe(ex, $"Uploading {item.Path}", "s3:PutObject").Message);
        }
    }

    /// <summary>Parts in parallel; on any failure or cancel the upload is aborted, so S3 keeps no partial object or parts.</summary>
    private async Task UploadMultipartAsync(UploadItem item, string region, string bucket, long partSize, TransferRun run)
    {
        var token = run.Token;
        string uploadId;
        await run.Slots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            uploadId = await Api.StartMultipartUploadAsync(region, bucket, item.Key, token).ConfigureAwait(false);
        }
        finally
        {
            run.Slots.Release();
        }

        var completed = false;
        try
        {
            var count = Parts.CountFor(item.Size, partSize);
            var eTags = new string[count];
            await Parts.AllAsync(count, async (i, partToken) =>
            {
                var offset = i * partSize;
                var length = Math.Min(partSize, item.Size - offset);
                var sent = new SentBytes(run.Progress, length);
                await run.Slots.WaitAsync(partToken).ConfigureAwait(false);
                try
                {
                    eTags[i] = await Api.UploadPartAsync(region, bucket, item.Key, uploadId, i + 1, item.Path, offset, length, sent.Add, partToken).ConfigureAwait(false);
                }
                finally
                {
                    run.Slots.Release();
                }

                sent.Finish();
            }, token).ConfigureAwait(false);
            await Api.CompleteMultipartUploadAsync(region, bucket, item.Key, uploadId, eTags, token).ConfigureAwait(false);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                await AbortAsync(item, region, bucket, uploadId, run).ConfigureAwait(false);
            }
        }
    }

    private async Task AbortAsync(UploadItem item, string region, string bucket, string uploadId, TransferRun run)
    {
        try
        {
            // Not the operation's token: the abort must run after a cancel too. The request timeout bounds it.
            await Api.AbortMultipartUploadAsync(region, bucket, item.Key, uploadId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            run.Note($"{item.Key}: the unfinished upload could not be removed, so S3 keeps its parts (and bills their storage) until it is aborted, e.g. by a lifecycle rule. {Describe(ex, "Aborting the upload", "s3:AbortMultipartUpload").Message}");
        }
    }

    private async Task UploadFolderMarkerAsync(string key, string region, string bucket, TransferRun run)
    {
        var token = run.Token;
        if (token.IsCancellationRequested)
        {
            return;
        }

        try
        {
            run.Progress.Begin(key);
            await run.Slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await Api.PutEmptyObjectAsync(region, bucket, key, token).ConfigureAwait(false);
            }
            finally
            {
                run.Slots.Release();
            }

            run.Done();
        }
        catch (Exception ex) when (IsCancel(ex, token))
        {
            // Cancelled: counted as skipped.
        }
        catch (Exception ex)
        {
            run.Fail(Describe(ex, $"Creating the folder {key}", "s3:PutObject").Message);
        }
    }

    /// <summary>The outcome; problems are listed in a message as well. A cancel from the host's button is rethrown.</summary>
    private async Task<OperationOutcome> FinishAsync(TransferRun run, int total, string verb, string title, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outcome = run.Outcome(total, verb);
        if (run.Problems.Count > 0)
        {
            var report = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"{outcome.Succeeded} {verb}, {outcome.Failed} failed, {outcome.Skipped} skipped.")
                .AppendLine();
            foreach (var problem in run.Problems.Take(20))
            {
                report.AppendLine(problem);
            }

            if (run.Problems.Count > 20)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"… and {run.Problems.Count - 20} more.");
            }

            await _host.Dialogs.ShowMessageAsync(title, report.ToString().TrimEnd(), MessageKind.Warning).ConfigureAwait(false);
        }

        return outcome;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open by a scanner or the like; nothing more to do.
        }
    }

    internal sealed record DownloadItem(string Region, string Bucket, string Key, long Size, string? ETag, string LocalPath);

    internal sealed record UploadItem(string Path, string Key, long Size);

    private sealed class DownloadPlan
    {
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

        public List<DownloadItem> Files { get; } = [];

        public List<string> Folders { get; } = [];

        /// <summary>Keys whose local names differ only in letter case from one already planned.</summary>
        public List<string> Clashes { get; } = [];

        public void AddFile(DownloadItem item)
        {
            if (_paths.Add(item.LocalPath))
            {
                Files.Add(item);
            }
            else
            {
                Clashes.Add($"{item.Key}: not downloaded. Its local name {item.LocalPath} differs only in letter case from another object's, and Windows file names ignore case.");
            }
        }
    }

    internal sealed class UploadPlan
    {
        public List<UploadItem> Files { get; } = [];

        /// <summary>Keys of "folder/" markers for empty local folders.</summary>
        public List<string> Folders { get; } = [];

        /// <summary>Items that cannot be uploaded (each counts as failed).</summary>
        public List<string> Problems { get; } = [];

        /// <summary>Things left out on purpose, reported but not failures.</summary>
        public List<string> Notes { get; } = [];
    }
}
