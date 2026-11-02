using Amazon.Runtime.CredentialManagement;

namespace Habilis.Aws.Common;

/// <summary>Profiles of the standard AWS config and credentials files (or a custom credentials file).</summary>
public static class AwsProfiles
{
    /// <summary>Profile names, sorted; "default" first when present.</summary>
    public static IReadOnlyList<string> List(string? credentialsFile = null)
    {
        try
        {
            var chain = credentialsFile is { Length: > 0 } file ? new CredentialProfileStoreChain(file) : new CredentialProfileStoreChain();
            return chain.ListProfiles()
                .Select(p => p.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n == "default" ? 0 : 1)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The region set in the profile, or null.</summary>
    public static string? RegionOf(string profile, string? credentialsFile = null)
    {
        var chain = credentialsFile is { Length: > 0 } file ? new CredentialProfileStoreChain(file) : new CredentialProfileStoreChain();
        return chain.TryGetProfile(profile, out var found) ? found.Region?.SystemName : null;
    }
}
