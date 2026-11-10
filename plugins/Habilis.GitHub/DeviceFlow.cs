using System.Net.Http.Headers;
using System.Text.Json;
using HabilisTerminal.Plugins;

namespace Habilis.GitHub;

/// <summary>What GitHub gave to show the user: the code to enter and where.</summary>
internal sealed record DeviceCode(string Code, string UserCode, Uri VerificationUri, TimeSpan ExpiresIn, TimeSpan Interval);

/// <summary>
/// Sign-in with GitHub's device flow: GitHub gives a short code, the user enters it on github.com in their browser and
/// approves, and the plugin, asking every few seconds, gets a token. The password never passes through the program.
/// Needs an OAuth App with "Enable Device Flow"; its client ID is not secret and there is no client secret.
/// </summary>
internal sealed class DeviceFlow(HttpClient http, string clientId, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>Private repositories need <c>repo</c>; organizations' repositories need <c>read:org</c>.</summary>
    public const string Scopes = "repo read:org";

    private static readonly Uri CodeUri = new("https://github.com/login/device/code");
    private static readonly Uri TokenUri = new("https://github.com/login/oauth/access_token");

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>Only GitHub's own pages are opened in the browser, whatever a response says.</summary>
    public static bool IsGitHubPage(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort;

    public async Task<DeviceCode> StartAsync(CancellationToken cancellationToken)
    {
        using var json = await PostAsync(CodeUri, new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = Scopes }, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if (Text(root, "error") is { } error)
        {
            throw new PluginException(error switch
            {
                "device_flow_disabled" => "The GitHub OAuth App does not allow device sign-in: tick \"Enable Device Flow\" in its settings on GitHub.",
                "incorrect_client_credentials" => "GitHub does not know this client ID. Check it in the GitHub plugin's settings.",
                _ => $"GitHub refused to start the sign-in: {Text(root, "error_description") ?? error}.",
            });
        }

        if (Text(root, "device_code") is not { } code || Text(root, "user_code") is not { } userCode
            || !Uri.TryCreate(Text(root, "verification_uri"), UriKind.Absolute, out var uri) || !IsGitHubPage(uri))
        {
            throw new PluginException("GitHub's answer to the sign-in did not look right; try again.");
        }

        return new DeviceCode(code, userCode, uri,
            TimeSpan.FromSeconds(Number(root, "expires_in") ?? 900),
            TimeSpan.FromSeconds(Math.Max(Number(root, "interval") ?? 5, 1)));
    }

    /// <summary>Asks until the user approves (the token), denies or the code expires (both a <see cref="PluginException"/>).</summary>
    public async Task<string> WaitForTokenAsync(DeviceCode code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        var interval = code.Interval;
        var deadline = DateTime.UtcNow + code.ExpiresIn;
        while (true)
        {
            await _delay(interval, cancellationToken).ConfigureAwait(false);
            using var json = await PostAsync(TokenUri, new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["device_code"] = code.Code,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }, cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            if (Text(root, "access_token") is { Length: > 0 } token)
            {
                return token;
            }

            switch (Text(root, "error"))
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    // GitHub says how long to wait from now on, or else 5 seconds more.
                    interval = Number(root, "interval") is { } seconds ? TimeSpan.FromSeconds(seconds) : interval + TimeSpan.FromSeconds(5);
                    break;
                case "expired_token":
                    throw new PluginException("The sign-in code expired before it was entered. Open GitHub again to get a new one.");
                case "access_denied":
                    throw new PluginException("The sign-in was cancelled on GitHub.");
                case { } other:
                    throw new PluginException($"GitHub refused the sign-in: {Text(root, "error_description") ?? other}.");
                default:
                    throw new PluginException("GitHub's answer to the sign-in did not look right; try again.");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new PluginException("The sign-in code expired before it was entered. Open GitHub again to get a new one.");
            }
        }
    }

    private async Task<JsonDocument> PostAsync(Uri uri, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PluginException($"Cannot reach GitHub: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new PluginException("GitHub's answer to the sign-in did not look right; try again.", ex);
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
}
