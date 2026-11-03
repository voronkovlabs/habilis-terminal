using System.Diagnostics;
using System.IO;
using HabilisTerminal.Plugins;

namespace Habilis.Gpu;

/// <summary>Where local GPU samples come from; a fake stands in for it in the tests.</summary>
internal interface IGpuSource
{
    Task<GpuSnapshot> SampleAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The NVIDIA driver's <c>nvidia-smi</c> (installed with the driver into System32), run for each sample. Read-only:
/// it only queries.
/// </summary>
internal sealed class NvidiaSmi(Func<DateTime> clock) : IGpuSource
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<GpuSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        var gpus = NvidiaSmiFormat.ParseGpus(await RunAsync(NvidiaSmiFormat.GpuQuery, cancellationToken).ConfigureAwait(false));
        var processes = NvidiaSmiFormat.ParseProcesses(await RunAsync(NvidiaSmiFormat.ComputeQuery, cancellationToken).ConfigureAwait(false));
        return new GpuSnapshot(gpus, processes, clock());
    }

    /// <summary>The full path of nvidia-smi, or null when the NVIDIA driver is not installed.</summary>
    public static string? Find()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<string> RunAsync(string arguments, CancellationToken cancellationToken)
    {
        var path = Find() ?? throw new PluginException("nvidia-smi was not found: the GPU monitor needs an NVIDIA GPU and its driver.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(path, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new PluginException($"nvidia-smi failed: {(await error.ConfigureAwait(false)).Trim()} {(await output.ConfigureAwait(false)).Trim()}".Trim());
            }

            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new PluginException("nvidia-smi did not answer within 10 seconds.");
        }
    }
}
