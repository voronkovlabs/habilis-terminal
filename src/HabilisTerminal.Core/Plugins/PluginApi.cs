using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HabilisTerminal.Core.Plugins;

/// <summary>A plugin as <c>plugins.list</c> reports it (docs/plugin-api.md).</summary>
/// <param name="BuildId">Changes with every compiled change, so that a reload can be seen to have taken effect.</param>
public sealed record PluginApiEntry(string Id, string Name, string? Version, string? Sdk, string Folder, string Source, string State, string? Error, string? ErrorDetail, bool Collectible, string? BuildId);

/// <summary>One row of a panel, as <c>panels.state</c> reports it.</summary>
public sealed record PluginApiPanelEntry(string Name, bool IsFolder, long Size);

/// <summary>The active panel, so that a plugin can be checked without a window (<c>panels.open</c>, <c>panels.state</c>).</summary>
public sealed record PluginApiPanelState(string? Plugin, string Location, string? Title, string? Message, int Count, IReadOnlyList<PluginApiPanelEntry> Entries);

/// <summary>What a load, reload or unload did. <paramref name="OldVersionFreed"/>: null when there was no old copy.</summary>
public sealed record PluginApiOutcome(string Id, string State, bool? OldVersionFreed, int PanelsClosed, int PanelsReopened, string? Error);

/// <summary>The answer to <c>api.version</c>.</summary>
public sealed record PluginApiVersion(int Api, string Sdk, string Host);

/// <summary>A request that cannot be done; its code goes to the client (docs/plugin-api.md).</summary>
public sealed class PluginApiException(string code, string message) : Exception(message)
{
    public const string BadRequest = "bad_request";
    public const string UnknownMethod = "unknown_method";
    public const string NotFound = "not_found";
    public const string Busy = "busy";
    public const string LoadFailed = "load_failed";
    public const string IncompatibleSdk = "incompatible_sdk";
    public const string Internal = "internal";

    public string Code { get; } = code;
}

/// <summary>What the API asks of the app. The app runs these on its UI thread.</summary>
public interface IPluginApiHandler
{
    Task<IReadOnlyList<PluginApiEntry>> ListAsync();

    Task<PluginApiOutcome> LoadAsync(string folder);

    Task<PluginApiOutcome> ReloadAsync(string id);

    Task<PluginApiOutcome> UnloadAsync(string id);

    /// <summary>Opens an address or a file in the active panel, as the user would.</summary>
    Task<PluginApiPanelState> OpenPanelAsync(string target);

    Task<PluginApiPanelState> PanelStateAsync();
}

/// <summary>
/// The API for loading and unloading plugins while the app runs (issue #150, docs/plugin-api.md): one JSON object per
/// line, over a named pipe only the current Windows user can open.
/// </summary>
public sealed class PluginApiServer(string pipeName, IPluginApiHandler handler, Version hostVersion) : IDisposable
{
    public const int ApiVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly CancellationTokenSource _stop = new();

    /// <summary>The pipe of a running app; a client finds it by the process id.</summary>
    public static string PipeNameFor(int processId) => $"habi-plugins-{processId}";

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    /// <summary>One request line to one reply line. Never throws: everything wrong becomes an error reply.</summary>
    public async Task<string> HandleLineAsync(string line)
    {
        JsonElement id = default;
        try
        {
            using var request = ParseRequest(line);
            var root = request.RootElement;
            if (root.TryGetProperty("id", out var given))
            {
                id = given.Clone();
            }

            var method = root.TryGetProperty("method", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
            var result = await DispatchAsync(method, root).ConfigureAwait(false);
            return Reply(id, new { ok = true, result });
        }
        catch (PluginApiException ex)
        {
            return Reply(id, new { ok = false, error = new { code = ex.Code, message = ex.Message } });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Reply(id, new { ok = false, error = new { code = PluginApiException.Internal, message = ex.Message } });
        }
    }

    private static JsonDocument ParseRequest(string line)
    {
        try
        {
            var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
        }
        catch (JsonException ex)
        {
            throw new PluginApiException(PluginApiException.BadRequest, $"The request is not valid JSON: {ex.Message}");
        }

        throw new PluginApiException(PluginApiException.BadRequest, "The request must be a JSON object.");
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new PluginApiException(PluginApiException.BadRequest, $"params.{name} is required.");

    private static string Reply(JsonElement id, object body)
    {
        var node = JsonSerializer.SerializeToNode(body, Options)!.AsObject();
        if (id.ValueKind != JsonValueKind.Undefined)
        {
            node.Insert(0, "id", System.Text.Json.Nodes.JsonNode.Parse(id.GetRawText()));
        }

        return node.ToJsonString(Options);
    }

    private async Task<object?> DispatchAsync(string? method, JsonElement root) => method switch
    {
        "api.version" => new PluginApiVersion(ApiVersion, $"{PluginManifest.HostSdkVersion.Major}.{PluginManifest.HostSdkVersion.Minor}", hostVersion.ToString()),
        "plugins.list" => await handler.ListAsync().ConfigureAwait(false),
        "plugins.load" => await handler.LoadAsync(Text(root, "folder")).ConfigureAwait(false),
        "plugins.reload" => await handler.ReloadAsync(Text(root, "id")).ConfigureAwait(false),
        "plugins.unload" => await handler.UnloadAsync(Text(root, "id")).ConfigureAwait(false),
        "panels.open" => await handler.OpenPanelAsync(Text(root, "target")).ConfigureAwait(false),
        "panels.state" => await handler.PanelStateAsync().ConfigureAwait(false),
        _ => throw new PluginApiException(PluginApiException.UnknownMethod, $"There is no method \"{method}\"."),
    };

    private async Task AcceptLoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // The pipe could not be made: try again shortly rather than losing the API for this run.
                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => ServeAsync(pipe, token), CancellationToken.None);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var reader = new StreamReader(pipe, Utf8, leaveOpen: true);
                var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                await using (writer.ConfigureAwait(false))
                {
                    while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                    {
                        await writer.WriteLineAsync(await HandleLineAsync(line).ConfigureAwait(false)).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away, or the app is closing.
            }
        }
    }
}
