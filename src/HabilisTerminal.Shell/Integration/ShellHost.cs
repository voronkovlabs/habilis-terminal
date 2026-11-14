using System.Text;
using System.Text.Json;
using HabilisTerminal.Shell.ConPty;
using HabilisTerminal.Shell.Pty;

namespace HabilisTerminal.Shell.Integration;

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

/// <summary>Which integration a shell gets, by its program's name (issue #268).</summary>
public enum ShellKind
{
    /// <summary>PowerShell 7: HabilisTerminal.ps1, requests as F24 in win32-input-mode (Windows).</summary>
    Pwsh,

    /// <summary>zsh: shell/zsh through ZDOTDIR, requests as ESC [9024~ (macOS and Linux).</summary>
    Zsh,

    /// <summary>
    /// bash: shell/bash/habilis.bash as its rcfile (#270): prompt marks, the folder, command lines and cd requests; run
    /// and insert are typed into the line.
    /// </summary>
    Bash,

    /// <summary>Any other program: a plain console without integration.</summary>
    Other,
}

/// <param name="PwshPath">The shell's program: pwsh on Windows, the login shell (zsh) elsewhere.</param>
public sealed record ShellHostOptions(
    string PwshPath,
    string ScriptPath,
    string? WorkingDirectory = null,
    string? InitCommand = null,
    bool LoadProfile = true,
    string? ExtraArguments = null,
    bool Integration = true);

/// <summary>
/// pwsh in a pseudo console with Habilis Terminal shell integration (SPEC §7): tracks prompt state and the
/// current directory from OSC sequences, and sends requests (cd, run, insert) to PSReadLine key handlers
/// through a per-session channel directory and the F24 key.
/// </summary>
public sealed class ShellHost : IDisposable
{
    public const string ChannelVariable = "HABI_CHANNEL";
    public const string RequestFileName = "request.json";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The private key sequence the zsh integration binds to its request widget.</summary>
    public const string ZshRequestKey = "\u001b[9024~";

    /// <summary>Ctrl+^ for bash: bash 3.2 (macOS) runs a bind -x only from a single key (#270).</summary>
    public const string BashRequestKey = "\u001e";

    private readonly object _sync = new();
    private readonly ShellHostOptions _options;
    private readonly OscScanner _scanner = new();
    private readonly BracketedPasteTracker _modes = new();
    private readonly LinkedList<Request> _queue = new();
    private readonly Timer _timeoutTimer;
    private IPseudoTerminal? _session;
    private Request? _inFlight;
    private DateTime _inFlightSince;
    private int _nextId;

