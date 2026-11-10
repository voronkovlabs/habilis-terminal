using System.Text;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Search;

namespace HabilisTerminal.Core.Tests;

/// <summary>Alt+F7 with regular expressions (#209), inside archives (#210) and with the cache of repeated searches (#211).</summary>
public sealed class FileSearchMoreTests : IDisposable
{
    private readonly TestSandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_sandbox.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private List<string> Find(string mask, string? text, bool regex = false, bool matchCase = false, bool wholeWords = false, bool archives = false,
        SearchCache? cache = null, IContainerSearch? containers = null, bool folders = false)
    {
        var found = new List<string>();
        var request = new FileSearchRequest(FileMask.Parse(mask), text, matchCase, wholeWords, folders, [_sandbox.Path], Recursive: true)
        {
            Regex = regex,
            Archives = archives,
        };
        FileSearch.Run(request, item => found.Add(Path.GetRelativePath(_sandbox.Path, item.Path)), null, cache, containers, CancellationToken.None);
        return found;
    }

    [Fact]
    public void A_regular_expression_matches_within_a_line()
    {
        Write("a.txt", "first line\nerror: code 42\n");
        Write("b.txt", "error:\ncode 42\n");
        Write("c.txt", "ERROR: code 7");

        Assert.Equal(["a.txt", "c.txt"], Find("*", @"error: code \d+", regex: true));
        Assert.Equal(["a.txt"], Find("*", @"error: code \d+", regex: true, matchCase: true));
        Assert.Equal(["b.txt"], Find("*", @"^code 42$", regex: true));
        Assert.Empty(Find("*", "cod", regex: true, wholeWords: true));
        Assert.ThrowsAny<ArgumentException>(() => new TextFinder("(", false, false, regex: true));
    }

    [Fact]
    public void A_regular_expression_finds_a_line_that_spans_two_chunks()
    {
        // The reader's chunk is a million characters: the matching line straddles the first cut.
        var text = new string('x', (1 << 20) - 5) + "\nab key=value cd\n";
        Write("big.txt", text);

        Assert.Equal(["big.txt"], Find("*", @"key=\w+ cd$", regex: true));
    }

    [Fact]
    public void Masks_take_regular_expressions_as_in_far()
    {
        Write("report-2024.txt", "x");
        Write("report-old.txt", "x");

        Assert.Equal(["report-2024.txt"], Find(@"/report-\d+/", null));
    }

