using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using HabilisTerminal.Plugins;

namespace Habilis.Samples.Demo;

/// <summary>
/// A sample plugin without network access (docs/plugins.md): an in-memory tree that uses every plugin panel feature,
/// for trying the SDK and checking the host. Opens from F11, the drive menu or <c>demo:</c> on the command line.
/// </summary>
public sealed class DemoPlugin : Plugin
{
    private readonly DemoNode _root = DemoNode.Sample();

    public override IEnumerable<PluginMenuItem> MenuItems => [new("Demo tree", _ => Open("/"))];

    public override IEnumerable<PluginMenuItem> DriveMenuItems => MenuItems;

    public override IEnumerable<string> Prefixes => ["demo"];

    public override bool HasConfiguration => true;

    /// <summary>"demo:" or "demo:/folder/sub".</summary>
    public override Task<IPanelProvider?> OpenAddressAsync(string address, CancellationToken cancellationToken) =>
        Open(address[(address.IndexOf(':', StringComparison.Ordinal) + 1)..]);

    public override async Task ConfigureAsync(CancellationToken cancellationToken)
    {
        var settings = Host.Settings.Load();
        var form = new PluginForm("Demo settings",
        [
            new NumberField("delay", "Listing delay per batch, ms", Number(settings, "delay", 0), 0, 5000) { Description = "Slows the listing down to watch batches arrive and cancel them." },
            new NumberField("batch", "Entries per batch", Number(settings, "batch", 100), 1, 10000),
            new ToggleField("tones", "Colour rows", settings["tones"]?.GetValue<bool>() ?? true),
        ]);
        if (await Host.Dialogs.EditAsync(form).ConfigureAwait(false))
        {
            Host.Settings.Save(new JsonObject
            {
                ["delay"] = form.Field<NumberField>("delay").Value,
                ["batch"] = form.Field<NumberField>("batch").Value,
                ["tones"] = form.Field<ToggleField>("tones").Value,
            });
        }
    }

    private Task<IPanelProvider?> Open(string path)
    {
        var node = _root.Find(path.Trim()) ?? _root;
        return Task.FromResult<IPanelProvider?>(new DemoPanel(Host, node));
    }

    private static int Number(JsonObject settings, string key, int fallback) => settings[key]?.GetValue<int>() ?? fallback;
}

/// <summary>A folder or file of the in-memory tree.</summary>
internal sealed class DemoNode(string name, DemoNode? parent, bool isFolder)
{
    public string Name { get; set; } = name;

    public DemoNode? Parent { get; } = parent;

    public bool IsFolder { get; } = isFolder;

    public List<DemoNode> Children { get; } = [];

    public byte[] Content { get; set; } = [];

    public DateTime Modified { get; set; } = DateTime.UtcNow;

    public string Kind { get; set; } = "note";

    public int Score { get; set; }

    public bool Pinned { get; set; }

    public string Path => Parent is null ? "/" : (Parent.Path.TrimEnd('/') + "/" + Name + (IsFolder ? "/" : string.Empty));

    public DemoNode Add(string name, bool folder, string text = "")
    {
        var node = new DemoNode(name, this, folder) { Content = Encoding.UTF8.GetBytes(text) };
        Children.Add(node);
        return node;
    }

    public DemoNode? Find(string path)
    {
        var node = this;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.Children.FirstOrDefault(c => c.IsFolder && c.Name == part);
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    public static DemoNode Sample()
    {
        var root = new DemoNode(string.Empty, null, true);
        var kinds = new[] { "note", "report", "image", "archive" };
        var docs = root.Add("documents", folder: true);
        docs.Add("readme.txt", folder: false, "This tree lives in memory; changes vanish when the app closes.").Pinned = true;
        for (var i = 1; i <= 12; i++)
        {
            var file = docs.Add($"file{i:00}.txt", folder: false, string.Concat(Enumerable.Repeat($"Line of file {i}\n", i * 10)));
            file.Kind = kinds[i % kinds.Length];
            file.Score = (i * 37) % 100;
            file.Modified = DateTime.UtcNow.AddDays(-i);
        }

        var big = root.Add("many entries", folder: true);
        for (var i = 1; i <= 5000; i++)
        {
            var file = big.Add($"entry {i:0000}", folder: false, $"Entry {i}");
            file.Score = i % 1000;
            file.Kind = kinds[i % kinds.Length];
        }

        root.Add("empty folder", folder: true);
        root.Add("Ünïcödé — names ✓.txt", folder: false, "Non-ASCII names work too.");
        return root;
    }
}

/// <summary>One panel on the demo tree: every optional interface of the SDK.</summary>
internal sealed class DemoPanel(IPluginHost host, DemoNode folder) : IPanelProvider, IGetFiles, IPutFiles, IDeleteEntries, IMakeFolder, IViewEntry, IPanelCommands
{
    private DemoNode _folder = folder;

