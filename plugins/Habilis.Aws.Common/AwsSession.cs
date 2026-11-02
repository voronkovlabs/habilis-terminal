using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.Common;

/// <summary>
/// Where an AWS plugin connects: a profile from the standard shared config and credentials files (role and SSO
/// profiles included) and a region. Credentials live in memory only; nothing here is ever written anywhere.
/// </summary>
public sealed record AwsSession(string Profile, string Region, string? CredentialsFile = null, int TimeoutSeconds = 30)
{
    private AWSCredentials? _credentials;

    public RegionEndpoint Endpoint => RegionEndpoint.GetBySystemName(Region);

    /// <summary>The profile's credentials, resolved once per session (the SDK refreshes temporary ones itself).</summary>
    /// <exception cref="PluginException">The profile does not exist or has no usable credentials.</exception>
    public AWSCredentials Credentials => _credentials ??= Resolve();

    /// <summary>A client config for this region with the request timeout.</summary>
    public T Configure<T>(T config)
        where T : ClientConfig
    {
        ArgumentNullException.ThrowIfNull(config);
        config.RegionEndpoint = Endpoint;
        config.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);
        config.MaxErrorRetry = 3;
        return config;
    }

    /// <summary>What kind of credentials the profile gives, for the info panel; never the secrets themselves.</summary>
    public string CredentialsDescription
    {
        get
        {
            try
            {
                return Credentials switch
                {
                    RefreshingAWSCredentials => "temporary, refreshed automatically",
                    SessionAWSCredentials => "session token",
                    BasicAWSCredentials => "long-term access keys",
                    _ => Credentials.GetType().Name,
                };
            }
            catch (PluginException)
            {
                return "not available";
            }
        }
    }

    private AWSCredentials Resolve()
    {
        var chain = CredentialsFile is { Length: > 0 } file ? new CredentialProfileStoreChain(file) : new CredentialProfileStoreChain();
        if (!chain.TryGetProfile(Profile, out var profile))
        {
            throw new PluginException($"The AWS profile \"{Profile}\" was not found in the AWS config or credentials file.");
        }

        try
        {
            return AwsClients.Own(profile.GetAWSCredentials(chain), this);
        }
        catch (Exception ex) when (ex is AmazonClientException or InvalidOperationException or ArgumentException)
        {
            throw new PluginException($"The AWS profile \"{Profile}\" has no usable credentials: {ex.Message}", ex);
        }
    }
}