    [Fact]
    public void A_repeated_search_reads_only_changed_files()
    {
        var directory = Path.Combine(_sandbox.Path, "..", Path.GetFileName(_sandbox.Path) + "-cache");
        try
        {
            var a = Write("a.txt", "needle");
            Write("b.txt", "hay");
            var key = SearchCache.KeyOf("needle", false, false, false);

            var first = SearchCache.Open(directory, key);
            Assert.Equal(["a.txt"], Find("*", "needle", cache: first));
            Assert.Equal(0, first.Hits);
            first.Save();

            var second = SearchCache.Open(directory, key);
            Assert.Equal(["a.txt"], Find("*", "needle", cache: second));
            Assert.Equal(2, second.Hits);

            // A changed file is read again: its size and date differ.
            File.WriteAllText(a, "no longer");
            File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(1));
            Write("b.txt", "now a needle");
            var third = SearchCache.Open(directory, key);
            Assert.Equal(["b.txt"], Find("*", "needle", cache: third));
            Assert.Equal(0, third.Hits);

            // A different text has its own cache.
            Assert.NotEqual(key, SearchCache.KeyOf("needle", true, false, false));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Only_the_latest_queries_are_kept_and_a_damaged_cache_is_dropped()
    {
        var directory = Path.Combine(_sandbox.Path, "cache");
        for (var i = 0; i < SearchCache.KeptQueries + 3; i++)
        {
            var cache = SearchCache.Open(directory, SearchCache.KeyOf("q" + i, false, false, false));
            cache.Record("x" + i, 1, DateTime.UtcNow, true);
            cache.Save();
            File.SetLastWriteTimeUtc(Directory.GetFiles(directory, SearchCache.KeyOf("q" + i, false, false, false) + "*")[0], DateTime.UtcNow.AddSeconds(-1000 + i));
        }

        Assert.Equal(SearchCache.KeptQueries, Directory.GetFiles(directory, "*.cache").Length);

        var damaged = Path.Combine(directory, SearchCache.KeyOf("bad", false, false, false) + ".cache");
        File.WriteAllBytes(damaged, [1, 0, 0, 0, 200, 0]);
        Assert.Null(SearchCache.Open(directory, SearchCache.KeyOf("bad", false, false, false)).Lookup("x", 1, DateTime.UtcNow));
    }

    [Fact]
    public void Archives_are_searched_through_what_the_plugin_gives()
    {
        Write("data.pack", "packed bytes with a needle");
        Write("plain.txt", "a needle");
        var containers = new FakeContainers(new Dictionary<string, string>
        {
            [@"docs\readme.txt"] = "the needle is here",
            [@"docs\other.txt"] = "nothing",
            ["notes.md"] = "needle too",
        });

        // Inside: entries whose names fit the mask and whose content holds the text. The pack's own bytes are not searched.
        Assert.Equal([@"data.pack\docs\readme.txt", @"data.pack\notes.md", "plain.txt"], Find("*", "needle", archives: true, containers: containers));
        Assert.Equal([@"data.pack\docs\readme.txt", "plain.txt"], Find("*.txt", "needle", archives: true, containers: containers));

        // By name: the archive and its entries.
        Assert.Equal(["data.pack", @"data.pack\docs\other.txt", @"data.pack\docs\readme.txt", @"data.pack\notes.md", "plain.txt"],
            Find("*", null, archives: true, containers: containers));

        // Without the option the archive is a plain file again.
        Assert.Equal(["data.pack", "plain.txt"], Find("*", "needle", containers: containers));
    }

    [Fact]
    public void A_failing_plugin_counts_the_archive_as_unreadable()
    {
        Write("data.pack", "x");
        Write("plain.txt", "needle");
        var request = new FileSearchRequest(FileMask.All, "needle", false, false, false, [_sandbox.Path], true) { Archives = true };
        var found = new List<FoundItem>();
        var unreadable = FileSearch.Run(request, found.Add, null, null, new FakeContainers(null), CancellationToken.None);

        Assert.Equal(1, unreadable);
        Assert.Equal(["plain.txt"], found.Select(f => Path.GetFileName(f.Path)));
    }

    [Fact]
    public void Archive_entries_are_cached_by_their_own_size_and_date()
    {
        var directory = Path.Combine(_sandbox.Path, "cache");
        Write("data.pack", "x");
        var containers = new FakeContainers(new Dictionary<string, string> { ["a.txt"] = "needle", ["b.txt"] = "hay" });
        var key = SearchCache.KeyOf("needle", false, false, false);
        var first = SearchCache.Open(directory, key);
        Find("*.txt", "needle", archives: true, cache: first, containers: containers);
        first.Save();
        Assert.Equal(2, containers.Opened);

        var second = SearchCache.Open(directory, key);
        Assert.Equal([@"data.pack\a.txt"], Find("*.txt", "needle", archives: true, cache: second, containers: containers));
        Assert.Equal(2, containers.Opened);
    }

    /// <summary>A plugin that opens *.pack; null entries make it fail.</summary>
    private sealed class FakeContainers(Dictionary<string, string>? entries) : IContainerSearch
    {
        private static readonly DateTime Stamp = new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        public int Opened { get; private set; }

        public bool Claims(string fileName) => fileName.EndsWith(".pack", StringComparison.OrdinalIgnoreCase);

        public IAsyncEnumerable<ContainerEntry>? Enumerate(string path, CancellationToken cancellationToken) =>
            entries is null ? throw new IOException("damaged") : List(cancellationToken);

        private async IAsyncEnumerable<ContainerEntry> List([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var (inner, text) in entries!.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = Encoding.UTF8.GetBytes(text);
                yield return new ContainerEntry(inner, false, bytes.Length, Stamp, _ =>
                {
                    Opened++;
                    return Task.FromResult<Stream>(new MemoryStream(bytes));
                });
            }
        }
    }
}
