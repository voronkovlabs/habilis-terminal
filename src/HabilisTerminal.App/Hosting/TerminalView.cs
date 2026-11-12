using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using HabilisTerminal.Terminal;

namespace HabilisTerminal.App.Hosting;

/// <summary>The colours a console draws with: 16 ANSI colours plus its defaults.</summary>
public sealed record TerminalPalette(Color Foreground, Color Background, Color Selection, Color Cursor, IReadOnlyList<Color> Ansi)
{
    public static readonly TerminalPalette Default = new(
        Color.FromRgb(204, 204, 204), Color.FromRgb(12, 12, 12), Color.FromArgb(0x60, 0x26, 0x4F, 0x78), Color.FromRgb(204, 204, 204),
        [.. new uint[]
        {
            0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
            0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2,
        }.Select(c => Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c))]);
}

/// <summary>
/// The console (issue #253): draws a <see cref="TerminalEmulator"/>'s screen, turns keys into what the console reads,
/// selects with the mouse and scrolls back with the wheel. It replaces the native Windows Terminal control, so there is
/// no child window: focus, popups and zoom are plain Avalonia.
/// </summary>
public sealed class TerminalView : Control
{
    private const int WheelLines = 3;

    private readonly TerminalEmulator _emulator;
    private readonly DispatcherTimer _blink;
    private TerminalPalette _palette = TerminalPalette.Default;
    private Typeface _typeface;
    private Typeface _boldTypeface;
    private string _fontFamily = "Cascadia Mono";
    private double _fontSize = 14;
    private Size _cell = new(8, 16);
    private int _scroll; // lines scrolled back from the live screen
    private long _seenLines; // LineCount + DroppedLines when last drawn, to keep a scrolled-back view in place
    private int _invalidatePosted;
    private bool _cursorOn = true;
    private (long Line, int Column)? _anchor;
    private (long Line, int Column) _caret;
    private bool _selecting;
    private double _wheelRest;

