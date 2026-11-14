using System.Diagnostics;
using System.Text.Json.Nodes;
using HabilisTerminal.Plugins;

namespace Habilis.GitHub;

/// <summary>
/// GitHub in a Habilis Terminal panel (issue #214): owners, repositories, branches and files, read-only. Opens from
/// F11, the drive menu or the command line (<c>gh:</c>, <c>gh:owner/repo</c>, also after <c>cd</c>). Signs in with
/// GitHub's device flow, the GitHub CLI's sign-in or a personal access token; a token stays in Windows Credential
/// Manager, or the macOS Keychain, until "Sign out".
/// </summary>
public sealed class GitHubPlugin : Plugin
{
    /// <summary>The Habilis Terminal OAuth App on GitHub (device flow). Not a secret; the settings can replace it.</summary>
    public const string DefaultClientId = "Ov23litdCqb7kVF7RRSs";

    private const string ClientIdKey = "clientId";
    private const string SignInKey = "signIn";
    private const string GhSignIn = "gh";
    private const string WithCode = "With a code in the browser";
    private const string WithGitHubCli = "With the GitHub CLI's sign-in (gh)";
    private const string WithToken = "With a personal access token";
    private const string CreateToken = "Open GitHub to create a token";
    private const string HaveToken = "I already have a token";

    /// <summary>A new classic token, named and with the scopes the plugin needs ticked.</summary>
    internal const string NewTokenPage = "https://github.com/settings/tokens/new?scopes=repo,read:org&description=Habilis%20Terminal";

    private HttpClient? _http;

    internal ITokenStore Tokens { get; set; } = TokenStores.ForThisSystem();

    internal Func<string, IGitHubApi> Connect { get; set; }

