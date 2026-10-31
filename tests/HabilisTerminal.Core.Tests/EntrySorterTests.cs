using System.Globalization;
using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Tests;

public class EntrySorterTests
{
    private static readonly DateTime Day = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static FileEntry File(string name, long size = 0, int modifiedDay = 0, int createdDay = 0) =>
        new(name, FileAttributes.Normal, size, Day.AddDays(createdDay), Day.AddDays(modifiedDay), Day);

    private static FileEntry Dir(string name) =>
        new(name, FileAttributes.Directory, 0, Day, Day, Day);

    private static string[] Names(IEnumerable<FileEntry> entries) => entries.Select(e => e.Name).ToArray();

    private static FileEntry[] Sort(FileEntry[] entries, SortOptions options) =>
        EntrySorter.Sort(entries, options, CultureInfo.InvariantCulture);

    [Fact]
    public void Names_sort_logically_and_case_insensitively()
    {
        FileEntry[] entries = [File("file10.txt"), File("File2.txt"), File("file1.txt"), File("a.txt")];

        Assert.Equal(["a.txt", "file1.txt", "File2.txt", "file10.txt"], Names(Sort(entries, new SortOptions())));
    }

    [Fact]
    public void Directories_come_first_by_default()
    {
        FileEntry[] entries = [File("a.txt"), Dir("zeta"), File("b.txt"), Dir("alpha")];

        Assert.Equal(["alpha", "zeta", "a.txt", "b.txt"], Names(Sort(entries, new SortOptions())));
        // "." sorts before letters, as in Explorer.
        Assert.Equal(["a.txt", "alpha", "b.txt", "zeta"], Names(Sort(entries, new SortOptions(DirectoriesFirst: false))));
    }

    [Fact]
    public void Descending_keeps_directories_first()
    {
        FileEntry[] entries = [File("a.txt"), Dir("d1"), File("b.txt"), Dir("d2")];

        Assert.Equal(["d2", "d1", "b.txt", "a.txt"], Names(Sort(entries, new SortOptions(Descending: true))));
    }

    [Fact]
    public void Extension_sort_groups_by_extension_then_name()
    {
        FileEntry[] entries = [File("b.txt"), File("a.zip"), File("noext"), File("a.txt"), Dir("dir.zip")];

        Assert.Equal(["dir.zip", "noext", "a.txt", "b.txt", "a.zip"], Names(Sort(entries, new SortOptions(SortMode.Extension))));
    }

    [Fact]
    public void Size_and_date_sorts_break_ties_by_name()
    {
        FileEntry[] entries = [File("c", size: 10, modifiedDay: 2), File("b", size: 5, modifiedDay: 1), File("a", size: 10, modifiedDay: 1)];

        Assert.Equal(["b", "a", "c"], Names(Sort(entries, new SortOptions(SortMode.Size))));
        Assert.Equal(["a", "b", "c"], Names(Sort(entries, new SortOptions(SortMode.Modified))));
    }

    [Fact]
    public void Created_and_accessed_sorts_use_their_own_times()
    {
        FileEntry[] entries = [File("new", createdDay: 5), File("old", createdDay: 1)];

        Assert.Equal(["old", "new"], Names(Sort(entries, new SortOptions(SortMode.Created))));
    }

    [Fact]
    public void Unsorted_keeps_enumeration_order_with_directories_first()
    {
        FileEntry[] entries = [File("z"), Dir("y"), File("a"), Dir("b")];

        Assert.Equal(["y", "b", "z", "a"], Names(Sort(entries, new SortOptions(SortMode.Unsorted))));
        Assert.Equal(["z", "y", "a", "b"], Names(Sort(entries, new SortOptions(SortMode.Unsorted, DirectoriesFirst: false))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    public void Sort_key_order_matches_direct_comparison(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        string[] names =
        [
            "file10.txt", "File2.txt", "file1.txt", "Отчёт 3.docx", "отчёт 12.docx", "Ёлка", "елка", "_tmp", "a-b", "ab",
            "a b", "résumé.pdf", "resume.pdf", "z", "Z1", "z01", "z001", "data2024", "data 2024", "1.txt", "10.txt", "2.txt",
        ];
        var entries = names.Select(n => File(n)).ToArray();

        var sorted = Names(EntrySorter.Sort(entries, new SortOptions(DirectoriesFirst: false), culture));
        var expected = names.ToList();
        expected.Sort((x, y) => EntrySorter.CompareNames(x, y, culture.CompareInfo));

        // Equal names under the comparison may keep either order; the key order must not contradict it.
        for (var i = 1; i < sorted.Length; i++)
        {
            Assert.True(EntrySorter.CompareNames(sorted[i - 1], sorted[i], culture.CompareInfo) <= 0, $"{sorted[i - 1]} > {sorted[i]}");
        }

        Assert.Equal(expected.Count, sorted.Length);
    }

    [Fact]
    public void Input_order_is_not_modified()
    {
        FileEntry[] entries = [File("b"), File("a")];

        _ = Sort(entries, new SortOptions());

        Assert.Equal(["b", "a"], Names(entries));
    }
}
