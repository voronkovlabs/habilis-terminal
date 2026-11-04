using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using HabilisTerminal.App.Panels;
using HabilisTerminal.Core.Operations;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// Non-modal progress of running operations (SPEC §8): VS Code-like notification cards in the bottom-right corner,
/// each with a progress bar, Pause / Resume and Cancel. Shown in a popup so they stay above the terminal.
/// </summary>
public sealed class OperationToasts
{
    private const double CardWidth = 380;
    private readonly Window _owner;
    private readonly FrameworkElement _target;
    private readonly Popup _popup;
    private readonly StackPanel _stack = new();

    public OperationToasts(Window owner, FrameworkElement target)
    {
        _owner = owner;
        _target = target;
        _popup = new Popup
        {
            Child = _stack,
            PlacementTarget = target,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            StaysOpen = true,
        };
        _stack.SizeChanged += (_, _) => Reposition();
        target.SizeChanged += (_, _) => Reposition();
        owner.LocationChanged += (_, _) => Reposition();
        owner.StateChanged += (_, _) => _popup.IsOpen = _stack.Children.Count > 0 && owner.WindowState != WindowState.Minimized;
        owner.Activated += (_, _) => _popup.IsOpen = _stack.Children.Count > 0;
        owner.Deactivated += (_, _) => _popup.IsOpen = false;
    }

    public Card Add(string title, OperationControl control, bool canPause = true)
    {
        var card = new Card(title, control, canPause);
        card.Closed += () =>
        {
            _stack.Children.Remove(card.Root);
            _popup.IsOpen = _stack.Children.Count > 0;
        };
        _stack.Children.Add(card.Root);
        _popup.IsOpen = _owner.IsActive;
        Reposition();
        return card;
    }

    /// <summary>
    /// A message card (settings errors and the like) with optional action buttons; replaces an earlier notice with the
    /// same <paramref name="key"/>. Returns an action that closes it.
    /// </summary>
    public Action Notify(string key, string title, string message, params (string Label, Action Run)[] actions)
    {
        foreach (var existing in _stack.Children.OfType<Border>().Where(b => Equals(b.Tag, key)).ToList())
        {
            _stack.Children.Remove(existing);
        }

        var layout = new StackPanel();
        var heading = Card.Text(13, bold: true);
        heading.Text = title;
        var body = Card.Text(12, bold: false);
        body.Text = message;
        body.TextWrapping = TextWrapping.Wrap;
        body.Margin = new Thickness(0, 4, 0, 8);
        body.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Description");
        layout.Children.Add(heading);
        layout.Children.Add(body);

        var root = new Border
        {
            Tag = key,
            Width = CardWidth,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(16, 8, 16, 0),
            Child = layout,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.4, Color = Colors.Black },
        };
        root.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        root.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");

