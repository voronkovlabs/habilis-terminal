using System.Globalization;
using System.Text;

namespace Habilis.Aws.S3.Tests;

public class KeyNamesTests
{
    [Theory]
    [InlineData("report.txt", "report.txt")]
    [InlineData("what?.txt", "what%3F.txt")]
    [InlineData("a<b>c:d\"e|f*g\\h", "a%3Cb%3Ec%3Ad%22e%7Cf%2Ag%5Ch")]
    [InlineData("tab\there", "tab%09here")]
    [InlineData("trailing.", "trailing%2E")]
    [InlineData("trailing. . ", "trailing%2E%20%2E%20")]
    [InlineData(".", "%2E")]
    [InlineData("..", "%2E%2E")]
    [InlineData(".hidden", ".hidden")]
    [InlineData("CON", "CO%4E")]
    [InlineData("nul.txt", "nu%6C.txt")]
    [InlineData("com1.tar.gz", "com%31.tar.gz")]
    [InlineData("CON .txt", "CO%4E .txt")]
    [InlineData("CONSOLE", "CONSOLE")]
    [InlineData("100%", "100%")]
    [InlineData("100% sure", "100% sure")]
    [InlineData("%41", "%2541")]
    [InlineData("", KeyNames.EmptySegment)]
    [InlineData("Ünïcødé ключ 鍵", "Ünïcødé ключ 鍵")]
    public void Key_segments_become_valid_windows_names(string segment, string expected)
    {
        var name = KeyNames.LocalName(segment);

        Assert.Equal(expected, name);
        Assert.True(IsValidWindowsName(name), name);
    }

    [Fact]
    public void Different_keys_never_share_a_local_name()
    {
        string[] segments =
        [
            "a", "a.", "a. ", "a%2E", "a%252E", "%2F", "", "?", "%3F", "%3f", "CON", "CO%4E", "con", "x%", "x%4", "x%41",
            "\u0001", "%01", " ", "%20", "*", "\\", "|", "AUX.txt", "AU%58.txt",
        ];

        var names = segments.Select(KeyNames.LocalName).ToList();

        // Decoding the escapes gives the key back: the mapping loses nothing.
        Assert.Equal(segments.Select(s => s.Length == 0 ? "/" : s), names.Select(Decode));
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("sub/dir/file.txt", "sub\\dir\\file.txt")]
    [InlineData("a//b", "a\\%2F\\b")]
    [InlineData("q?/x:y", "q%3F\\x%3Ay")]
    public void Relative_keys_become_relative_paths(string key, string path) =>
        Assert.Equal(path, KeyNames.LocalPath(key));

    [Theory]
    [InlineData("a/b/", "a/")]
    [InlineData("a/", "")]
    [InlineData("a//", "a/")]
    [InlineData("/", "")]
    [InlineData("//", "/")]
    [InlineData("", "")]
    public void Parent_prefix_drops_one_level(string prefix, string parent) =>
        Assert.Equal(parent, KeyNames.ParentPrefix(prefix));

    [Theory]
    [InlineData("", "")]
    [InlineData("a", "a")]
    [InlineData("/a", "//a")]
    [InlineData("//a", "///a")]
    [InlineData("a//b", "a//b")]
    public void Keys_in_request_paths_survive_the_sdk_dropping_a_leading_slash(string key, string pathKey) =>
        Assert.Equal(pathKey, S3Api.PathKey(key));

    [Theory]
    [InlineData("a+b", "a b")]
    [InlineData("a%2Bb", "a+b")]
    [InlineData("q%3F%01x%2541", "q?\u0001x%41")]
    [InlineData("%D0%BA%D0%BB%D1%8E%D1%87", "ключ")]
    public void Url_encoded_listings_are_decoded(string listed, string key) =>
        Assert.Equal(key, S3Api.Decode(listed));

    private static string Decode(string name)
    {
        var text = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '%' && i + 2 < name.Length && char.IsAsciiHexDigit(name[i + 1]) && char.IsAsciiHexDigit(name[i + 2]))
            {
                text.Append((char)int.Parse(name.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 2;
            }
            else
            {
                text.Append(name[i]);
            }
        }

        return text.ToString();
    }

    private static bool IsValidWindowsName(string name) =>
        name.Length > 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.EndsWith('.')
        && !name.EndsWith(' ')
        && !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "LPT1" }.Contains(name.Split('.')[0].TrimEnd(), StringComparer.OrdinalIgnoreCase);
}
