using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HabilisTerminal.Core.Assistant;

namespace HabilisTerminal.App.Assistant;

/// <summary>
/// The built-in assistant (issue #197): Claude Code, run headless, with the app's own MCP server to drive the window.
/// A full-window page; Esc hides it and the session goes on, Ctrl+Shift+A brings it back. Every tool that changes
/// something asks here first, as Claude Code asks in a terminal.
/// </summary>
public sealed class AssistantPage : DockPanel, IPage
{
    private readonly Func<ClaudeLaunchOptions> _options;
    private readonly Action<string?> _setToken;
    private readonly StackPanel _log = new() { Margin = new Thickness(24, 12, 24, 12), Spacing = 6 };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly TextBlock _status;
    private readonly Button _stop;
    private readonly HashSet<string> _allowedTools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _tools = new(StringComparer.Ordinal);
    private ClaudeSession? _session;
    private bool _busy;
    private string? _model;
    private Control? _pendingFocus;

    /// <param name="options">How to start Claude Code; asked when a conversation starts, so settings changes apply.</param>
    /// <param name="setToken">Tells the app's API which secret the session's calls carry (null when none runs).</param>
    public AssistantPage(Func<ClaudeLaunchOptions> options, Action<string?> setToken)
    {
        _options = options;
        _setToken = setToken;
        LastChildFill = true;
        this.SetResourceReference(BackgroundProperty, "Habi.Background");

        var title = Label("Assistant", 12, FontWeight.SemiBold, "Habi.Foreground");
        var hint = Label("Enter send · Shift+Enter new line · Ctrl+N new conversation · Esc hide", 12, FontWeight.Normal, "Habi.Description");
        var header = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(title);
        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        headerBorder.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        SetDock(headerBorder, Dock.Top);
        Children.Add(headerBorder);

        _input = new TextBox
        {
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 56,
            MaxHeight = 200,
            PlaceholderText = "Ask the assistant: \"keep only the music in this folder\"…",
        };
        _input.AddHandler(KeyDownEvent, OnInputKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _status = Label(string.Empty, 12, FontWeight.Normal, "Habi.Description");
        _status.VerticalAlignment = VerticalAlignment.Center;
        _stop = new Button { Content = "Stop", IsVisible = false, Margin = new Thickness(8, 0, 0, 0) };
        _stop.Click += (_, _) => Stop();
        var statusRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(_stop, Dock.Right);
        statusRow.Children.Add(_stop);
        statusRow.Children.Add(_status);
        var bottom = new StackPanel { Margin = new Thickness(24, 8, 24, 12) };
        bottom.Children.Add(_input);
        bottom.Children.Add(statusRow);
        var bottomBorder = new Border { Child = bottom, BorderThickness = new Thickness(0, 1, 0, 0) };
        bottomBorder.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        SetDock(bottomBorder, Dock.Bottom);
        Children.Add(bottomBorder);

        _scroll = new ScrollViewer { Content = _log, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Children.Add(_scroll);
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Note("Claude Code runs here with this window's panels and console as its tools. It asks before it changes anything.");
    }

    public event Action? CloseRequested;

    /// <summary>A question is waiting for the user; the window shows the page.</summary>
    public event Action? AttentionNeeded;

    public bool HasSession => _session is not null;

    public void FocusContent()
    {
        var target = _pendingFocus ?? _input;
        target.Focus();
    }

    /// <summary>Ends the session; the window is closing.</summary>
    public void Shutdown() => EndSession();

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            CloseRequested?.Invoke();
        }
        else if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            NewConversation();
        }
    }

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Shift)
        {
            e.Handled = true;
            var at = _input.CaretIndex;
            _input.Text = (_input.Text ?? string.Empty).Insert(at, Environment.NewLine);
            _input.CaretIndex = at + Environment.NewLine.Length;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            SendInput();
        }
        else if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control && _busy && string.IsNullOrEmpty(_input.SelectedText))
        {
            e.Handled = true;
            Stop();
        }
    }

    private void SendInput()
    {
        var text = (_input.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            EnsureSession();
            _session!.Send(text);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Problem(ex.Message);
            EndSession();
            return;
        }

        _input.Text = string.Empty;
        Add(Bubble(text));
        SetBusy(true);
    }

    private void EnsureSession()
    {
        if (_session is { IsRunning: true })
        {
            return;
        }

        EndSession();
        var options = _options();
        var (start, config) = ClaudeLaunch.Build(options);
        _setToken(options.Token);
        var session = ClaudeSession.Start(start, config);
        session.Event += item => Dispatcher.UIThread.Post(() => OnEvent(session, item));
        session.Exited += _ => Dispatcher.UIThread.Post(() =>
        {
            if (_session == session)
            {
                _session = null;
                _setToken(null);
                SetBusy(false);
                Note("The session ended. Your next message starts a new one.");
            }
        });
        _session = session;
        var provider = options.Provider == AssistantProvider.Bedrock
            ? $"Bedrock{(options.AwsProfile is { Length: > 0 } p ? $", profile {p}" : string.Empty)}{(options.AwsRegion is { Length: > 0 } r ? $", {r}" : string.Empty)}"
            : "Claude Code";
        Note($"Started in {options.WorkingDirectory} ({provider}).");
    }

    private void NewConversation()
    {
        EndSession();
        _log.Children.Clear();
        _tools.Clear();
        _allowedTools.Clear();
        _pendingFocus = null;
        Note("A new conversation. The next message starts it in the active panel's folder.");
        _input.Focus();
    }

    private void EndSession()
    {
        var session = _session;
        _session = null;
        _setToken(null);
        SetBusy(false);
        session?.Dispose();
    }

    private void Stop()
    {
        try
        {
            _session?.Interrupt();
            Note("Stopping…");
        }
        catch (InvalidOperationException)
        {
            EndSession();
        }
    }

    private void OnEvent(ClaudeSession session, AssistantEvent item)
    {
        if (session != _session)
        {
            return;
        }

        switch (item)
        {
            case AssistantStarted started:
                _model = started.Model;
                SetBusy(_busy);
                break;
            case AssistantText text:
                Add(Reply(text.Text));
                break;
            case AssistantToolUse use:
                var line = Label($"▸ {ToolName(use.Tool)}  {use.Summary}", 12, FontWeight.Normal, "Habi.Description");
                line.FontFamily = new FontFamily(Theming.Fonts.Mono);
                line.TextWrapping = TextWrapping.Wrap;
                _tools[use.Id] = line;
                Add(line);
                break;
            case AssistantToolResult result when _tools.TryGetValue(result.ToolUseId, out var toolLine):
                if (result.IsError)
                {
                    toolLine.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Error");
                    toolLine.Text += $"\n  {FirstLines(result.Text, 3)}";
                }

                break;
            case AssistantAsks asks:
                Ask(session, asks);
                break;
            case AssistantTurnEnded ended:
                SetBusy(false);
                if (ended.IsError && ended.Text is { Length: > 0 } error)
                {
                    Problem(error);
                }

                var cost = ended.CostUsd is { } usd && usd > 0 ? $" · ${usd:0.###}" : string.Empty;
                _status.Text = $"Done in {TimeSpan.FromMilliseconds(ended.DurationMs ?? 0).TotalSeconds:0.#} s{cost}{ModelSuffix()}";
                break;
            case AssistantProblem problem:
                Problem(problem.Text);
                break;
        }
    }

    private void Ask(ClaudeSession session, AssistantAsks asks)
    {
        if (_allowedTools.Contains(asks.Tool))
        {
            session.Allow(asks);
            return;
        }

        var question = Label($"Claude wants to use {ToolName(asks.Tool)}", 13, FontWeight.SemiBold, "Habi.Foreground");
        // The whole command: what is allowed must be readable in full, however long.
        var whatText = new SelectableTextBlock { Text = asks.Detail, FontSize = 12, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily(Theming.Fonts.Mono) };
        whatText.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
        var what = new ScrollViewer { Content = whatText, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var allow = new Button { Content = "_Allow" };
        var always = new Button { Content = $"Allow {ToolName(asks.Tool)} for this _conversation" };
        var deny = new Button { Content = "_Deny" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(allow);
        buttons.Children.Add(always);
        buttons.Children.Add(deny);
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(question);
        body.Children.Add(what);
        body.Children.Add(buttons);
        var card = new Border { Child = body, Padding = new Thickness(12, 8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        card.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "Habi.Accent");
        Add(card);

        void Answer(bool allowIt, bool rememberTool)
        {
            if (!buttons.IsEnabled)
            {
                return;
            }

            buttons.IsEnabled = false;
            _pendingFocus = null;
            try
            {
                if (rememberTool)
                {
                    _allowedTools.Add(asks.Tool);
                }

                if (allowIt)
                {
                    session.Allow(asks);
                }
                else
                {
                    session.Deny(asks, "The user declined this. Ask what to do instead.");
                }

                question.Text = allowIt ? $"Allowed: {ToolName(asks.Tool)}" : $"Declined: {ToolName(asks.Tool)}";
            }
            catch (InvalidOperationException)
            {
                question.Text = "The session has ended.";
            }

            _input.Focus();
        }

        allow.Click += (_, _) => Answer(true, false);
        always.Click += (_, _) => Answer(true, true);
        deny.Click += (_, _) => Answer(false, false);
        _pendingFocus = allow;
        AttentionNeeded?.Invoke();
        Dispatcher.UIThread.Post(() => allow.Focus(), DispatcherPriority.Input);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _stop.IsVisible = busy;
        if (busy)
        {
            _status.Text = $"Working…{ModelSuffix()}";
        }
    }

    private string ModelSuffix() => _model is { Length: > 0 } model ? $" · {model}" : string.Empty;

    private void Note(string text) => Add(Label(text, 12, FontWeight.Normal, "Habi.Description"));

    private void Problem(string text)
    {
        var line = Label(text, 12, FontWeight.Normal, "Habi.Error");
        line.TextWrapping = TextWrapping.Wrap;
        Add(line);
    }

    private void Add(Control control)
    {
        _log.Children.Add(control);
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private static Border Bubble(string text)
    {
        var block = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, FontSize = 13 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
        var border = new Border { Child = block, Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 8, 0, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "Habi.InputBackground");
        return border;
    }

    private static SelectableTextBlock Reply(string text)
    {
        var block = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
        return block;
    }

    private static TextBlock Label(string text, double size, FontWeight weight, string brush)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    /// <summary>mcp__habilis__run_in_console reads as "habilis: run_in_console".</summary>
    private static string ToolName(string tool) =>
        tool.StartsWith("mcp__", StringComparison.Ordinal) && tool.Split("__") is [_, var server, var name] ? $"{server}: {name}" : tool;

    private static string FirstLines(string text, int count)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n  ", lines.Take(count)) + (lines.Length > count ? " …" : string.Empty);
    }
}