    public TerminalView(TerminalEmulator emulator)
    {
        _emulator = emulator;
        _typeface = new Typeface(Theming.Fonts.MonoWith(_fontFamily));
        _boldTypeface = new Typeface(Theming.Fonts.MonoWith(_fontFamily), FontStyle.Normal, FontWeight.Bold);
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        UpdateCell();
        emulator.Changed += OnEmulatorChanged;
        _blink = new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Background, (_, _) =>
        {
            _cursorOn = !_cursorOn;
            InvalidateVisual();
        });
        GotFocus += (_, _) => RestartBlink();
        LostFocus += (_, _) => InvalidateVisual();
    }

    /// <summary>What the view sends to the console: keys, text, replies.</summary>
    public event Action<string>? Input;

    /// <summary>The grid changed size: the console must be resized to it.</summary>
    public event Action<int, int>? GridResized;

    public TerminalEmulator Emulator => _emulator;

    public Size CellSize => _cell;

    public int Columns => _emulator.Columns;

    public int Rows => _emulator.Rows;

    /// <summary>The cell size a font gives, in device-independent pixels.</summary>
    public static Size MeasureCell(string fontFamily, double fontSize)
    {
        var text = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Theming.Fonts.MonoWith(fontFamily)), fontSize, null);
        return new Size(Math.Max(1, Math.Round(text.WidthIncludingTrailingWhitespace, 2)), Math.Max(1, Math.Ceiling(text.Height)));
    }

    public void SetTheme(TerminalPalette palette, string fontFamily, double fontSize)
    {
        _palette = palette;
        if (fontFamily != _fontFamily || Math.Abs(fontSize - _fontSize) > 0.01)
        {
            _fontFamily = fontFamily;
            _fontSize = fontSize;
            _typeface = new Typeface(Theming.Fonts.MonoWith(fontFamily));
            _boldTypeface = new Typeface(Theming.Fonts.MonoWith(fontFamily), FontStyle.Normal, FontWeight.Bold);
            UpdateCell();
            ResizeGrid(Bounds.Size);
        }

        InvalidateVisual();
    }

    public void SendText(string text) => Input?.Invoke(text);

    /// <summary>The selected text, or empty.</summary>
    public string GetSelectedText()
    {
        if (SelectionRange() is not var ((startLine, startColumn), (endLine, endColumn)))
        {
            return string.Empty;
        }

        var dropped = _emulator.DroppedLines;
        return _emulator.TextBetween((int)(startLine - dropped), startColumn, (int)(endLine - dropped), endColumn + 1);
    }

    public bool HasSelection => _anchor is not null && _anchor != _caret;

    public void ClearSelection()
    {
        _anchor = null;
        InvalidateVisual();
    }

    /// <summary>Back to the live screen (typing does this).</summary>
    public void ScrollToBottom()
    {
        if (_scroll != 0)
        {
            _scroll = 0;
            InvalidateVisual();
        }
    }

    private void UpdateCell() => _cell = MeasureCell(_fontFamily, _fontSize);

    private void RestartBlink()
    {
        _cursorOn = true;
        _blink.Stop();
        if (IsFocused && _emulator.CursorBlinks)
        {
            _blink.Start();
        }

        InvalidateVisual();
    }

    private void OnEmulatorChanged()
    {
        // Output arrives on the reader's thread: one redraw per burst.
        if (Interlocked.Exchange(ref _invalidatePosted, 1) == 0)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _invalidatePosted, 0);
                InvalidateVisual();
            }, DispatcherPriority.Render);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ResizeGrid(e.NewSize);
    }

    private void ResizeGrid(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var columns = Math.Max(1, (int)Math.Floor(size.Width / _cell.Width));
        var rows = Math.Max(1, (int)Math.Floor(size.Height / _cell.Height));
        if (columns != _emulator.Columns || rows != _emulator.Rows)
        {
            _emulator.Resize(columns, rows);
            GridResized?.Invoke(columns, rows);
        }
    }

    // ----- Drawing -----

    private IBrush BrushOf(TermColor color, bool foreground, bool bold)
    {
        Color value;
        if (color.IsDefault)
        {
            value = foreground ? _palette.Foreground : _palette.Background;
        }
        else if (color.IsRgb)
        {
            value = Color.FromRgb(color.R, color.G, color.B);
        }
        else
        {
            var index = color.Index;
            if (bold && foreground && index < 8)
            {
                index += 8;
            }

            value = index < 16 ? _palette.Ansi[index] : Xterm256(index);
        }

        return Brushes(value);
    }

    private readonly Dictionary<Color, IBrush> _brushes = [];

    private IBrush Brushes(Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new ImmutableSolidColorBrush(color);
            _brushes[color] = brush;
        }

        return brush;
    }

    private static Color Xterm256(int index)
    {
        if (index >= 232)
        {
            var gray = (byte)(8 + ((index - 232) * 10));
            return Color.FromRgb(gray, gray, gray);
        }

        index -= 16;
        static byte Level(int v) => (byte)(v == 0 ? 0 : 55 + (v * 40));
        return Color.FromRgb(Level(index / 36), Level(index / 6 % 6), Level(index % 6));
    }

    public override void Render(DrawingContext context)
    {
        using var measure = Core.Diagnostics.Perf.Measure("console.render");
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes(_palette.Background), bounds);
        var selection = SelectionRange();
        lock (_emulator.SyncRoot)
        {
            var total = _emulator.LineCount + _emulator.DroppedLines;
            if (_scroll > 0 && total > _seenLines)
            {
                // New output while scrolled back: the view stays on what it shows.
                _scroll += (int)(total - _seenLines);
            }

            _seenLines = total;
            _scroll = Math.Clamp(_scroll, 0, _emulator.ScrollbackCount);
            var first = _emulator.ScrollbackCount - _scroll;
            for (var row = 0; row < _emulator.Rows; row++)
            {
                var index = first + row;
                if (index < 0 || index >= _emulator.LineCount)
                {
                    continue;
                }

                DrawLine(context, _emulator.Line(index), row, index + _emulator.DroppedLines, selection);
            }

            if (_scroll == 0 && _emulator.CursorVisible && (_cursorOn || !IsFocused))
            {
                DrawCursor(context);
            }
        }
    }

    private void DrawLine(DrawingContext context, TermLine line, int row, long absolute, ((long Line, int Column) Start, (long Line, int Column) End)? selection)
    {
        var cells = line.Cells;
        var y = row * _cell.Height;
        var reverse = _emulator.ReverseVideo;

        // Backgrounds in runs of one colour.
        var runStart = 0;
        IBrush? runBrush = null;
        for (var x = 0; x <= cells.Length; x++)
        {
            IBrush? brush = null;
            if (x < cells.Length)
            {
                var style = cells[x].Style;
                var inverse = style.Flags.HasFlag(TextAttributes.Inverse) ^ reverse;
                var background = inverse ? BrushOf(style.Foreground, true, false) : style.Background.IsDefault ? null : BrushOf(style.Background, false, false);
                brush = background;
            }

            if (x == cells.Length || !Equals(brush, runBrush))
            {
                if (runBrush is not null)
                {
                    context.FillRectangle(runBrush, new Rect(runStart * _cell.Width, y, (x - runStart) * _cell.Width, _cell.Height));
                }

                runStart = x;
                runBrush = brush;
            }
        }

        if (selection is var ((startLine, startColumn), (endLine, endColumn)) && absolute >= startLine && absolute <= endLine)
        {
            var from = absolute == startLine ? startColumn : 0;
            var to = absolute == endLine ? endColumn + 1 : cells.Length;
            context.FillRectangle(Brushes(_palette.Selection), new Rect(from * _cell.Width, y, Math.Max(0, to - from) * _cell.Width, _cell.Height));
        }

        // Text in runs of one style; anything outside ASCII is placed cell by cell, as fallback fonts differ in width.
        var text = new StringBuilder();
        var textStart = 0;
        CellStyle? textStyle = null;
        for (var x = 0; x <= cells.Length; x++)
        {
            var cell = x < cells.Length ? cells[x] : default;
            var simple = x < cells.Length && (cell.Text is null || (cell.Text.Length == 1 && cell.Text[0] < 0x80));
            if (x == cells.Length || !simple || cell.Style != textStyle)
            {
                if (text.Length > 0 && textStyle is { } style)
                {
                    DrawText(context, text.ToString(), textStart, y, style);
                }

                text.Clear();
                textStart = x;
                textStyle = x < cells.Length ? cell.Style : null;
            }

            if (x == cells.Length)
            {
                break;
            }

            if (!simple)
            {
                if (!cell.IsWideTail && cell.Text is { } wide)
                {
                    DrawText(context, wide, x, y, cell.Style);
                }

                textStart = x + 1;
                textStyle = null;
                continue;
            }

            text.Append(cell.Text ?? " ");
        }
    }

    private void DrawText(DrawingContext context, string text, int column, double y, CellStyle style)
    {
        if (string.IsNullOrWhiteSpace(text) && !style.Flags.HasFlag(TextAttributes.Underline) && !style.Flags.HasFlag(TextAttributes.Strike))
        {
            return;
        }

        var bold = style.Flags.HasFlag(TextAttributes.Bold);
        var inverse = style.Flags.HasFlag(TextAttributes.Inverse) ^ _emulator.ReverseVideo;
        var foreground = inverse ? (style.Background.IsDefault ? Brushes(_palette.Background) : BrushOf(style.Background, false, false)) : BrushOf(style.Foreground, true, bold);
        if (style.Flags.HasFlag(TextAttributes.Hidden))
        {
            return;
        }

        var typeface = bold ? _boldTypeface : _typeface;
        if (style.Flags.HasFlag(TextAttributes.Italic))
        {
            typeface = new Typeface(typeface.FontFamily, FontStyle.Italic, typeface.Weight);
        }

        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, _fontSize, foreground);
        if (style.Flags.HasFlag(TextAttributes.Faint))
        {
            using (context.PushOpacity(0.6))
            {
                context.DrawText(formatted, new Point(column * _cell.Width, y));
            }
        }
        else
        {
            context.DrawText(formatted, new Point(column * _cell.Width, y));
        }

        var pen = new Pen(foreground, 1);
        if (style.Flags.HasFlag(TextAttributes.Underline))
        {
            var underline = y + _cell.Height - 1.5;
            context.DrawLine(pen, new Point(column * _cell.Width, underline), new Point((column + text.Length) * _cell.Width, underline));
        }

        if (style.Flags.HasFlag(TextAttributes.Strike))
        {
            var middle = y + (_cell.Height / 2);
            context.DrawLine(pen, new Point(column * _cell.Width, middle), new Point((column + text.Length) * _cell.Width, middle));
        }
    }

    private void DrawCursor(DrawingContext context)
    {
        var x = _emulator.CursorX * _cell.Width;
        var y = _emulator.CursorY * _cell.Height;
        var brush = Brushes(_palette.Cursor);
        if (!IsFocused)
        {
            context.DrawRectangle(new Pen(brush, 1), new Rect(x + 0.5, y + 0.5, _cell.Width - 1, _cell.Height - 1));
            return;
        }

        switch (_emulator.CursorShape)
        {
            case CursorShape.Block:
                using (context.PushOpacity(0.7))
                {
                    context.FillRectangle(brush, new Rect(x, y, _cell.Width, _cell.Height));
                }

                break;
            case CursorShape.Underline:
                context.FillRectangle(brush, new Rect(x, y + _cell.Height - 2, _cell.Width, 2));
                break;
            default:
                context.FillRectangle(brush, new Rect(x, y, 2, _cell.Height));
                break;
        }
    }

    // ----- Keys -----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        if (TerminalKeys.Encode(e.Key, e.KeyModifiers, e.KeySymbol, _emulator) is { } sequence)
        {
            e.Handled = true;
            Send(sequence);
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl))
        {
            return;
        }

        e.Handled = true;
        Send(e.Text);
    }

    private void Send(string text)
    {
        ScrollToBottom();
        if (HasSelection)
        {
            ClearSelection();
        }

        RestartBlink();
        Input?.Invoke(text);
    }

    // ----- Mouse -----

    private (long Line, int Column) CellAt(Point point)
    {
        lock (_emulator.SyncRoot)
        {
            var row = Math.Clamp((int)Math.Floor(point.Y / _cell.Height), 0, _emulator.Rows - 1);
            var column = Math.Clamp((int)Math.Floor(point.X / _cell.Width), 0, _emulator.Columns - 1);
            var first = _emulator.ScrollbackCount - _scroll;
            return (first + row + _emulator.DroppedLines, column);
        }
    }

    private ((long Line, int Column) Start, (long Line, int Column) End)? SelectionRange()
    {
        if (_anchor is not { } anchor || anchor == _caret)
        {
            return null;
        }

        return anchor.CompareTo(_caret) < 0 ? (anchor, _caret) : (_caret, anchor);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        var cell = CellAt(point.Position);
        switch (e.ClickCount)
        {
            case 2:
                SelectWord(cell);
                break;
            case >= 3:
                _anchor = (cell.Line, 0);
                _caret = (cell.Line, Math.Max(0, _emulator.Columns - 1));
                break;
            default:
                _anchor = cell;
                _caret = cell;
                _selecting = true;
                e.Pointer.Capture(this);
                break;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_selecting)
        {
            _caret = CellAt(e.GetPosition(this));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Handled || e.Delta.Y == 0)
        {
            return;
        }

        lock (_emulator.SyncRoot)
        {
            // A notch is 1; a fast wheel or a touchpad sends more, or fractions, in one event.
            _wheelRest += e.Delta.Y * WheelLines;
            var lines = (int)Math.Truncate(_wheelRest);
            _wheelRest -= lines;
            _scroll = Math.Clamp(_scroll + lines, 0, _emulator.ScrollbackCount);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    private void SelectWord((long Line, int Column) cell)
    {
        lock (_emulator.SyncRoot)
        {
            var index = (int)(cell.Line - _emulator.DroppedLines);
            if (index < 0 || index >= _emulator.LineCount)
            {
                return;
            }

            var cells = _emulator.Line(index).Cells;
            static bool IsWord(Cell c) => c.Text is { Length: > 0 } t && !char.IsWhiteSpace(t[0]) && "\"'()[]{}<>|,;`".IndexOf(t[0], StringComparison.Ordinal) < 0;
            if (!IsWord(cells[cell.Column]))
            {
                _anchor = cell;
                _caret = cell;
                return;
            }

            var from = cell.Column;
            while (from > 0 && (IsWord(cells[from - 1]) || cells[from - 1].IsWideTail))
            {
                from--;
            }

            var to = cell.Column;
            while (to < cells.Length - 1 && (IsWord(cells[to + 1]) || cells[to + 1].IsWideTail))
            {
                to++;
            }

            _anchor = (cell.Line, from);
            _caret = (cell.Line, to);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blink.Stop();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (_emulator.FocusEvents)
        {
            Input?.Invoke("\u001b[I");
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (_emulator.FocusEvents)
        {
            Input?.Invoke("\u001b[O");
        }
    }
}
