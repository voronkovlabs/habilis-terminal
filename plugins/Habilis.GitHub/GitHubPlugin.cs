using System.Diagnostics;
using System.Text.Json.Nodes;
using HabilisTerminal.Plugins;

namespace Habilis.GitHub;

/// <summary>
/// GitHub in a Habilis Terminal panel (issue #214): owners, repositories, branches and files, read-only. Opens from
/// F11, the drive menu or the command line (<c>gh:</c>, <c>gh:owner/repo</c>, also after <c>cd</c>). Signs in with
/// GitHub's device flow the first time; the token stays in Windows Credential Manager until "Sign out".
/// </summary>
public sealed class GitHubPlugin : Plugin
{
    /// <summary>The Habilis Terminal OAuth App on GitHub (device flow). Not a secret; the settings can replace it.</summary>
    public const string DefaultClientId = "Ov23litdCqb7kVF7RRSs";

    private const string ClientIdKey = "clientId";

    private HttpClient? _http;

    internal ITokenStore Tokens { get; set; } = new CredentialManagerStore();

    internal Func<string, IGitHubApi> Connect { get; set; }

    internal Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }

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

    private async Task<IPanelProvider?> OpenAsync(GitHubLocation location, CancellationToken cancellationToken)
    {
        var token = Tokens.Read() ?? await SignInAsync(cancellationToken).ConfigureAwait(false);
        return token is null ? null : new GitHubPanel(Host, Connect(token), location, () => Tokens.Delete());
    }

    /// <summary>The device flow: the code in a dialog, GitHub in the browser, then waiting for the approval.</summary>
    internal async Task<string?> SignInAsync(CancellationToken cancellationToken)
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
            Process.Start(new ProcessStartInfo(code.VerificationUri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }

        Host.ShowStatus($"Waiting for GitHub: enter the code {code.UserCode} in the browser.");
        var token = await flow.WaitForTokenAsync(code, cancellationToken).ConfigureAwait(false);
        var (login, _) = await Connect(token).RepositoriesAsync(cancellationToken).ConfigureAwait(false);
        Tokens.Write(login, token);
        Host.ShowStatus($"Signed in to GitHub as {login}.");
        return token;
    }

    private async Task SignOutAsync()
    {
        if (Tokens.Read() is null)
        {
            await Host.Dialogs.ShowMessageAsync("GitHub", "You are not signed in to GitHub.").ConfigureAwait(false);
            return;
        }

        if (await Host.Dialogs.ConfirmAsync("Sign out of GitHub", "Forget the GitHub sign-in on this computer?", confirmText: "Sign out").ConfigureAwait(false))
        {
            Tokens.Delete();
            Host.ShowStatus("Signed out of GitHub.");
        }
    }
}
