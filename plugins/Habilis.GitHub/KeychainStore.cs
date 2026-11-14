using System.Diagnostics;

namespace Habilis.GitHub;

/// <summary>
/// The token in the macOS login Keychain (issue #280): a generic password "HabilisTerminal:github" with the GitHub login
/// as its account, visible and removable in Keychain Access. The <c>security</c> tool reads and writes it; the token goes
/// to it on standard input (<c>security -i</c>), never on a command line another process could see. As the item belongs
/// to that tool, an update of the app (a new signature) does not make the Keychain ask again.
/// </summary>
/// <param name="service">The item's name; tests use one of their own.</param>
internal sealed class KeychainStore(string service = CredentialManagerStore.Target) : ITokenStore
{
    private const string Tool = "/usr/bin/security";

    public string? Read()
    {
        var (code, output) = Run(["find-generic-password", "-s", service, "-w"], input: null);
        var token = output.Trim();
        return code == 0 && token.Length > 0 ? token : null;
    }

    public void Write(string login, string token)
    {
        if (!IsPlain(login) || !IsPlain(token))
        {
            throw new HabilisTerminal.Plugins.PluginException("The GitHub sign-in has characters the Keychain tool cannot take.");
        }

        // -U replaces an item that is already there.
        var (code, _) = Run(["-i"], $"add-generic-password -U -s \"{service}\" -a \"{login}\" -l \"{service}\" -w \"{token}\"\n");
        // "security -i" goes on after a failed line, so what was kept is read back.
        if (code != 0 || Read() != token)
        {
            throw new HabilisTerminal.Plugins.PluginException($"The Keychain did not keep the GitHub sign-in (security exited with {code}).");
        }
    }

    public void Delete() => Run(["delete-generic-password", "-s", service], input: null);

    /// <summary>What goes between double quotes in a <c>security -i</c> line unchanged: GitHub logins and tokens.</summary>
    internal static bool IsPlain(string text) => text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private static (int Code, string Output) Run(IEnumerable<string> arguments, string? input)
    {
        var start = new ProcessStartInfo(Tool)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new HabilisTerminal.Plugins.PluginException("The Keychain tool did not start.");
        if (input is not null)
        {
            process.StandardInput.Write(input);
        }

        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.GetAwaiter().GetResult());
    }
}

/// <summary>Elsewhere (Linux) nothing is kept: the token lasts until the app closes, and the GitHub CLI's sign-in is the way to stay signed in.</summary>
internal sealed class MemoryStore : ITokenStore
{
    private string? _token;

    public string? Read() => _token;

    public void Write(string login, string token) => _token = token;

    public void Delete() => _token = null;
}

internal static class TokenStores
{
    public static ITokenStore ForThisSystem() =>
        OperatingSystem.IsWindows() ? new CredentialManagerStore()
        : OperatingSystem.IsMacOS() ? new KeychainStore()
        : new MemoryStore();

    /// <summary>Where the dialogs say the token is kept.</summary>
    public static string Place =>
        OperatingSystem.IsWindows() ? "Windows Credential Manager"
        : OperatingSystem.IsMacOS() ? "the macOS Keychain"
        : "memory until Habilis Terminal closes";
}
