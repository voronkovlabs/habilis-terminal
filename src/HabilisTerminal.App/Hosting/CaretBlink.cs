using System.Runtime.InteropServices;

namespace HabilisTerminal.App.Hosting;

/// <summary>
/// How the console's cursor blinks, from the Windows settings (Accessibility, Text cursor): the blink rate, and the
/// time after which a caret stops blinking and stays lit. Each blink wakes the renderer for a few frames, which cost
/// about 1% of a core while the app sat idle, so the cursor stops blinking as Windows' own text boxes do.
/// </summary>
internal static class CaretBlink
{
    private const uint Infinite = 0xFFFFFFFF;
    private const uint GetCaretTimeout = 0x2022; // SPI_GETCARETTIMEOUT
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(530);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The time between blinks, or null when the system says not to blink.</summary>
    public static TimeSpan? Interval() => OperatingSystem.IsWindows() ? IntervalOf(GetCaretBlinkTime()) : DefaultInterval;

    /// <summary>How long the cursor blinks after the last input, output or focus change.</summary>
    public static TimeSpan Timeout() =>
        OperatingSystem.IsWindows() && SystemParametersInfo(GetCaretTimeout, 0, out var ms, 0) ? TimeoutOf(ms) : DefaultTimeout;

    /// <summary>GetCaretBlinkTime: INFINITE means no blinking; 0 is a failed call.</summary>
    internal static TimeSpan? IntervalOf(uint ms) => ms switch
    {
        Infinite => null,
        0 => DefaultInterval,
        _ => TimeSpan.FromMilliseconds(Math.Clamp(ms, 100u, 5000u)),
    };

    /// <summary>SPI_GETCARETTIMEOUT: INFINITE means blink for ever; 0 is not a sensible value.</summary>
    internal static TimeSpan TimeoutOf(uint ms) => ms switch
    {
        Infinite => System.Threading.Timeout.InfiniteTimeSpan,
        0 => DefaultTimeout,
        _ => TimeSpan.FromMilliseconds(ms),
    };

    [DllImport("user32.dll")]
    private static extern uint GetCaretBlinkTime();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, out uint value, uint winIni);
}
