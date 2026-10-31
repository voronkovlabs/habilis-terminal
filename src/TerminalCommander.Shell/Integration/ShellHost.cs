using System.Text;
using System.Text.Json;
using TerminalCommander.Shell.ConPty;

namespace TerminalCommander.Shell.Integration;

public enum ShellState
{
    /// <summary>Started, no prompt seen yet (or integration is not loaded).</summary>
    Starting,

    /// <summary>At the prompt: PSReadLine waits for input.</summary>
    Idle,

    /// <summary>Drawing the prompt or running a command.</summary>
    Busy,

    Exited,
}

public sealed record ShellHostOptions(
    string PwshPath,
    string ScriptPath,
    string? WorkingDirectory = null,
    string? InitCommand = null,
    bool LoadProfile = true);

/// <summary>
/// pwsh in a pseudo console with Terminal Commander shell integration (SPEC §7): tracks prompt state and the
/// current directory from OSC sequences, and sends requests (cd, run, insert) to PSReadLine key handlers
/// through a per-session channel directory and the F24 key.
/// </summary>
public sealed class ShellHost : IDisposable
{
    public const string ChannelVariable = "TCMD_CHANNEL";
    public const string RequestFileName = "request.json";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly object _sync = new();
    private readonly ShellHostOptions _options;
    private readonly OscScanner _scanner = new();
    private readonly LinkedList<Request> _queue = new();
    private readonly Timer _timeoutTimer;
    private ConPtySession? _session;
    private Request? _inFlight;
    private DateTime _inFlightSince;
    private int _nextId;

