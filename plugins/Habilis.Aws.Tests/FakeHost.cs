using System.Text.Json.Nodes;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.Tests;

/// <summary>A host for tests: settings in memory, scripted dialog answers, recorded messages and views.</summary>
internal sealed class FakeHost : IPluginHost, IPluginDialogs, IPluginSettings
{
    private JsonObject _settings = [];

    public Version HostVersion { get; } = new(0, 1, 3);

    public IPluginDialogs Dialogs => this;

    public IPluginSettings Settings => this;

    public string TempDirectory { get; } = Path.Combine(Path.GetTempPath(), "habi-tests", "plugin-host-" + Guid.NewGuid().ToString("N"));

    public Queue<bool> ConfirmAnswers { get; } = new();

    public Queue<string?> ChooseAnswers { get; } = new();

    public List<(string Title, string Message, IReadOnlyList<string>? Items)> Confirmations { get; } = [];

    public List<(string Title, string Message, MessageKind Kind)> Messages { get; } = [];

    public List<(string Title, string Text)> Views { get; } = [];

    public List<IPanelProvider> Refreshed { get; } = [];

    public Func<PluginForm, bool> EditForm { get; set; } = _ => false;

    public Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Information)
    {
        Messages.Add((title, message, kind));
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, IReadOnlyList<string>? items = null, string confirmText = "OK", bool destructive = false)
    {
        Confirmations.Add((title, message, items));
        return Task.FromResult(ConfirmAnswers.Count > 0 && ConfirmAnswers.Dequeue());
    }

    public Task<string?> InputAsync(string title, string prompt, string? initialValue = null) => Task.FromResult<string?>(null);

    public Task<string?> ChooseAsync(string title, IReadOnlyList<string> choices, string? current = null) =>
        Task.FromResult(ChooseAnswers.Count > 0 ? ChooseAnswers.Dequeue() : null);

    public Task<bool> EditAsync(PluginForm form) => Task.FromResult(EditForm(form));

    public JsonObject Load() => (JsonObject)_settings.DeepClone();

    public void Save(JsonObject settings) => _settings = (JsonObject)settings.DeepClone();

    public Task ViewFileAsync(string path, string? title = null)
    {
        Views.Add((title ?? path, File.ReadAllText(path)));
        return Task.CompletedTask;
    }

    public Task ViewTextAsync(string title, string text)
    {
        Views.Add((title, text));
        return Task.CompletedTask;
    }

    public void Refresh(IPanelProvider provider) => Refreshed.Add(provider);

    public void ShowStatus(string message) => Messages.Add(("status", message, MessageKind.Information));
}

/// <summary>Records progress reports.</summary>
internal sealed class FakeProgress : IOperationProgress
{
    private long _bytes;
    private int _items;

    public long Bytes => Interlocked.Read(ref _bytes);

    public int Items => _items;

    public (long? Items, long? Bytes) Totals { get; private set; }

    public List<string> Begun { get; } = [];

    public void SetTotals(long? items, long? bytes) => Totals = (items, bytes);

    public void Begin(string item)
    {
        lock (Begun)
        {
            Begun.Add(item);
        }
    }

    public void AddBytes(long bytes) => Interlocked.Add(ref _bytes, bytes);

    public void CompleteItem() => Interlocked.Increment(ref _items);
}

internal static class Listing
{
    /// <summary>All batches of a listing, flattened.</summary>
    public static async Task<List<PanelEntry>> AllAsync(IPanelProvider provider, CancellationToken cancellationToken = default)
    {
        var entries = new List<PanelEntry>();
        await foreach (var batch in provider.ListAsync(cancellationToken))
        {
            entries.AddRange(batch);
        }

        return entries;
    }
}
