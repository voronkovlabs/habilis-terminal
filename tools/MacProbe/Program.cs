using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using HabilisTerminal.Terminal;

namespace MacProbe;

/// <summary>
/// Shows a window with an input box and a screen of the Habilis Terminal emulator, so a macOS CI run can be
/// photographed and typed into (#258). Not part of the app.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<ProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

public sealed class ProbeApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new ProbeWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class ProbeWindow : Window
{
    public ProbeWindow()
    {
        Title = "Habilis Terminal · macOS probe";
        Width = 1000;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var info = new TextBlock
        {
            Text = $"Avalonia on {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version}",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 10),
        };
        var input = new TextBox { PlaceholderText ="Typed by the CI run…", Name = "Input", FontSize = 15 };
        var echo = new TextBlock { FontSize = 14, Margin = new Thickness(0, 8, 0, 12), Foreground = Brushes.LightGreen };
        input.TextChanged += (_, _) => echo.Text = "Received: " + input.Text;

        // The terminal emulator the app's console draws, fed with what a shell would print.
        var emulator = new TerminalEmulator(90, 14);
        emulator.Feed("\u001b[1;34m~/habilis\u001b[0m \u001b[32m❯\u001b[0m ls -la\r\n");
        emulator.Feed("drwxr-xr-x  \u001b[1;34msrc\u001b[0m\r\ndrwxr-xr-x  \u001b[1;34mtests\u001b[0m\r\n-rw-r--r--  README.md  中文 ☺ é\r\n");
        emulator.Feed("\u001b[38;5;208m256 colours\u001b[0m \u001b[38;2;120;200;255mtruecolor\u001b[0m \u001b[7m inverse \u001b[0m \u001b[4munderline\u001b[0m\r\n");
        emulator.Feed("\u001b[1;34m~/habilis\u001b[0m \u001b[32m❯\u001b[0m ");
        var screen = new EmulatorScreen(emulator) { Height = 14 * 18 + 12 };

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Habilis Terminal on macOS: a first look", FontSize = 24, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(info);
        panel.Children.Add(input);
        panel.Children.Add(echo);
        panel.Children.Add(new Border { Child = screen, Background = new SolidColorBrush(Color.FromRgb(18, 18, 18)), CornerRadius = new CornerRadius(6), Padding = new Thickness(8) });
        Content = panel;
        Opened += (_, _) => input.Focus();
    }
}

/// <summary>Draws the emulator's screen cell by cell, as the app's console view does (simplified).</summary>
internal sealed class EmulatorScreen(TerminalEmulator emulator) : Control
{
    private static readonly Color[] Ansi =
    [
        Color.FromRgb(12, 12, 12), Color.FromRgb(197, 15, 31), Color.FromRgb(19, 161, 14), Color.FromRgb(193, 156, 0),
        Color.FromRgb(0, 55, 218), Color.FromRgb(136, 23, 152), Color.FromRgb(58, 150, 221), Color.FromRgb(204, 204, 204),
        Color.FromRgb(118, 118, 118), Color.FromRgb(231, 72, 86), Color.FromRgb(22, 198, 12), Color.FromRgb(249, 241, 165),
        Color.FromRgb(59, 120, 255), Color.FromRgb(180, 0, 158), Color.FromRgb(97, 214, 214), Color.FromRgb(242, 242, 242),
    ];

    private readonly Typeface _mono = new(new FontFamily("Menlo, Cascadia Mono, Consolas, monospace"));

    public override void Render(DrawingContext context)
    {
        var cell = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _mono, 14, null);
        var width = cell.WidthIncludingTrailingWhitespace;
        const double Height = 18;
        for (var row = 0; row < emulator.Rows; row++)
        {
            var cells = emulator.ScreenLine(row).Cells;
            for (var x = 0; x < cells.Length; x++)
            {
                var c = cells[x];
                if (c.Text is not { Length: > 0 } text)
                {
                    continue;
                }

                var style = c.Style;
                var inverse = style.Flags.HasFlag(TextAttributes.Inverse);
                var fore = Colour(style.Foreground, Color.FromRgb(204, 204, 204), style.Flags.HasFlag(TextAttributes.Bold));
                var back = Colour(style.Background, Colors.Transparent, false);
                if (inverse)
                {
                    (fore, back) = (back == Colors.Transparent ? Color.FromRgb(18, 18, 18) : back, fore);
                }

                if (back != Colors.Transparent)
                {
                    context.FillRectangle(new SolidColorBrush(back), new Rect(x * width, row * Height, width, Height));
                }

                var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _mono, 14, new SolidColorBrush(fore));
                if (style.Flags.HasFlag(TextAttributes.Underline))
                {
                    formatted.SetTextDecorations(TextDecorations.Underline);
                }

                context.DrawText(formatted, new Point(x * width, row * Height));
            }
        }

        if (emulator.CursorVisible)
        {
            context.FillRectangle(Brushes.LightGray, new Rect(emulator.CursorX * width, emulator.CursorY * Height, 2, Height));
        }
    }

    private static Color Colour(TermColor color, Color fallback, bool bold) =>
        color.IsDefault ? fallback
        : color.IsRgb ? Color.FromRgb(color.R, color.G, color.B)
        : color.Index < 16 ? Ansi[bold && color.Index < 8 ? color.Index + 8 : color.Index]
        : Color.FromRgb((byte)(color.Index * 7 % 256), (byte)(color.Index * 3 % 256), (byte)(color.Index * 5 % 256));
}
