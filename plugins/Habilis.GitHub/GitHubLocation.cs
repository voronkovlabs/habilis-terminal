namespace Habilis.GitHub;

/// <summary>
/// A place in the GitHub panel: the owners (all null), an owner's repositories, a repository's branches, or a folder
/// of a branch. The address is <c>gh:owner/repo/branch/path</c>; a branch name may hold "/", so in the address its
/// "%" and "/" are written <c>%25</c> and <c>%2F</c>, and the address reads back the same place.
/// </summary>
internal sealed record GitHubLocation(string? Owner = null, string? Repo = null, string? Branch = null, string Path = "")
{
    public const string Prefix = "gh";

    public static GitHubLocation Top { get; } = new();

    public bool IsTop => Owner is null;

    public bool IsOwner => Owner is not null && Repo is null;

    public bool IsRepo => Repo is not null && Branch is null;

    public bool IsBranch => Branch is not null;

    public string Address
    {
        get
        {
            var parts = new List<string>();
            if (Owner is not null)
            {
                parts.Add(Owner);
            }

            if (Repo is not null)
            {
                parts.Add(Repo);
            }

            if (Branch is not null)
            {
                parts.Add(EscapeBranch(Branch));
            }

            if (Path.Length > 0)
            {
                parts.Add(Path);
            }

            return Prefix + ":" + string.Join('/', parts);
        }
    }

    /// <summary>The place one level up; the top stays the top.</summary>
    public GitHubLocation Parent =>
        Path.Length > 0 ? this with { Path = Path.Contains('/', StringComparison.Ordinal) ? Path[..Path.LastIndexOf('/')] : string.Empty }
        : Branch is not null ? this with { Branch = null }
        : Repo is not null ? this with { Repo = null }
        : Top;

    /// <summary>A place inside a branch's folder.</summary>
    public GitHubLocation Child(string name) => this with { Path = Path.Length == 0 ? name : Path + "/" + name };

    /// <summary>Reads <c>gh:</c>, <c>gh:owner</c>, <c>gh:owner/repo</c>, <c>gh:owner/repo/branch/path</c>; also with <c>gh://</c>.</summary>
    public static GitHubLocation Parse(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var text = address.Trim().Trim('"');
        if (text.StartsWith(Prefix + ":", StringComparison.OrdinalIgnoreCase))
        {
            text = text[(Prefix.Length + 1)..];
        }

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => Top,
            1 => new GitHubLocation(parts[0]),
            2 => new GitHubLocation(parts[0], parts[1]),
            _ => new GitHubLocation(parts[0], parts[1], UnescapeBranch(parts[2]), string.Join('/', parts.Skip(3))),
        };
    }

    public static string EscapeBranch(string branch) =>
        branch.Replace("%", "%25", StringComparison.Ordinal).Replace("/", "%2F", StringComparison.Ordinal);

    public static string UnescapeBranch(string text) =>
        text.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase).Replace("%25", "%", StringComparison.Ordinal);
}
