using System.Security.Cryptography;
using System.Text;

namespace HabilisTerminal.Core.Search;

/// <summary>
/// What recent Alt+F7 text searches found, file by file (issue #211): searching the same text again reads only the
/// files whose size or date changed. One file per query (text, case, whole words, regular expression) in a folder of
/// its own; the most recent <see cref="KeptQueries"/> are kept. An entry inside an archive is keyed by the archive's
/// path and its own inner path, with the entry's size and date. A cache file that cannot be read is dropped, never an
/// error: the search just reads everything.
/// </summary>
public sealed class SearchCache
{
    public const int KeptQueries = 8;

    /// <summary>Entries kept per query; beyond it the ones not seen in the last search go first.</summary>
    public const int MaxEntries = 400_000;

    private const int FormatVersion = 1;

    private readonly string _file;
    private readonly Dictionary<string, (long Size, long Ticks, bool Found, bool Seen)> _entries;
    private readonly Lock _gate = new();
    private bool _changed;

    private SearchCache(string file, Dictionary<string, (long, long, bool, bool)> entries)
    {
        _file = file;
        _entries = entries;
    }

    /// <summary>Answers taken from the cache in this search.</summary>
    public int Hits { get; private set; }

    /// <summary>The key of a query: what decides whether a file matches. The name masks do not.</summary>
    public static string KeyOf(string text, bool matchCase, bool wholeWords, bool regex) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{FormatVersion}\n{matchCase}\n{wholeWords}\n{regex}\n{text}")))[..32];

    /// <summary>The cache of one query; empty when there is none yet or it cannot be read.</summary>
    public static SearchCache Open(string directory, string key)
    {
        var file = Path.Combine(directory, key + ".cache");
        var entries = new Dictionary<string, (long, long, bool, bool)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(file))
            {
                using var reader = new BinaryReader(File.OpenRead(file), Encoding.UTF8);
                if (reader.ReadInt32() == FormatVersion)
                {
                    var count = reader.ReadInt32();
                    for (var i = 0; i < count; i++)
                    {
                        var path = reader.ReadString();
                        var size = reader.ReadInt64();
                        var ticks = reader.ReadInt64();
                        entries[path] = (size, ticks, reader.ReadBoolean(), false);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or FormatException)
        {
            entries.Clear();
        }

        return new SearchCache(file, entries);
    }

    /// <summary>Whether the file matched when it last had this size and date; null when not known.</summary>
    public bool? Lookup(string path, long size, DateTime modifiedUtc)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(path, out var entry) && entry.Size == size && entry.Ticks == modifiedUtc.Ticks)
            {
                _entries[path] = entry with { Seen = true };
                Hits++;
                return entry.Found;
            }

            return null;
        }
    }

    public void Record(string path, long size, DateTime modifiedUtc, bool found)
    {
        lock (_gate)
        {
            _entries[path] = (size, modifiedUtc.Ticks, found, true);
            _changed = true;
        }
    }

    /// <summary>Writes the cache when it changed, then drops the oldest queries' files beyond <see cref="KeptQueries"/>.</summary>
    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_file)!;
                Directory.CreateDirectory(directory);
                if (_changed)
                {
                    var kept = _entries.Count <= MaxEntries
                        ? _entries.ToList()
                        : [.. _entries.OrderByDescending(e => e.Value.Seen).Take(MaxEntries)];
                    var temporary = _file + ".tmp";
                    using (var writer = new BinaryWriter(File.Create(temporary), Encoding.UTF8))
                    {
                        writer.Write(FormatVersion);
                        writer.Write(kept.Count);
                        foreach (var (path, entry) in kept)
                        {
                            writer.Write(path);
                            writer.Write(entry.Size);
                            writer.Write(entry.Ticks);
                            writer.Write(entry.Found);
                        }
                    }

                    File.Move(temporary, _file, overwrite: true);
                    _changed = false;
                }
                else if (File.Exists(_file))
                {
                    // Used: it is recent again.
                    File.SetLastWriteTimeUtc(_file, DateTime.UtcNow);
                }

                foreach (var old in new DirectoryInfo(directory).GetFiles("*.cache").OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeptQueries))
                {
                    old.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A cache that cannot be written only means the next search reads everything.
            }
        }
    }
}
