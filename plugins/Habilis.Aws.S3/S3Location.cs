namespace Habilis.Aws.S3;

/// <summary>
/// A place in S3: the bucket list (no bucket), or a bucket and a key prefix ("" at the bucket root, else ending in
/// "/"). As an address: "s3:" for the bucket list, "s3://bucket/prefix/" otherwise.
/// </summary>
internal readonly record struct S3Location(string? Bucket, string Prefix)
{
    public static S3Location Buckets { get; } = new(null, string.Empty);

    public bool IsBucketList => Bucket is null;

    /// <summary>
    /// "s3:", "s3://bucket", "s3://bucket/a/b/", and the forgiving "s3:bucket/a/b" (a missing final "/" is added).
    /// The prefix is kept exactly as typed otherwise: keys may contain spaces, "//" and any other character.
    /// </summary>
    public static S3Location Parse(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var colon = address.IndexOf(':', StringComparison.Ordinal);
        var rest = (colon >= 0 ? address[(colon + 1)..] : address).TrimStart().TrimEnd(' ', '\t', '\r', '\n');
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
        }

        if (rest.Length == 0)
        {
            return Buckets;
        }

        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var bucket = slash < 0 ? rest : rest[..slash];
        var prefix = slash < 0 ? string.Empty : rest[(slash + 1)..];
        if (bucket.Length == 0)
        {
            return Buckets;
        }

        if (prefix.Length > 0 && !prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        return new S3Location(bucket, prefix);
    }

    public string Address => Bucket is null ? "s3:" : $"s3://{Bucket}/{Prefix}";

    /// <summary>For the title: "bucket/prefix/".</summary>
    public string Path => Bucket is null ? string.Empty : $"{Bucket}/{Prefix}";

    public S3Location Up() => Bucket is null ? this : Prefix.Length == 0 ? Buckets : this with { Prefix = KeyNames.ParentPrefix(Prefix) };
}