    internal Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }

    /// <summary>The GitHub CLI's token, or null when it is not signed in; a fake in tests.</summary>
    internal Func<CancellationToken, Task<string?>> GitHubCli { get; set; } = GitHubCliToken.ReadAsync;

    /// <summary>Opens a github.com page in the browser; recorded instead in tests.</summary>
    internal Action<string> OpenBrowser { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    public GitHubPlugin() => Connect = token => new GitHubApi(Http, token);

    public override IEnumerable<PluginMenuItem> MenuItems => [new("GitHub", token => OpenAsync(GitHubLocation.Top, token))];

    public override IEnumerable<PluginMenuItem> DriveMenuItems => MenuItems;

    public override IEnumerable<string> Prefixes => [GitHubLocation.Prefix];

    public override IEnumerable<string> AddressCommands => ["cd"];

    public override IEnumerable<PluginCommand> Commands => [new("Sign out of GitHub", null, _ => SignOutAsync())];

    public override bool HasConfiguration => true;

    internal HttpClient Http
    {
        get => _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        set => _http = value;
    }

    public override Task<IPanelProvider?> OpenAddressAsync(string address, CancellationToken cancellationToken) =>
        OpenAsync(GitHubLocation.Parse(address), cancellationToken);

    public override async Task ConfigureAsync(CancellationToken cancellationToken)
    {
        var form = new PluginForm("GitHub",
        [
            new TextField(ClientIdKey, "OAuth App client ID", ClientId)
            {
                Description = "From the OAuth App on GitHub with \"Enable Device Flow\" ticked. Not a secret. Empty: the one built in.",
            },
        ]);
        if (await Host.Dialogs.EditAsync(form).ConfigureAwait(false))
        {
            var settings = Host.Settings.Load();
            settings[ClientIdKey] = form.Field<TextField>(ClientIdKey).Value.Trim();
            Host.Settings.Save(settings);
        }
    }

    public override Task ShutdownAsync()
    {
        _http?.Dispose();
        _http = null;
        return Task.CompletedTask;
    }

    private string ClientId =>
        Host.Settings.Load()[ClientIdKey]?.GetValue<string>() is { Length: > 0 } id ? id : DefaultClientId;

    /// <summary>Signed in through the GitHub CLI: its token is asked for on every open and never kept here.</summary>
    private bool UsesGitHubCli => Host.Settings.Load()[SignInKey]?.GetValue<string>() == GhSignIn;

    private async Task<IPanelProvider?> OpenAsync(GitHubLocation location, CancellationToken cancellationToken)
    {
        var token = UsesGitHubCli
            ? await GitHubCliTokenAsync(cancellationToken).ConfigureAwait(false)
            : Tokens.Read() ?? await SignInAsync(cancellationToken).ConfigureAwait(false);
        return token is null ? null : new GitHubPanel(Host, Connect(token), location, Forget);
    }

    /// <summary>
    /// The ways in (issue #224). A code in the browser signs in the Habilis Terminal app, which organizations that
    /// restrict OAuth apps keep from their repositories; the GitHub CLI's sign-in and a personal access token are not
    /// held back that way.
    /// </summary>
    internal async Task<string?> SignInAsync(CancellationToken cancellationToken)
    {
        var way = await Host.Dialogs.ChooseAsync("Sign in to GitHub", [WithCode, WithGitHubCli, WithToken], WithCode).ConfigureAwait(false);
        return way switch
        {
            WithCode => await SignInWithCodeAsync(cancellationToken).ConfigureAwait(false),
            WithGitHubCli => await SignInWithGitHubCliAsync(cancellationToken).ConfigureAwait(false),
            WithToken => await SignInWithTokenAsync(cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    /// <summary>The device flow: the code in a dialog, GitHub in the browser, then waiting for the approval.</summary>
    private async Task<string?> SignInWithCodeAsync(CancellationToken cancellationToken)
    {
        var clientId = ClientId;
        if (clientId.Length == 0)
        {
            throw new PluginException("The GitHub plugin has no OAuth App client ID yet. Put it in the plugin's settings (F9 > Options > Plugins, GitHub).");
        }

        var flow = new DeviceFlow(Http, clientId, Delay);
        var code = await flow.StartAsync(cancellationToken).ConfigureAwait(false);
        var go = await Host.Dialogs.ConfirmAsync("Sign in to GitHub",
            $"Your code:  {code.UserCode}\n\n\"Open GitHub\" opens {code.VerificationUri} in your browser. Enter the code there and approve Habilis Terminal; this panel then opens by itself. The program never sees your password.",
            confirmText: "Open GitHub").ConfigureAwait(false);
        if (!go)
        {
            return null;
        }

        if (DeviceFlow.IsGitHubPage(code.VerificationUri))
        {
            OpenBrowser(code.VerificationUri.AbsoluteUri);
        }

        Host.ShowStatus($"Waiting for GitHub: enter the code {code.UserCode} in the browser.");
        var token = await flow.WaitForTokenAsync(code, cancellationToken).ConfigureAwait(false);
        return await KeepAsync(token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The GitHub CLI's own sign-in, used as it is: nothing is copied, so "gh auth switch" or a new login follow.</summary>
    private async Task<string?> SignInWithGitHubCliAsync(CancellationToken cancellationToken)
    {
        var token = await GitHubCliTokenAsync(cancellationToken).ConfigureAwait(false);
        var (login, _) = await Connect(token).RepositoriesAsync(cancellationToken).ConfigureAwait(false);
        var settings = Host.Settings.Load();
        settings[SignInKey] = GhSignIn;
        Host.Settings.Save(settings);
        Host.ShowStatus($"Signed in to GitHub as {login}, through the GitHub CLI.");
        return token;
    }

    /// <summary>
    /// A classic personal access token (issue #226): GitHub's page for a new one opens with the name and the scopes
    /// filled in, then the token is pasted.
    /// </summary>
    private async Task<string?> SignInWithTokenAsync(CancellationToken cancellationToken)
    {
        var first = await Host.Dialogs.ChooseAsync("GitHub token", [CreateToken, HaveToken], CreateToken).ConfigureAwait(false);
        if (first is null)
        {
            return null;
        }

        if (first == CreateToken)
        {
            OpenBrowser(NewTokenPage);
        }

        var token = (await Host.Dialogs.InputSecretAsync("GitHub token",
            (first == CreateToken
                ? "GitHub's page for a new token is open in your browser, with the name and the boxes already set.\n"
                : "A classic token with the \"repo\" and \"read:org\" boxes ticked.\n") +
            "1. On that page press \"Generate token\" at the bottom.\n" +
            "2. Copy the token: the line that starts with ghp_. GitHub shows it only once.\n" +
            "3. If your organization uses single sign-on, press \"Configure SSO\" next to the token and authorize it.\n" +
            $"4. Paste the token here and press OK. It is kept in {TokenStores.Place}.").ConfigureAwait(false))?.Trim();
        return string.IsNullOrEmpty(token) ? null : await KeepAsync(token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A token that GitHub accepts, kept in Credential Manager or the Keychain under the login it belongs to.</summary>
    private async Task<string> KeepAsync(string token, CancellationToken cancellationToken)
    {
        string login;
        try
        {
            (login, _) = await Connect(token).RepositoriesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SignInExpiredException)
        {
            throw new PluginException("GitHub does not accept this token. Check that it is whole and has not expired.");
        }

        Tokens.Write(login, token);
        Host.ShowStatus($"Signed in to GitHub as {login}.");
        return token;
    }

    private async Task<string> GitHubCliTokenAsync(CancellationToken cancellationToken)
    {
        var token = await GitHubCli(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(token)
            ? throw new PluginException("The GitHub CLI is not signed in. Run \"gh auth login\" in the console, then open GitHub again.")
            : token.Trim();
    }

    /// <summary>Forgets every way in: the kept token and the choice of the GitHub CLI.</summary>
    private void Forget()
    {
        Tokens.Delete();
        var settings = Host.Settings.Load();
        if (settings.Remove(SignInKey))
        {
            Host.Settings.Save(settings);
        }
    }

    private async Task SignOutAsync()
    {
        if (Tokens.Read() is null && !UsesGitHubCli)
        {
            await Host.Dialogs.ShowMessageAsync("GitHub", "You are not signed in to GitHub.").ConfigureAwait(false);
            return;
        }

        var message = UsesGitHubCli
            ? "Stop using the GitHub CLI's sign-in here? The CLI itself stays signed in."
            : "Forget the GitHub sign-in on this computer?";
        if (await Host.Dialogs.ConfirmAsync("Sign out of GitHub", message, confirmText: "Sign out").ConfigureAwait(false))
        {
            Forget();
            Host.ShowStatus("Signed out of GitHub.");
        }
    }
}

/// <summary>Asks the GitHub CLI for its token: <c>gh auth token</c>, with no window.</summary>
internal static class GitHubCliToken
{
    public static async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("gh", "auth token")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new PluginException("The GitHub CLI (gh) did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new PluginException("The GitHub CLI (gh) is not installed, or not on PATH. Install it from cli.github.com, or sign in another way.", ex);
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill();
                throw new PluginException("The GitHub CLI did not answer in time.");
            }

            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
        }
    }
}
