using System.Text;

namespace HabilisTerminal.Core.Settings;

/// <summary>
/// settings.json on disk (SPEC §14): loads it, reloads it when it changes, keeps the last valid values when the file
/// is broken, and writes single-value changes from the Settings page. Also keeps the JSON Schema next to it current.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public SettingsStore(string path)
    {
        FilePath = Path.GetFullPath(path);
    }

    /// <summary>New effective settings after a reload (raised on a thread-pool thread when watching).</summary>
    public event Action<AppSettings>? Changed;

    public string FilePath { get; }

    public AppSettings Current { get; private set; } = AppSettings.Defaults;

    /// <summary>Problems found by the last load; empty when the file is fine.</summary>
    public IReadOnlyList<SettingsError> Errors { get; private set; } = [];

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }

    /// <summary>Reads the file. A syntax error keeps every previous value; an invalid value keeps its previous value.</summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            string text;
            try
            {
                text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;
            }
            catch (IOException ex)
            {
                Errors = [new SettingsError(1, ex.Message)];
                return Current;
            }
            catch (UnauthorizedAccessException ex)
            {
                Errors = [new SettingsError(1, ex.Message)];
                return Current;
            }

            var result = SettingsDocument.Parse(text);
            Errors = result.Errors;
            if (result.IsSyntaxError)
            {
                return Current;
            }

            var values = new Dictionary<string, object>(result.Values, StringComparer.Ordinal);
            foreach (var key in result.InvalidKeys)
            {
                if (Current.UserValues.TryGetValue(key, out var previous))
                {
                    values[key] = previous;
                }
            }

            Current = new AppSettings(values);
            return Current;
        }
    }

    /// <summary>Reloads on every change of the file, after it has been quiet for a moment (editors save in steps).</summary>
    public void Watch()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _debounce = new Timer(_ =>
        {
            Load();
            Changed?.Invoke(Current);
        });
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += (_, _) => Poke();
        _watcher.Created += (_, _) => Poke();
        _watcher.Deleted += (_, _) => Poke();
        _watcher.Renamed += (_, _) => Poke();
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Sets one value (null resets it to the default) keeping the rest of the file as written.</summary>
    /// <exception cref="System.Text.Json.JsonException">settings.json has a syntax error; fix it first.</exception>
    public AppSettings Set(string key, object? value)
    {
        lock (_gate)
        {
            var text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;
            if (string.IsNullOrWhiteSpace(text) && value is not null)
            {
                text = "{\n    \"$schema\": \"./settings.schema.json\"\n}\n";
            }

            var definition = SettingsCatalog.Find(key) ?? throw new KeyNotFoundException(key);
            if (value is not null && Equals(value, definition.Default))
            {
                // Only changed values belong in the file.
                value = null;
            }

            WriteAtomically(FilePath, SettingsDocument.SetValue(text, key, value));
            return Load();
        }
    }

    /// <summary>Writes settings.schema.json when it is missing or outdated.</summary>
    public static void WriteSchema(string schemaPath)
    {
        var schema = SettingsCatalog.Schema();
        try
        {
            if (!File.Exists(schemaPath) || File.ReadAllText(schemaPath) != schema)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(schemaPath)!);
                WriteAtomically(schemaPath, schema);
            }
        }
        catch (IOException)
        {
            // Only a convenience for editors.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static void WriteAtomically(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private void Poke() => _debounce?.Change(TimeSpan.FromMilliseconds(200), Timeout.InfiniteTimeSpan);
}
