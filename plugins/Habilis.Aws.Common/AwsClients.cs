using Amazon.Runtime;
using Amazon.Runtime.SharedInterfaces;
using Amazon.RuntimeDependencies;
using Amazon.SecurityToken;
using Amazon.SSO;
using Amazon.SSOOIDC;

namespace Habilis.Aws.Common;

/// <summary>
/// Gives role and SSO profiles this plugin's own STS / SSO clients (issue #61). AWSSDK.Core otherwise finds them by
/// reflection: the first assembly with the right name among all loaded ones, which in Habilis Terminal is often
/// another plugin's copy, from another load context, and unusable here ("Assembly AWSSDK.SecurityToken could not be
/// found or loaded"). Static type references below always bind to the copy next to this plugin.
/// </summary>
internal static class AwsClients
{
    private static readonly Lazy<bool> Registered = new(Register);

    /// <summary>The credentials a profile gives, with role assumption done by this plugin's STS client.</summary>
    public static AWSCredentials Own(AWSCredentials credentials, AwsSession session)
    {
        _ = Registered.Value;
        return credentials is AssumeRoleAWSCredentials role
            ? new AssumeRoleCredentials(Own(role.SourceCredentials, session), role.RoleArn, role.RoleSessionName, role.Options ?? new(), session)
            : credentials;
    }

    /// <summary>
    /// SSO and SSO-OIDC need no AWS credentials, so a registered factory can build them; the registry is per AWSSDK.Core
    /// copy, that is per plugin, and is checked before the reflection fallback. STS is not registered: assuming a role
    /// signs with the source credentials, which only <see cref="AssumeRoleCredentials"/> knows.
    /// </summary>
    private static bool Register()
    {
        GlobalRuntimeDependencyRegistry.Instance.RegisterSSOClient(context =>
        {
            var config = new AmazonSSOConfig { RegionEndpoint = context.SSOClientContextData?.Region };
            if (context.SSOClientContextData?.ProxySettings is { } proxy)
            {
                config.SetWebProxy(proxy);
            }

            return new AmazonSSOClient(new AnonymousAWSCredentials(), config);
        });
        GlobalRuntimeDependencyRegistry.Instance.RegisterSSOOIDCClient(context =>
        {
            var config = new AmazonSSOOIDCConfig { RegionEndpoint = context.SSOOIDCClientContextData?.Region };
            if (context.SSOOIDCClientContextData?.ProxySettings is { } proxy)
            {
                config.SetWebProxy(proxy);
            }

            return new AmazonSSOOIDCClient(new AnonymousAWSCredentials(), config);
        });
        return true;
    }

    /// <summary>What <see cref="AssumeRoleAWSCredentials"/> does, with the STS client of this plugin.</summary>
    private sealed class AssumeRoleCredentials(
        AWSCredentials source, string roleArn, string sessionName, AssumeRoleAWSCredentialsOptions options, AwsSession session)
        : RefreshingAWSCredentials
    {
        protected override CredentialsRefreshState GenerateNewCredentials() =>
            GenerateNewCredentialsAsync().GetAwaiter().GetResult();

        protected override async Task<CredentialsRefreshState> GenerateNewCredentialsAsync()
        {
            var config = session.Configure(new AmazonSecurityTokenServiceConfig());
            if (options.ProxySettings is { } proxy)
            {
                config.SetWebProxy(proxy);
            }

            using var client = new AmazonSecurityTokenServiceClient(source, config);
            var credentials = await ((ICoreAmazonSTS)client)
                .CredentialsFromAssumeRoleAuthenticationAsync(roleArn, sessionName, options).ConfigureAwait(false);
            return new CredentialsRefreshState(credentials, credentials.Expiration);
        }
    }
}
