using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HabilisTerminal.Shell.Integration;

namespace HabilisTerminal.App.Hosting;

/// <summary>
/// Prototype (issue #122): a running console moves to a new process of the app. The old process stops reading, gives the
/// new one the console's handles, its state and the output so far, and lets go once the new one has taken over. The
/// shell and what runs in it notice nothing. Nothing here is for release yet.
/// </summary>
internal static class ConsoleHandoff
{
    public const string Argument = "--adopt";
    public const string TestVariable = "HABI_HANDOFF_TEST";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static bool Enabled => Environment.GetEnvironmentVariable(TestVariable) is { Length: > 0 };

    /// <summary>What the tab knows beyond the shell.</summary>
    public sealed record TabState(bool CommandRunning, string? CommandLine, DateTime CommandStartedAt, int ColorIndex);

    public sealed record Message(ShellHostHandoff Shell, TabState Tab);

    /// <summary>The pipe to adopt from, when this process was started to take a console over.</summary>
    public static string? PipeToAdopt()
    {
        var arguments = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(arguments, Argument);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }

    /// <summary>The old process's side. False: the console stays here, working.</summary>
    public static async Task<bool> SendAsync(ConsoleTab tab, string? folder)
    {
        var watch = Stopwatch.StartNew();
        var pipeName = $"habi-handoff-{Guid.NewGuid():N}";
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cancel = new CancellationTokenSource(Timeout);
        var handedOver = false;
        var prepared = false;
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = folder ?? string.Empty };
            start.ArgumentList.Add(Argument);
            start.ArgumentList.Add(pipeName);
            using var target = Process.Start(start) ?? throw new IOException("The new process did not start.");
            await pipe.WaitForConnectionAsync(cancel.Token).ConfigureAwait(true);

            prepared = true;
            var shell = tab.Host.PrepareHandoff(target.Handle);
            var state = new TabState(tab.CommandRunning, tab.CommandLine, tab.CommandStartedAt, tab.ColorIndex);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Message(shell, state)));
            await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), cancel.Token).ConfigureAwait(true);
            await pipe.WriteAsync(bytes, cancel.Token).ConfigureAwait(true);
            await pipe.FlushAsync(cancel.Token).ConfigureAwait(true);

            // The new process reads on from here. No answer, or its death: the console stays.
            var answer = new byte[1];
            handedOver = await pipe.ReadAsync(answer, cancel.Token).ConfigureAwait(true) == 1 && answer[0] == 1;
            Trace($"handoff {(handedOver ? "done" : "refused")} after {watch.ElapsedMilliseconds} ms, {bytes.Length / 1024} KB");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            Trace($"handoff failed after {watch.ElapsedMilliseconds} ms: {ex.Message}");
        }

        if (handedOver)
        {
            tab.Host.CompleteHandoff();
        }
        else if (prepared)
        {
            tab.Host.CancelHandoff();
        }

        return handedOver;
    }

    /// <summary>The new process's side, before its window is built. Null: nothing to adopt, it starts as usual.</summary>
    public static (Message Message, Action Confirm)? Receive(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect((int)Timeout.TotalMilliseconds);
            var length = new byte[4];
            pipe.ReadExactly(length);
            var bytes = new byte[BitConverter.ToInt32(length)];
            pipe.ReadExactly(bytes);
            var message = JsonSerializer.Deserialize<Message>(bytes) ?? throw new IOException("Empty handoff message.");
            if (Environment.GetEnvironmentVariable("HABI_HANDOFF_DIE") is { Length: > 0 })
            {
                Environment.Exit(1); // test aid: the new process dies before it has confirmed
            }

            return (message, () =>
            {
                try
                {
                    pipe.WriteByte(1);
                    pipe.Flush();
                }
                catch (IOException)
                {
                    // The old process is gone: it gave up the console when it did.
                }

                pipe.Dispose();
            });
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or UnauthorizedAccessException)
        {
            Trace($"handoff could not be received: {ex.Message}");
            pipe.Dispose();
            return null;
        }
    }

    private static void Trace(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "handoff.log"), $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {text}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a prototype's log.
        }
    }
}
