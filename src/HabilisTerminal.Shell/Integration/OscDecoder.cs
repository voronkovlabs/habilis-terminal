using System.Globalization;

namespace HabilisTerminal.Shell.Integration;

/// <summary>Turns OSC payloads into <see cref="ShellEvent"/>s; unrelated payloads yield null.</summary>
public static class OscDecoder
{
    public const string MessagePrefix = "7731;habi;";

    public static ShellEvent? Decode(string payload)
    {
        if (payload.StartsWith("133;", StringComparison.Ordinal))
        {
            return DecodeSemanticPrompt(payload.AsSpan(4));
        }

        if (payload.StartsWith("9;9;", StringComparison.Ordinal))
        {
            var path = Unquote(payload[4..]);
            return path.Length > 0 ? new ShellEvent.DirectoryChanged(path) : null;
        }

        if (payload.StartsWith("7;", StringComparison.Ordinal))
        {
            return FileUriToPath(payload[2..]) is { } path ? new ShellEvent.DirectoryChanged(path) : null;
        }

        if (payload.StartsWith(MessagePrefix, StringComparison.Ordinal))
        {
            var parts = payload[MessagePrefix.Length..].Split(';');
            return parts[0].Length > 0 ? new ShellEvent.Message(parts[0], parts[1..]) : null;
        }

        return null;
    }

    private static ShellEvent? DecodeSemanticPrompt(ReadOnlySpan<char> rest)
    {
        if (rest.IsEmpty)
        {
            return null;
        }

        return rest[0] switch
        {
            'A' => new ShellEvent.PromptStarted(),
            'B' => new ShellEvent.InputStarted(),
            'C' => new ShellEvent.CommandStarted(),
            'D' => new ShellEvent.CommandFinished(ParseExitCode(rest[1..])),
            _ => null,
        };
    }

    private static int? ParseExitCode(ReadOnlySpan<char> rest)
    {
        if (rest.Length < 2 || rest[0] != ';')
        {
            return null;
        }

        var value = rest[1..];
        var end = value.IndexOf(';');
        if (end >= 0)
        {
            value = value[..end];
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    /// <summary>
    /// <c>file://host/C:/dir</c> → <c>C:\dir</c>; <c>file://server/share/dir</c> → <c>\\server\share\dir</c>.
    /// </summary>
    internal static string? FileUriToPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            return Uri.UnescapeDataString(parsed.AbsolutePath); // a POSIX path as it is (issue #268)
        }

        var path = Uri.UnescapeDataString(parsed.AbsolutePath).Replace('/', '\\');
        if (path.Length >= 3 && path[0] == '\\' && char.IsAsciiLetter(path[1]) && path[2] == ':')
        {
            // Local drive: the host part only names this machine.
            return path[1..];
        }

        return parsed.Host.Length > 0 ? $@"\\{parsed.Host}{path}" : null;
    }
}