    public ShellHost(ShellHostOptions options)
    {
        _options = options;
        Kind = KindOf(options.PwshPath);
        ChannelDirectory = Path.Combine(Path.GetTempPath(), "HabilisTerminal", $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        _timeoutTimer = new Timer(_ => CheckTimeout());
    }

    /// <summary>Raw output for the terminal control; nothing is filtered out. Background thread.</summary>
    public event Action<string>? Output;

    public event Action<ShellState>? StateChanged;

    public event Action<string>? DirectoryChanged;

    /// <summary>A request finished: id, op, and "ok" or the error text from the shell.</summary>
    public event Action<int, string, string>? RequestCompleted;

    public event Action<int>? Exited;

    /// <summary>Esc on an empty command line inside the console while <see cref="PanelsShown"/> is on. Background thread.</summary>
    public event Action? FocusPanelRequested;

    /// <summary>
    /// While <see cref="PanelMode"/> is on, a key whose meaning depends on the command line found it empty, so it
    /// belongs to the panel (issues #85, #90): "Enter", "Left", "Right", "Home", "End", "Delete", "Backspace",
    /// "Escape", "Tab", or a key of <see cref="SendGrayKey"/>. Background thread.
    /// </summary>
    public event Action<string>? PanelKeyReturned;

    /// <summary>
    /// Enter on a command line starting with one of <see cref="AddressPrefixes"/> and a colon, or with one of
    /// <see cref="AddressCommands"/>: the address, which the shell did not run (issues #49, #140). Background thread.
    /// </summary>
    public event Action<string>? AddressEntered;

    /// <summary>Enter on "goto &lt;text&gt;": the text, which the shell did not run (issue #152). Background thread.</summary>
    public event Action<string>? GotoEntered;

    /// <summary>
    /// Enter on "cd" with a path that has %VARIABLES% or leads to a file (issue #245): the full path, which exists and
    /// which the shell did not run. Background thread.
    /// </summary>
    public event Action<string>? CdEntered;

    /// <summary>
    /// A command was accepted: the console row its output starts at and the console's rows and columns (issue #82).
    /// Raised from the output thread before the command's output reaches <see cref="Output"/>.
    /// </summary>
    public event Action<int, int, int>? CommandOutputStarted;

    /// <summary>The line of a command that was accepted (issue #119). Background thread.</summary>
    public event Action<string>? CommandLineAccepted;

    /// <summary>A command finished, with its exit code when known. Background thread.</summary>
    public event Action<int?>? CommandFinished;

    /// <summary>
    /// Plugin prefixes ("s3", "sqs"): Enter on "s3:..." is reported through <see cref="AddressEntered"/> instead of
    /// running. Kept as a file in the channel, which the integration script reads when Enter is pressed.
    /// </summary>
    public IReadOnlyList<string> AddressPrefixes
    {
        set => WriteChannelLines("prefixes", value);
    }

    /// <summary>
    /// Commands plugins catch in front of an address, as "command prefix": Enter on "cd s3://bucket/" with "cd s3" here
    /// reports "s3://bucket/" through <see cref="AddressEntered"/> instead of running (issue #140). Kept as a file too.
    /// </summary>
    public IReadOnlyList<string> AddressCommands
    {
        set => WriteChannelLines("commands", value);
    }

    private void WriteChannelLines(string name, IReadOnlyList<string> lines)
    {
        try
        {
            Directory.CreateDirectory(ChannelDirectory);
            File.WriteAllLines(Path.Combine(ChannelDirectory, name), lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the file Enter simply runs the line.
        }
    }

    /// <summary>
    /// A panel is shown: Esc on an empty command line inside the console asks to focus the panel instead of
    /// clearing the line. Kept as a flag file in the channel, which the integration script checks when Esc is pressed.
    /// </summary>
    public bool PanelsShown
    {
        get => File.Exists(Path.Combine(ChannelDirectory, "panels-shown"));
        set => SetFlag("panels-shown", value); // without the flag Esc simply clears the line
    }

    /// <summary>
    /// A panel has the keys (issue #90): on an empty command line Enter, Left, Right, Home, End, Del, Backspace, Esc
    /// and Tab come back through <see cref="PanelKeyReturned"/> instead of acting in the line. A flag file in the
    /// channel, which the integration script checks on those keys.
    /// </summary>
    public bool PanelMode
    {
        get => File.Exists(Path.Combine(ChannelDirectory, "panel-mode"));
        set => SetFlag("panel-mode", value); // without the flag the keys simply act in the line
    }

    private void SetFlag(string name, bool value)
    {
        var flag = Path.Combine(ChannelDirectory, name);
        try
        {
            if (value)
            {
                Directory.CreateDirectory(ChannelDirectory);
                File.WriteAllText(flag, string.Empty);
            }
            else
            {
                File.Delete(flag);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing flag only means the default behaviour of the key.
        }
    }

    public string ChannelDirectory { get; }

    public ShellKind Kind { get; }

    /// <summary>By the program's name: pwsh / powershell, zsh, or anything else.</summary>
    public static ShellKind KindOf(string program) =>
        Path.GetFileNameWithoutExtension(program).ToLowerInvariant() switch
        {
            "pwsh" or "powershell" => ShellKind.Pwsh,
            "zsh" => ShellKind.Zsh,
            "bash" => ShellKind.Bash,
            _ => ShellKind.Other,
        };

    /// <summary>Keys go to the console as win32-input-mode sequences (ConPTY), else as plain VT ones.</summary>
    private static bool Win32Keys => OperatingSystem.IsWindows();

    public ShellState State { get; private set; } = ShellState.Starting;

    public string? CurrentDirectory { get; private set; }

    public void Start(int columns, int rows)
    {
        Directory.CreateDirectory(ChannelDirectory);
        if (!OperatingSystem.IsWindows())
        {
            _session = PseudoTerminal.Start(UnixStartInfo(columns, rows), OnOutput, OnExited);
            return;
        }

        var startInfo = new PtyStartInfo(
            _options.PwshPath,
            BuildArguments(),
            _options.WorkingDirectory,
            columns,
            rows,
            new Dictionary<string, string>
            {
                [ChannelVariable] = ChannelDirectory,

                // Tools started in this console find the app they run in (tools/plugins/habi-plugin.ps1).
                ["HABI_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        _session = PseudoTerminal.Start(startInfo, OnOutput, OnExited);
    }

    /// <summary>
    /// macOS and Linux (issue #268): the shell as a login shell ("-zsh"), with the terminal's variables a Mac terminal
    /// sets. zsh reads its startup files from shell/zsh, which run the user's own and then habilis.zsh.
    /// </summary>
    private PtyStartInfo UnixStartInfo(int columns, int rows)
    {
        var environment = new Dictionary<string, string>
        {
            [ChannelVariable] = ChannelDirectory,
            ["HABI_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["TERM_PROGRAM"] = "HabilisTerminal",
        };

        // An app started from the Finder has no LANG, and zsh would then take UTF-8 for single bytes.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANG")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LC_ALL")))
        {
            environment["LANG"] = "en_US.UTF-8";
        }

        if (Kind == ShellKind.Zsh)
        {
            // Without the user's profile (tests): the user's startup files are looked for in the empty channel folder.
            var userFolder = _options.LoadProfile ? Environment.GetEnvironmentVariable("ZDOTDIR") : ChannelDirectory;
            environment["HABI_USER_ZDOTDIR"] = string.IsNullOrEmpty(userFolder) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : userFolder;
            environment["ZDOTDIR"] = Path.Combine(Path.GetDirectoryName(_options.ScriptPath) ?? AppContext.BaseDirectory, "zsh");
            if (_options.Integration)
            {
                environment["HABI_INTEGRATION"] = "1";
            }

            if (!string.IsNullOrWhiteSpace(_options.InitCommand))
            {
                environment["HABI_INIT"] = _options.InitCommand;
            }
        }

        var extra = string.IsNullOrWhiteSpace(_options.ExtraArguments)
            ? []
            : _options.ExtraArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (Kind == ShellKind.Bash)
        {
            // Not a login shell, which would ignore --rcfile: habilis.bash runs the login files itself.
            environment["HABI_INTEGRATION"] = _options.Integration ? "1" : string.Empty;
            if (!_options.LoadProfile)
            {
                environment["HABI_NO_PROFILE"] = "1";
            }

            if (!string.IsNullOrWhiteSpace(_options.InitCommand))
            {
                environment["HABI_INIT"] = _options.InitCommand;
            }

            var rcfile = Path.Combine(Path.GetDirectoryName(_options.ScriptPath) ?? AppContext.BaseDirectory, "bash", "habilis.bash");
            return new PtyStartInfo(_options.PwshPath, string.Empty, _options.WorkingDirectory, columns, rows, environment, ["--rcfile", rcfile, "-i", .. extra]);
        }

        return new PtyStartInfo(
            _options.PwshPath,
            string.Empty,
            _options.WorkingDirectory,
            columns,
            rows,
            environment,
            extra,
            ArgumentZero: "-" + Path.GetFileName(_options.PwshPath));
    }

    /// <summary>
    /// Input from the terminal control (keys, replies to terminal queries). For zsh a lone Esc goes as a key of its own
    /// (#270): there Esc is the start of the Option keys and cannot be bound by itself.
    /// </summary>
    public void Write(string data) => _session?.Write(Kind == ShellKind.Zsh && data == "\u001b" ? ZshEscapeKey : data);

    /// <summary>The private sequence habilis.zsh binds to Esc: the panel on an empty line, else the line cleared.</summary>
    public const string ZshEscapeKey = "\u001b[9028~";

    public void Resize(int columns, int rows) => _session?.Resize(columns, rows);

    /// <summary>Changes the shell directory without touching the typed command. Coalesced while the shell is busy.</summary>
    public int ChangeDirectory(string path) => Enqueue("cd", new Dictionary<string, string> { ["path"] = path }, coalesce: true);

    /// <summary>Runs a command line through PSReadLine (it goes to history); the typed text is restored afterwards.</summary>
    public int Run(string commandLine) => Enqueue("run", new Dictionary<string, string> { ["command"] = commandLine }, coalesce: false);

    /// <summary>Inserts text at the command line cursor.</summary>
    public int Insert(string text) => Enqueue("insert", new Dictionary<string, string> { ["text"] = text }, coalesce: false);

    /// <summary>
    /// Gray + ("Add"), - ("Subtract") or * ("Multiply") while a panel has the keys: typed on a line with text, returned
    /// through <see cref="PanelKeyReturned"/> on an empty one. Sent as F13..F15, the integration script's table.
    /// </summary>
    public void SendGrayKey(string name)
    {
        if (Win32Keys)
        {
            SendKey(Win32InputSequence.VkF13 + Array.IndexOf(GrayKeys, name));
        }
        else
        {
            _session?.Write($"\u001b[{9025 + Array.IndexOf(GrayKeys, name)}~"); // habilis.zsh binds these
        }
    }

    private static readonly string[] GrayKeys = ["Add", "Subtract", "Multiply"];

    /// <summary>Sends a key press to the shell (e.g. history navigation from panel focus).</summary>
    public void SendKey(int virtualKey, char unicodeChar = '\0', int controlKeyState = 0) =>
        _session?.Write(Win32Keys ? Win32InputSequence.KeyPress(virtualKey, unicodeChar, controlKeyState) : VtKey(virtualKey, unicodeChar, controlKeyState));

    /// <summary>The keys the window sends to a shell, as a VT terminal writes them (issue #268).</summary>
    private static string VtKey(int virtualKey, char unicodeChar, int controlKeyState) => virtualKey switch
    {
        Win32InputSequence.VkUp => "\u001b[A",
        Win32InputSequence.VkDown => "\u001b[B",
        Win32InputSequence.VkSpace when (controlKeyState & Win32InputSequence.LeftCtrl) != 0 => "\0",
        _ => unicodeChar == '\0' ? string.Empty : unicodeChar.ToString(),
    };

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

    private string BuildArguments()
    {
        var script = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(_options.InitCommand))
        {
            script.AppendLine(_options.InitCommand);
        }

        if (_options.Integration)
        {
            script.Append(". '").Append(_options.ScriptPath.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
        }

        var noProfile = _options.LoadProfile ? string.Empty : " -NoProfile";
        var extra = string.IsNullOrWhiteSpace(_options.ExtraArguments) ? string.Empty : " " + _options.ExtraArguments.Trim();
        if (script.Length == 0)
        {
            return $"-NoLogo{noProfile}{extra}";
        }

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
        return $"-NoLogo{noProfile}{extra} -NoExit -EncodedCommand {encoded}";
    }

    /// <summary>The shell asked for bracketed paste (DECSET 2004): pasted text is wrapped in ESC[200~ … ESC[201~.</summary>
    public bool BracketedPaste => _modes.Enabled;

    /// <summary>Clipboard text as it must be written for a paste (CR line breaks, bracketed when the shell asked).</summary>
    public string PrepareForPaste(string text) => _modes.Wrap(text);

    private void OnOutput(string text)
    {
        _modes.Feed(text);
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

            case ShellEvent.Message { Name: "command-start" } start when start.Arguments.Count == 3
                && int.TryParse(start.Arguments[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var row) && int.TryParse(start.Arguments[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var rows) && int.TryParse(start.Arguments[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var columns):
                CommandOutputStarted?.Invoke(row, rows, columns);
                break;

            case ShellEvent.CommandFinished finished:
                CommandFinished?.Invoke(finished.ExitCode);
                break;

            case ShellEvent.Message { Name: "command-line" } accepted when accepted.Arguments.Count == 1:
                try
                {
                    CommandLineAccepted?.Invoke(Encoding.UTF8.GetString(Convert.FromBase64String(accepted.Arguments[0])));
                }
                catch (FormatException)
                {
                    // A garbled message: the tab shows the folder instead.
                }

                break;

            case ShellEvent.Message { Name: "panel-key" } panelKey when panelKey.Arguments.Count == 1:
                PanelKeyReturned?.Invoke(panelKey.Arguments[0]);
                break;

            case ShellEvent.Message { Name: "focus-panel" }:
                FocusPanelRequested?.Invoke();
                break;

            case ShellEvent.Message { Name: "cd-path" } cd when cd.Arguments.Count == 1:
                try
                {
                    CdEntered?.Invoke(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cd.Arguments[0])));
                }
                catch (FormatException)
                {
                    // A garbled message: nowhere to go.
                }

                break;

            case ShellEvent.Message { Name: "goto" } go when go.Arguments.Count == 1:
                try
                {
                    GotoEntered?.Invoke(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(go.Arguments[0])));
                }
                catch (FormatException)
                {
                    // A garbled message: nowhere to go.
                }

                break;

            case ShellEvent.Message { Name: "open-address" } open when open.Arguments.Count == 1:
                try
                {
                    AddressEntered?.Invoke(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(open.Arguments[0])));
                }
                catch (FormatException)
                {
                    // A garbled message: nothing to open.
                }

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

        // bash 3.2 can neither run a line nor insert into it from a key binding (#270): those are typed.
        if (Kind == ShellKind.Bash && request.Op != "cd")
        {
            var text = request.Arguments.Values.FirstOrDefault() ?? string.Empty;
            _session.Write(request.Op switch
            {
                "run" => "\u0005\u0015" + text + "\r", // end of line, cut it (Ctrl+Y gives it back), then the command
                "insert" => text,
                _ => string.Empty,
            });
            CompleteInFlight(request.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "ok");
            return;
        }

        WriteRequestFile(request);
        _timeoutTimer.Change(RequestTimeout, Timeout.InfiniteTimeSpan);
        _session.Write(Kind switch
        {
            ShellKind.Zsh => ZshRequestKey,
            ShellKind.Bash => BashRequestKey,
            _ => Win32InputSequence.KeyPress(Win32InputSequence.VkF24),
        });
    }

    private void WriteRequestFile(Request request)
    {
        if (Kind is ShellKind.Zsh or ShellKind.Bash)
        {
            WriteZshRequest(request); // plain assignments, which bash reads too
            return;
        }

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

    /// <summary>The request as zsh assignments, which habilis.zsh sources: single quotes keep any text as it is.</summary>
    private void WriteZshRequest(Request request)
    {
        static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var argument = request.Arguments.Values.FirstOrDefault() ?? string.Empty;
        var text = $"habi_id={request.Id}\nhabi_op={Quote(request.Op)}\nhabi_arg={Quote(argument)}\n";
        var target = Path.Combine(ChannelDirectory, "request.zsh");
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
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