    public string Title => "Demo";

    public string Location => "demo:" + _folder.Path;

    public IReadOnlyList<PanelLayout> Layouts { get; } =
    [
        new("Details", [new PanelColumn("kind", "Kind", 80), new PanelColumn("score", "Score", 64, AlignRight: true), PanelColumn.Size, PanelColumn.Modified]),
        new("Brief", [PanelColumn.Size]),
        new("Scores", [new PanelColumn("score", "Score", 64, AlignRight: true), new PanelColumn("kind", "Kind", 80)]),
    ];

    public IReadOnlyList<PanelSortKey> SortKeys { get; } = [new("score", "Score"), new("kind", "Kind")];

    public IReadOnlyList<InfoLine> Info => [new("Folder", _folder.Path), new("Entries", _folder.Children.Count.ToString("N0", CultureInfo.CurrentCulture)), new("Storage", "in memory")];

    public bool CanMove => true;

    public IReadOnlyList<PanelCommand> Commands =>
    [
        new("Pin or unpin (pinned entries sort first)", "Shift+F4", context =>
        {
            foreach (var node in Nodes(context.Selected))
            {
                node.Pinned = !node.Pinned;
            }

            return Task.CompletedTask;
        }),
        new("Add a sample file", "Shift+F7", async context =>
        {
            var name = await host.Dialogs.InputAsync("Add a sample file", "File name", "new file.txt").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(name))
            {
                _folder.Add(name.Trim(), folder: false, $"Created {DateTime.Now:G}");
            }
        }),
        new("Show a message", null, _ => host.Dialogs.ShowMessageAsync("Demo", "Plugin dialogs are drawn by the host in its own theme.")),
    ];

