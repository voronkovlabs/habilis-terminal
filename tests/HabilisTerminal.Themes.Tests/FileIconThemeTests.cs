namespace HabilisTerminal.Themes.Tests;

public class FileIconThemeTests
{
    private static readonly Lazy<FileIconTheme> Seti = new(() => FileIconTheme.LoadBuiltIn(FileIconTheme.Seti));
    private static readonly Lazy<FileIconTheme> Material = new(() => FileIconTheme.LoadBuiltIn(FileIconTheme.Material));

    [Fact]
    public void Both_built_in_icon_themes_load_with_all_definitions()
    {
        Assert.Equal(383, Seti.Value.Count);
        Assert.Equal(1256, Material.Value.Count);
        Assert.All(new[] { Seti.Value, Material.Value }, theme => Assert.NotNull(theme.ResolveFile("unknown.zzz")));
    }

    [Fact]
    public void Material_resolves_by_file_name_extension_and_folder_name()
    {
        Assert.Equal("nodejs", Material.Value.ResolveFile("package.json")!.Id);
        Assert.Equal("typescript", Material.Value.ResolveFile("Index.TS")!.Id);
        Assert.Equal("folder-src", Material.Value.ResolveFolder("src")!.Id);
        Assert.Equal("folder", Material.Value.ResolveFolder("plain-folder")!.Id);
        Assert.Equal("file", Material.Value.ResolveFile("no-extension")!.Id);
    }

    [Fact]
    public void Longest_extension_wins()
    {
        // Material maps "d.ts" separately from "ts".
        var declaration = Material.Value.ResolveFile("types.d.ts")!.Id;

        Assert.NotEqual(Material.Value.ResolveFile("types.ts")!.Id, declaration);
    }

    [Fact]
    public void Seti_resolves_through_language_ids_and_light_variants()
    {
        Assert.Equal("_c-sharp", Seti.Value.ResolveFile("Program.cs")!.Id);
        Assert.Equal("_c-sharp_light", Seti.Value.ResolveFile("Program.cs", light: true)!.Id);
        Assert.Equal("_default", Seti.Value.ResolveFile("unknown.zzz")!.Id);
    }

    [Fact]
    public void Seti_has_no_folder_icons()
    {
        Assert.Null(Seti.Value.ResolveFolder("src"));
    }

    [Fact]
    public void Icons_have_view_boxes_and_shapes()
    {
        var icon = Material.Value.ResolveFile("Program.cs")!;

        Assert.True(icon.Width > 0 && icon.Height > 0);
        Assert.NotEmpty(icon.Shapes);
        Assert.StartsWith("F", icon.Shapes[0].Data, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.test.ts", new[] { "test.ts", "ts" })]
    [InlineData(".gitignore", new string[0])]
    [InlineData("noext", new string[0])]
    [InlineData("trailing.", new string[0])]
    public void Extension_candidates_are_longest_first(string name, string[] expected)
    {
        Assert.Equal(expected, FileIconTheme.Extensions(name));
    }
}
