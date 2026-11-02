namespace Habilis.Aws.Common;

/// <summary>AWS commercial regions offered when switching region (the user can type any other).</summary>
public static class AwsRegions
{
    public static IReadOnlyList<string> Commercial { get; } =
    [
        "us-east-1", "us-east-2", "us-west-1", "us-west-2",
        "ca-central-1", "ca-west-1", "mx-central-1", "sa-east-1",
        "eu-west-1", "eu-west-2", "eu-west-3", "eu-central-1", "eu-central-2", "eu-north-1", "eu-south-1", "eu-south-2",
        "il-central-1", "me-central-1", "me-south-1", "af-south-1",
        "ap-east-1", "ap-east-2", "ap-south-1", "ap-south-2", "ap-southeast-1", "ap-southeast-2", "ap-southeast-3",
        "ap-southeast-4", "ap-southeast-5", "ap-southeast-7", "ap-northeast-1", "ap-northeast-2", "ap-northeast-3",
    ];

    /// <summary>A plausible region name ("eu-west-1"), so a typo is caught before a request.</summary>
    public static bool IsValid(string region) =>
        region.Length is > 4 and < 32 && region.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') && region.Contains('-', StringComparison.Ordinal);
}