    public async IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = host.Settings.Load();
        var delay = settings["delay"]?.GetValue<int>() ?? 0;
        var batch = Math.Max(1, settings["batch"]?.GetValue<int>() ?? 100);
        var tones = settings["tones"]?.GetValue<bool>() ?? true;
        var children = _folder.Children.ToList();
        for (var i = 0; i < children.Count || i == 0; i += batch)
        {
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            yield return children.Skip(i).Take(batch).Select(n => Entry(n, tones)).ToList();
        }
    }

    public Task<bool> EnterAsync(PanelEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Tag is DemoNode { IsFolder: true } node)
        {
            _folder = node;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<bool> GoUpAsync(CancellationToken cancellationToken)
    {
        if (_folder.Parent is not { } parent)
        {
            return Task.FromResult(false);
        }

        _folder = parent;
        return Task.FromResult(true);
    }

    public async Task<OperationOutcome> GetFilesAsync(TransferRequest request, IOperationProgress progress, CancellationToken cancellationToken)
    {
        var files = Nodes(request.Entries).SelectMany(n => Flatten(n, string.Empty)).ToList();
        progress.SetTotals(files.Count, files.Sum(f => (long)f.Node.Content.Length));
        int done = 0, skipped = 0;
        var overwriteAll = false;
        foreach (var (node, relative) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Begin(relative);
            var target = System.IO.Path.Combine(request.LocalFolder, relative);
            if (File.Exists(target) && !overwriteAll)
            {
                var choice = await request.AskOverwrite(relative).ConfigureAwait(false);
                if (choice == OverwriteChoice.Cancel)
                {
                    throw new OperationCanceledException();
                }

                overwriteAll = choice == OverwriteChoice.OverwriteAll;
                if (choice is OverwriteChoice.Skip or OverwriteChoice.SkipAll)
                {
                    skipped++;
                    continue;
                }
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, node.Content, cancellationToken).ConfigureAwait(false);
            progress.AddBytes(node.Content.Length);
            progress.CompleteItem();
            done++;
        }

        if (request.Move)
        {
            foreach (var node in Nodes(request.Entries))
            {
                node.Parent?.Children.Remove(node);
            }
        }

        return new OperationOutcome(done, skipped, 0);
    }

    public async Task<OperationOutcome> PutFilesAsync(IReadOnlyList<string> localPaths, Func<string, Task<OverwriteChoice>> askOverwrite, IOperationProgress progress, CancellationToken cancellationToken)
    {
        var done = 0;
        foreach (var path in localPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            done += await PutAsync(path, _folder, progress, cancellationToken).ConfigureAwait(false);
        }

        return new OperationOutcome(done, 0, 0);
    }

    public Task<OperationOutcome> DeleteAsync(IReadOnlyList<PanelEntry> entries, IOperationProgress progress, CancellationToken cancellationToken)
    {
        var nodes = Nodes(entries).ToList();
        progress.SetTotals(nodes.Count, null);
        foreach (var node in nodes)
        {
            progress.Begin(node.Name);
            node.Parent?.Children.Remove(node);
            progress.CompleteItem();
        }

        return Task.FromResult(new OperationOutcome(nodes.Count, 0, 0));
    }

    public Task MakeFolderAsync(string name, CancellationToken cancellationToken)
    {
        if (name.Contains('/', StringComparison.Ordinal) || _folder.Children.Any(c => c.Name == name))
        {
            throw new PluginException($"\"{name}\" cannot be created here: it contains '/' or already exists.");
        }

        _folder.Add(name, folder: true);
        return Task.CompletedTask;
    }

    public Task<(string Title, string Text)?> ViewAsync(PanelEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult<(string, string)?>(entry.Tag is DemoNode { IsFolder: false } node
            ? (node.Name, $"{node.Path}\nKind: {node.Kind}, score {node.Score}, {node.Content.Length} bytes\n\n{Encoding.UTF8.GetString(node.Content)}")
            : null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static PanelEntry Entry(DemoNode node, bool tones) => new(node.Name, node.IsFolder, node.IsFolder ? null : node.Content.Length, node.Modified)
    {
        Tag = node,
        Group = node.Pinned ? 0 : 1,
        Tone = !tones ? RowTone.Normal : node.Pinned ? RowTone.Accent : node.Score >= 90 ? RowTone.Good : node.Score < 10 && !node.IsFolder ? RowTone.Bad : RowTone.Normal,
        Cells = node.IsFolder ? null : new Dictionary<string, CellValue>
        {
            ["kind"] = node.Kind,
            ["score"] = new(node.Score.ToString(CultureInfo.CurrentCulture), node.Score),
        },
    };

    private static IEnumerable<DemoNode> Nodes(IEnumerable<PanelEntry> entries) => entries.Select(e => e.Tag).OfType<DemoNode>();

    private static IEnumerable<(DemoNode Node, string Relative)> Flatten(DemoNode node, string prefix)
    {
        var relative = System.IO.Path.Combine(prefix, node.Name);
        return node.IsFolder ? node.Children.SelectMany(c => Flatten(c, relative)) : [(node, relative)];
    }

    private static async Task<int> PutAsync(string path, DemoNode into, IOperationProgress progress, CancellationToken cancellationToken)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        if (Directory.Exists(path))
        {
            var folder = into.Children.FirstOrDefault(c => c.IsFolder && c.Name == name) ?? into.Add(name, folder: true);
            var count = 0;
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                count += await PutAsync(child, folder, progress, cancellationToken).ConfigureAwait(false);
            }

            return count;
        }

        progress.Begin(name);
        var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        into.Children.RemoveAll(c => !c.IsFolder && c.Name == name);
        into.Add(name, folder: false).Content = content;
        progress.AddBytes(content.Length);
        progress.CompleteItem();
        return 1;
    }
}