    public ShellHost(ShellHostOptions options)
    {
        _options = options;
        ChannelDirectory = Path.Combine(Path.GetTempPath(), "TerminalCommander", $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        _timeoutTimer = new Timer(_ => CheckTimeout());
    }

    /// <summary>Raw output for the terminal control; nothing is filtered out. Background thread.</summary>
    public event Action<string>? Output;

    public event Action<ShellState>? StateChanged;

    public event Action<string>? DirectoryChanged;

    /// <summary>A request finished: id, op, and "ok" or the error text from the shell.</summary>
    public event Action<int, string, string>? RequestCompleted;

    public event Action<int>? Exited;

    public string ChannelDirectory { get; }

    public ShellState State { get; private set; } = ShellState.Starting;

    public string? CurrentDirectory { get; private set; }

    public void Start(int columns, int rows)
    {
        Directory.CreateDirectory(ChannelDirectory);
        var startInfo = new ConPtyStartInfo(
            BuildCommandLine(),
            _options.WorkingDirectory,
            columns,
            rows,
            new Dictionary<string, string> { [ChannelVariable] = ChannelDirectory });

        _session = ConPtySession.Start(startInfo, OnOutput, OnExited);
    }

    /// <summary>Input from the terminal control (keys, replies to terminal queries).</summary>
    public void Write(string data) => _session?.Write(data);

    public void Resize(int columns, int rows) => _session?.Resize(columns, rows);

    /// <summary>Changes the shell directory without touching the typed command. Coalesced while the shell is busy.</summary>
    public int ChangeDirectory(string path) => Enqueue("cd", new Dictionary<string, string> { ["path"] = path }, coalesce: true);

    /// <summary>Runs a command line through PSReadLine (it goes to history); the typed text is restored afterwards.</summary>
    public int Run(string commandLine) => Enqueue("run", new Dictionary<string, string> { ["command"] = commandLine }, coalesce: false);

    /// <summary>Inserts text at the command line cursor.</summary>
    public int Insert(string text) => Enqueue("insert", new Dictionary<string, string> { ["text"] = text }, coalesce: false);

    /// <summary>Sends a key press to the shell (e.g. history navigation from panel focus).</summary>
    public void SendKey(int virtualKey, char unicodeChar = '\0', int controlKeyState = 0) =>
        _session?.Write(Win32InputSequence.KeyPress(virtualKey, unicodeChar, controlKeyState));

    public void Dispose()
    {
        _timeoutTimer.Dispose();
        _session?.Dispose();
        try
        {
            if (Directory.Exists(ChannelDirectory))
            {
                Directory.Delete(ChannelDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string BuildCommandLine()
    {
        var script = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(_options.InitCommand))
        {
            script.AppendLine(_options.InitCommand);
        }

        script.Append(". '").Append(_options.ScriptPath.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
        var noProfile = _options.LoadProfile ? string.Empty : " -NoProfile";
        return $"\"{_options.PwshPath}\" -NoLogo{noProfile} -NoExit -EncodedCommand {encoded}";
    }

    private void OnOutput(string text)
    {
        _scanner.Scan(text, payload =>
        {
            if (OscDecoder.Decode(payload) is { } shellEvent)
            {
                Handle(shellEvent);
            }
        });

        Output?.Invoke(text);
    }

    private void OnExited(int exitCode)
    {
        SetState(ShellState.Exited);
        Exited?.Invoke(exitCode);
    }

    private void Handle(ShellEvent shellEvent)
    {
        switch (shellEvent)
        {
            case ShellEvent.InputStarted:
                SetState(ShellState.Idle);
                Pump();
                break;

            case ShellEvent.PromptStarted or ShellEvent.CommandStarted:
                SetState(ShellState.Busy);
                break;

            case ShellEvent.DirectoryChanged changed:
                bool changedDirectory;
                lock (_sync)
                {
                    changedDirectory = !string.Equals(CurrentDirectory, changed.Path, StringComparison.Ordinal);
                    CurrentDirectory = changed.Path;
                }

                if (changedDirectory)
                {
                    DirectoryChanged?.Invoke(changed.Path);
                }

                break;

            case ShellEvent.Message { Name: "ack" } ack when ack.Arguments.Count >= 2:
                CompleteInFlight(ack.Arguments[0], string.Join(';', ack.Arguments.Skip(1)));
                break;

            case ShellEvent.Message { Name: "restore-pending" }:
                lock (_sync)
                {
                    _queue.AddFirst(new Request(++_nextId, "restore", new Dictionary<string, string>()));
                }

                break;
        }
    }

    private void SetState(ShellState state)
    {
        lock (_sync)
        {
            if (State == state || State == ShellState.Exited)
            {
                return;
            }

            State = state;
        }

        StateChanged?.Invoke(state);
    }

    private int Enqueue(string op, Dictionary<string, string> arguments, bool coalesce)
    {
        int id;
        lock (_sync)
        {
            id = ++_nextId;
            if (coalesce)
            {
                for (var node = _queue.First; node is not null; node = node.Next)
                {
                    if (node.Value.Op == op)
                    {
                        _queue.Remove(node);
                        break;
                    }
                }
            }

            _queue.AddLast(new Request(id, op, arguments));
        }

        Pump();
        return id;
    }

    /// <summary>Sends the next queued request when the shell is idle and nothing is in flight.</summary>
    private void Pump()
    {
        Request request;
        lock (_sync)
        {
            if (_session is null || State != ShellState.Idle || _inFlight is not null || _queue.First is null)
            {
                return;
            }

            request = _queue.First.Value;
            _queue.RemoveFirst();
            _inFlight = request;
            _inFlightSince = DateTime.UtcNow;
        }

        WriteRequestFile(request);
        _timeoutTimer.Change(RequestTimeout, Timeout.InfiniteTimeSpan);
        _session.Write(Win32InputSequence.KeyPress(Win32InputSequence.VkF24));
    }

    private void WriteRequestFile(Request request)
    {
        var payload = new Dictionary<string, object>(request.Arguments.ToDictionary(p => p.Key, p => (object)p.Value))
        {
            ["id"] = request.Id,
            ["op"] = request.Op,
        };

        var target = Path.Combine(ChannelDirectory, RequestFileName);
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(payload), new UTF8Encoding(false));
        File.Move(temporary, target, overwrite: true);
    }

    private void CompleteInFlight(string id, string status)
    {
        Request? completed;
        lock (_sync)
        {
            completed = _inFlight;
            if (completed is null || completed.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) != id)
            {
                return;
            }

            _inFlight = null;
        }

        _timeoutTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        RequestCompleted?.Invoke(completed.Id, completed.Op, status);
        Pump();
    }

    private void CheckTimeout()
    {
        Request? expired;
        lock (_sync)
        {
            expired = _inFlight;
            if (expired is null || DateTime.UtcNow - _inFlightSince < RequestTimeout)
            {
                return;
            }

            _inFlight = null;
        }

        RequestCompleted?.Invoke(expired.Id, expired.Op, "timeout");
        Pump();
    }

    private sealed record Request(int Id, string Op, Dictionary<string, string> Arguments);
}
