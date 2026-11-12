using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.App.Platform;

/// <summary>
/// What the app asks of the desktop beyond Avalonia (#259): the Windows build talks to Win32 and the shell
/// (<see cref="WindowsDesktop"/>), the portable one does what plain .NET and Avalonia can (<see cref="PortableDesktop"/>).
/// </summary>
internal interface IDesktop
{
    bool ClipboardContainsText();

    /// <summary>Clipboard text, synchronously; throws <see cref="System.Runtime.InteropServices.ExternalException"/> while another program holds it.</summary>
    string ClipboardText();

    void SetClipboardText(string text);

    /// <summary>The modifier keys held right now.</summary>
    KeyModifiers HeldModifiers { get; }

    /// <summary>The character a key produces in the current keyboard layout, or null.</summary>
    char? CharOf(Key key, bool shift);

    /// <summary>The system's icon for a file (programs, shortcuts), or null to draw the theme's icon.</summary>
    Bitmap? FileIcon(string path, int size);

    /// <summary>The system's context menu for items of one folder; false when there is none or nothing was chosen.</summary>
    bool ShowContextMenu(Window owner, string folder, IReadOnlyList<string> names, PixelPoint screen, Func<string, bool>? intercept);

    /// <summary>Safely ejects a removable drive.</summary>
    void Eject(string root);

    /// <summary>The frame the system draws around the window follows the theme.</summary>
    void SetDarkFrame(Window window, bool dark);

    /// <summary>Puts the window where it was, once it is open.</summary>
    void RestorePlacement(Window window, WindowPlacement placement);

    WindowPlacement? CapturePlacement(Window window);

    /// <summary>Presses a key as the keyboard would, so the focused page handles it as typed.</summary>
    void PressKey(Key key, KeyModifiers modifiers);
}

internal static class Desktop
{
    public static IDesktop Current { get; set; } =
#if WINDOWS
        new WindowsDesktop();
#else
        new PortableDesktop();
#endif
}

/// <summary>
/// The desktop with plain .NET and Avalonia: the clipboard through pbcopy / pbpaste (macOS) or wl-copy / xclip
/// (Linux), modifiers as the last key event left them, no shell menu or icons.
/// </summary>
internal sealed class PortableDesktop : IDesktop
{
    private static KeyModifiers _modifiers;

    public PortableDesktop()
    {
        // Every window's keys and clicks pass here first, handled or not.
        InputElement.KeyDownEvent.AddClassHandler<TopLevel>((_, e) => Track(e.KeyModifiers), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.KeyUpEvent.AddClassHandler<TopLevel>((_, e) => Track(e.KeyModifiers), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerMovedEvent.AddClassHandler<TopLevel>((_, e) => Track(e.KeyModifiers), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((_, e) => Track(e.KeyModifiers), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>The window's key handlers report every key event here, as there is no system call to ask.</summary>
    public static void Track(KeyModifiers modifiers) => _modifiers = modifiers;

    public bool ClipboardContainsText() => ClipboardText().Length > 0;

    public string ClipboardText() =>
        OperatingSystem.IsMacOS() ? Run("pbpaste", [], null)
        : Run("wl-paste", ["--no-newline"], null) is { Length: > 0 } wayland ? wayland
        : Run("xclip", ["-selection", "clipboard", "-o"], null);

    public void SetClipboardText(string text)
    {
        if (OperatingSystem.IsMacOS())
        {
            Run("pbcopy", [], text);
        }
        else if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 })
        {
            Run("wl-copy", [], text);
        }
        else
        {
            Run("xclip", ["-selection", "clipboard"], text);
        }
    }

    public KeyModifiers HeldModifiers => _modifiers;

    public char? CharOf(Key key, bool shift) => key switch
    {
        >= Key.A and <= Key.Z => (char)((shift ? 'A' : 'a') + (key - Key.A)),
        >= Key.D0 and <= Key.D9 when !shift => (char)('0' + (key - Key.D0)),
        _ => null,
    };

    public Bitmap? FileIcon(string path, int size) => null;

    public bool ShowContextMenu(Window owner, string folder, IReadOnlyList<string> names, PixelPoint screen, Func<string, bool>? intercept) => false;

    public void Eject(string root)
    {
        if (OperatingSystem.IsMacOS())
        {
            Run("diskutil", ["eject", root], null);
        }
        else
        {
            Run("udisksctl", ["power-off", "--block-device", root], null);
        }
    }

    public void SetDarkFrame(Window window, bool dark)
    {
        // Avalonia's window follows RequestedThemeVariant itself.
    }

    public void RestorePlacement(Window window, WindowPlacement placement)
    {
        window.Position = new PixelPoint((int)placement.Left, (int)placement.Top);
        window.Width = placement.Width / window.DesktopScaling;
        window.Height = placement.Height / window.DesktopScaling;
        if (placement.Maximized)
        {
            window.Opened += (_, _) => window.WindowState = WindowState.Maximized;
        }
    }

    public WindowPlacement? CapturePlacement(Window window)
    {
        var scale = window.DesktopScaling;
        return new WindowPlacement(window.Position.X, window.Position.Y, window.Bounds.Width * scale, window.Bounds.Height * scale,
            window.WindowState == WindowState.Maximized);
    }

    public void PressKey(Key key, KeyModifiers modifiers)
    {
        if (Keyboard.FocusedElement is not Interactive target)
        {
            return;
        }

        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = target });
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers, Source = target });
    }

    private static string Run(string program, string[] arguments, string? input)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments)
            {
                RedirectStandardInput = input is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return string.Empty;
            }

            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return string.Empty;
        }
    }
}

/// <summary>Light or dark, the accent colour and high contrast as Avalonia reads them from the system (macOS, Linux).</summary>
internal sealed class AvaloniaSystemAppearance : Themes.ISystemAppearance
{
    private static Avalonia.Platform.PlatformColorValues? Values => Application.Current?.PlatformSettings?.GetColorValues();

    public bool AppsUseLightTheme => Values?.ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Light;

    public Themes.ThemeColor? AccentColor =>
        Values?.AccentColor1 is { A: > 0 } accent ? new Themes.ThemeColor(accent.R, accent.G, accent.B) : null;

    public bool IsHighContrast() => Values?.ContrastPreference == Avalonia.Platform.ColorContrastPreference.High;
}
