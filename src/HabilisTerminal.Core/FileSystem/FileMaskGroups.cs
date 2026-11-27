namespace HabilisTerminal.Core.FileSystem;

/// <summary>A named set of masks, written <c>&lt;music&gt;</c> wherever a mask is taken (issue #413, as Far's mask groups).</summary>
/// <param name="Name">What goes between the angle brackets.</param>
/// <param name="Title">How pickers show it.</param>
/// <param name="Masks">The masks, as a mask field takes them.</param>
public sealed record FileMaskGroup(string Name, string Title, string Masks)
{
    public string Token => $"<{Name}>";
}

/// <summary>
/// The mask groups: pictures, music, video, documents, archives, code, executables. Their masks come from the settings
/// (<c>masks.&lt;name&gt;</c>); until the app applies them, the defaults hold.
/// </summary>
public static class FileMaskGroups
{
    /// <summary>The groups with their default masks, in the order pickers show them.</summary>
    public static IReadOnlyList<FileMaskGroup> Defaults { get; } =
    [
        new("pictures", "Pictures",
            "*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.gif;*.bmp;*.dib;*.webp;*.heic;*.heif;*.avif;*.tif;*.tiff;*.ico;*.svg;*.psd;*.raw;*.dng;*.cr2;*.cr3;*.nef;*.arw;*.orf;*.rw2"),
        new("music", "Music",
            "*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg;*.oga;*.opus;*.wma;*.ape;*.alac;*.aiff;*.aif;*.mid;*.midi;*.dsf;*.dff;*.mka"),
        new("video", "Video",
            "*.mp4;*.m4v;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.flv;*.mpg;*.mpeg;*.m2ts;*.mts;*.ts;*.vob;*.3gp"),
        new("documents", "Documents",
            "*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.odt;*.ods;*.odp;*.rtf;*.txt;*.md;*.csv;*.epub;*.djvu;*.fb2"),
        new("archives", "Archives",
            "*.zip;*.7z;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.tbz2;*.xz;*.txz;*.zst;*.cab;*.iso;*.arj;*.lzh;*.jar"),
        new("code", "Code",
            "*.cs;*.csproj;*.sln;*.slnx;*.fs;*.vb;*.c;*.h;*.cpp;*.hpp;*.cc;*.java;*.kt;*.kts;*.go;*.rs;*.py;*.rb;*.php;*.js;*.jsx;*.ts;*.tsx;*.mjs;*.css;*.scss;*.html;*.htm;*.xml;*.xaml;*.axaml;*.json;*.yaml;*.yml;*.toml;*.sql;*.ps1;*.psm1;*.sh;*.bat;*.cmd;*.swift;*.dart;*.lua"),
        new("executables", "Programs",
            "*.exe;*.com;*.bat;*.cmd;*.ps1;*.msi;*.msix;*.appx;*.lnk;*.scr"),
    ];

    private static Dictionary<string, FileMaskGroup> _groups = Defaults.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The groups in force, in the pickers' order.</summary>
    public static IReadOnlyList<FileMaskGroup> All => [.. Defaults.Select(d => _groups[d.Name])];

    /// <summary>The settings' masks by group name; an empty or missing one keeps the default.</summary>
    public static void Apply(Func<string, string?> masksOf) =>
        _groups = Defaults
            .Select(d => masksOf(d.Name) is { } masks && !string.IsNullOrWhiteSpace(masks) ? d with { Masks = masks.Trim() } : d)
            .ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Back to the defaults (tests).</summary>
    public static void Reset() => _groups = Defaults.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The group a token such as <c>&lt;music&gt;</c> names; null when the text is no token.</summary>
    /// <exception cref="FormatException">A token naming no group.</exception>
    public static FileMaskGroup? Resolve(string token)
    {
        if (token.Length < 3 || token[0] != '<' || token[^1] != '>')
        {
            return null;
        }

        var name = token[1..^1].Trim();
        return _groups.TryGetValue(name, out var group)
            ? group
            : throw new FormatException($"There is no mask group {token}. Groups: {string.Join(", ", All.Select(g => g.Token))}.");
    }

    /// <summary>
    /// A picker's choice into a mask field: the group alone when the field holds everything or a group, else added to
    /// what is there. A group already in the field is left as it is.
    /// </summary>
    public static string AddTo(string? current, FileMaskGroup group)
    {
        var text = (current ?? string.Empty).Trim();
        if (text is "" or "*" or "*.*" || (text.StartsWith('<') && text.EndsWith('>') && !text.AsSpan(1).ContainsAny(";,|")))
        {
            return group.Token;
        }

        var bar = text.IndexOf('|', StringComparison.Ordinal);
        var include = bar < 0 ? text : text[..bar];
        if (include.Split([';', ','], StringSplitOptions.TrimEntries).Contains(group.Token, StringComparer.OrdinalIgnoreCase))
        {
            return text;
        }

        return bar < 0 ? $"{include};{group.Token}" : $"{include};{group.Token}{text[bar..]}";
    }
}
