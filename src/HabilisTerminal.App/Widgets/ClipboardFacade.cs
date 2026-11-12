namespace HabilisTerminal.App.Widgets;

/// <summary>Clipboard text, synchronously (issue #253), through <see cref="Platform.Desktop.Current"/> (#259).</summary>
internal static class Clipboard
{
    public static bool ContainsText() => Platform.Desktop.Current.ClipboardContainsText();

    public static string GetText() => Platform.Desktop.Current.ClipboardText();

    public static void SetText(string text) => Platform.Desktop.Current.SetClipboardText(text);
}
