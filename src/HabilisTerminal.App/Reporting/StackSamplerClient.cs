using System.Diagnostics;
using System.IO;

namespace HabilisTerminal.App.Reporting;

/// <summary>
/// The helper process that reads stacks of this process's threads (issue #164). It starts when first needed and ends with
/// its input. It is never killed: a walker killed part way would leave a thread suspended. One that does not answer is
/// just not asked again.
/// </summary>
internal sealed class StackSamplerClient : IDisposable
{
    /// <summary>The first answer also loads the module list, on a machine that may be at full load.</summary>
    private const int FirstAnswerMs = 15000;
    private const int AnswerMs = 2000;

    private readonly Lock _gate = new();
    private Process? _helper;
    private bool _gaveUp;
    private bool _answered;

    /// <summary>The thread's stack, or null: no helper, no answer in time, or nothing readable.</summary>
    public string? Stack(int threadId)
    {
        lock (_gate)
        {
            if (_gaveUp || (_helper ??= Start()) is not { } helper)
            {
                return null;
            }

            try
            {
                helper.StandardInput.WriteLine(threadId);
                helper.StandardInput.Flush();
                var answer = helper.StandardOutput.ReadLineAsync();
                if (!answer.Wait(_answered ? AnswerMs : FirstAnswerMs))
                {
                    GiveUp(helper);
                    return null;
                }

                _answered = true;
                return answer.Result is { Length: > 0 } stack ? stack : null;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or AggregateException)
            {
                GiveUp(helper);
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_helper is { } helper)
            {
                GiveUp(helper);
            }
        }
    }

    /// <summary>Closing its input ends it once it has finished what it does.</summary>
    private void GiveUp(Process helper)
    {
        _gaveUp = true;
        try
        {
            helper.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            MainWindow.Trace($"stack helper not closed: {ex.Message}");
        }

        helper.Dispose();
        _helper = null;
    }

    private Process? Start()
    {
        try
        {
            var info = new ProcessStartInfo(Environment.ProcessPath ?? "habi.exe", ["--perf-stack-sampler", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            };
            return Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MainWindow.Trace($"stack helper not started: {ex.Message}");
            _gaveUp = true;
            return null;
        }
    }
}