        void Close()
        {
            _stack.Children.Remove(root);
            _popup.IsOpen = _stack.Children.Count > 0;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, run) in actions)
        {
            var button = new Button { Content = label, MinWidth = 64 };
            button.Click += (_, _) =>
            {
                Close();
                run();
            };
            buttons.Children.Add(button);
        }

        var dismiss = new Button { Content = "Close", MinWidth = 64 };
        dismiss.Click += (_, _) => Close();
        buttons.Children.Add(dismiss);
        layout.Children.Add(buttons);

        _stack.Children.Add(root);
        _popup.IsOpen = _owner.IsActive;
        Reposition();
        return Close;
    }

    /// <summary>Closes the notice shown with <paramref name="key"/>, if any.</summary>
    public void Dismiss(string key)
    {
        foreach (var existing in _stack.Children.OfType<Border>().Where(b => Equals(b.Tag, key)).ToList())
        {
            _stack.Children.Remove(existing);
        }

        _popup.IsOpen = _stack.Children.Count > 0 && _owner.IsActive;
    }

    private void Reposition()
    {
        _stack.UpdateLayout();
        _popup.HorizontalOffset = Math.Max(0, _target.ActualWidth - CardWidth - 32);
        _popup.VerticalOffset = Math.Max(0, _target.ActualHeight - _stack.ActualHeight - 16);

        // Popups only move when an offset actually changes.
        _popup.HorizontalOffset += 0.01;
        _popup.HorizontalOffset -= 0.01;
    }

    public sealed class Card
    {
        private readonly TextBlock _title = Text(13, bold: true);
        private readonly TextBlock _current = Text(12, bold: false);
        private readonly TextBlock _counts = Text(12, bold: false);
        private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 6) };
        private readonly Button _pause = new() { Content = "Pause", MinWidth = 64 };
        private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 64 };
        private readonly OperationControl _control;

        internal Card(string title, OperationControl control, bool canPause)
        {
            _control = control;
            _title.Text = title;
            _title.TextTrimming = TextTrimming.CharacterEllipsis;
            _title.ToolTip = title;
            _pause.Visibility = canPause ? Visibility.Visible : Visibility.Collapsed;
            _current.Text = "Preparing…";
            _current.TextTrimming = TextTrimming.CharacterEllipsis;
            _current.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Description");
            _counts.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Description");
            _bar.IsIndeterminate = true;

            _pause.Click += (_, _) =>
            {
                if (_control.IsPaused)
                {
                    _control.Resume();
                    _pause.Content = "Pause";
                }
                else
                {
                    _control.Pause();
                    _pause.Content = "Resume";
                }
            };
            _cancel.Click += (_, _) =>
            {
                _control.Cancel();
                _current.Text = "Cancelling…";
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(_pause);
            buttons.Children.Add(_cancel);

            var footer = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Right);
            footer.Children.Add(buttons);
            footer.Children.Add(_counts);

            var layout = new StackPanel();
            layout.Children.Add(_title);
            layout.Children.Add(_current);
            layout.Children.Add(_bar);
            layout.Children.Add(footer);

            Root = new Border
            {
                Width = CardWidth,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(16, 8, 16, 0),
                Child = layout,
                Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.4, Color = Colors.Black },
            };
            Root.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
            Root.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        }

        internal event Action? Closed;

        internal Border Root { get; }

        public void Queued() => _current.Text = "Waiting for another operation on the same disk…";

        /// <summary>A wait with no known total: the bar keeps moving, the text says what has arrived so far.</summary>
        internal void ShowWaiting(string? counts)
        {
            if (!_control.Token.IsCancellationRequested)
            {
                _current.Text = counts ?? "Please wait…";
            }
        }

        public void Update(OperationProgress progress)
        {
            var culture = CultureInfo.CurrentCulture;
            _bar.IsIndeterminate = progress.TotalBytes == 0 && progress.TotalItems == 0;
            _bar.Value = progress.TotalBytes > 0 ? (double)progress.DoneBytes / progress.TotalBytes
                : progress.TotalItems > 0 ? (double)progress.DoneItems / progress.TotalItems : 0;
            if (progress.CurrentPath is { } current && !_control.Token.IsCancellationRequested)
            {
                _current.Text = Path.GetFileName(current.TrimEnd('\\'));
                _current.ToolTip = current;
            }

            _counts.Text = progress.TotalBytes > 0
                ? $"{PanelColumns.FormatCompactSize(progress.DoneBytes, culture)} of {PanelColumns.FormatCompactSize(progress.TotalBytes, culture)} · {progress.DoneItems:N0}/{progress.TotalItems:N0}"
                : progress.TotalItems > 0 ? $"{progress.DoneItems:N0}/{progress.TotalItems:N0}"
                : progress.DoneItems > 0 ? $"{progress.DoneItems:N0} done"
                : string.Empty;
        }

        public void Close() => Closed?.Invoke();

        internal static TextBlock Text(double size, bool bold)
        {
            var block = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            block.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
            return block;
        }
    }
}
