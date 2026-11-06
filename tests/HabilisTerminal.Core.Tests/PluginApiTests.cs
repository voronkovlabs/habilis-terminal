using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HabilisTerminal.Core.Plugins;

namespace HabilisTerminal.Core.Tests;

public class PluginApiTests
{
    private sealed class FakeHandler : IPluginApiHandler
    {
        public string? LastCall { get; private set; }

        public Task<IReadOnlyList<PluginApiEntry>> ListAsync() =>
            Task.FromResult<IReadOnlyList<PluginApiEntry>>([new PluginApiEntry("a.b", "A", "1.0.0", "1.5", @"C:\run", @"C:\src", "loaded", null, null, true, "build")]);

        public Task<PluginApiOutcome> LoadAsync(string folder)
        {
            LastCall = "load " + folder;
            return Task.FromResult(new PluginApiOutcome("a.b", "loaded", true, 1, 1, null));
        }

        public Task<PluginApiOutcome> ReloadAsync(string id) => id == "busy"
            ? throw new PluginApiException(PluginApiException.Busy, "Under way.")
            : throw new InvalidOperationException("Something else.");

        public Task<PluginApiOutcome> UnloadAsync(string id) =>
            throw new PluginApiException(PluginApiException.NotFound, $"No plugin \"{id}\".");

        public Task<PluginApiPanelState> OpenPanelAsync(string target)
        {
            LastCall = "open " + target;
            return PanelStateAsync();
        }

        public Task<IReadOnlyList<PluginApiCommand>> ListCommandsAsync() =>
            Task.FromResult<IReadOnlyList<PluginApiCommand>>([new PluginApiCommand("files.copy", "Copy", "Files", "F5")]);

        public Task<PluginApiPanelState> PanelStateAsync() =>
            Task.FromResult(new PluginApiPanelState("a.b", "a:/", "A", null, 1, [new PluginApiPanelEntry("x.txt", false, 5)]));
    }

    private readonly FakeHandler _handler = new();

    private PluginApiServer Server(string pipe = "unused") => new(pipe, _handler, new Version(1, 2, 3));

    private async Task<JsonElement> Ask(string line)
    {
        using var server = Server();
        return JsonDocument.Parse(await server.HandleLineAsync(line)).RootElement;
    }

    [Fact]
    public async Task Version_tells_the_api_and_sdk()
    {
        var reply = await Ask("""{ "id": 7, "method": "api.version" }""");
        Assert.Equal(7, reply.GetProperty("id").GetInt32());
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(PluginApiServer.ApiVersion, reply.GetProperty("result").GetProperty("api").GetInt32());
        Assert.Equal($"{PluginManifest.HostSdkVersion.Major}.{PluginManifest.HostSdkVersion.Minor}", reply.GetProperty("result").GetProperty("sdk").GetString());
    }

    [Fact]
    public async Task List_and_load_reach_the_handler_and_names_are_camel_case()
    {
        var list = await Ask("""{ "id": "x", "method": "plugins.list" }""");
        Assert.Equal("x", list.GetProperty("id").GetString());
        Assert.Equal("a.b", list.GetProperty("result")[0].GetProperty("id").GetString());
        Assert.True(list.GetProperty("result")[0].GetProperty("collectible").GetBoolean());

        var load = await Ask("""{ "method": "plugins.load", "params": { "folder": "C:\\build" } }""");
        Assert.Equal(@"load C:\build", _handler.LastCall);
        Assert.True(load.GetProperty("result").GetProperty("oldVersionFreed").GetBoolean());
        Assert.False(load.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task The_command_list_can_be_read()
    {
        var reply = await Ask("""{ "method": "commands.list" }""");
        Assert.Equal("F5", reply.GetProperty("result")[0].GetProperty("keys").GetString());
    }

    [Fact]
    public async Task A_panel_opens_and_its_rows_can_be_read()
    {
        var open = await Ask("""{ "method": "panels.open", "params": { "target": "a:/" } }""");
        Assert.Equal("open a:/", _handler.LastCall);
        Assert.Equal("a:/", open.GetProperty("result").GetProperty("location").GetString());

        var state = await Ask("""{ "method": "panels.state" }""");
        var row = state.GetProperty("result").GetProperty("entries")[0];
        Assert.Equal("x.txt", row.GetProperty("name").GetString());
        Assert.False(row.GetProperty("isFolder").GetBoolean());
    }

    [Theory]
    [InlineData("not json", PluginApiException.BadRequest)]
    [InlineData("[1]", PluginApiException.BadRequest)]
    [InlineData("""{ "method": "plugins.load" }""", PluginApiException.BadRequest)]
    [InlineData("""{ "method": "plugins.reload", "params": { "id": 5 } }""", PluginApiException.BadRequest)]
    [InlineData("""{ "method": "nothing" }""", PluginApiException.UnknownMethod)]
    [InlineData("""{ "method": "plugins.unload", "params": { "id": "gone" } }""", PluginApiException.NotFound)]
    [InlineData("""{ "method": "plugins.reload", "params": { "id": "busy" } }""", PluginApiException.Busy)]
    [InlineData("""{ "method": "plugins.reload", "params": { "id": "other" } }""", PluginApiException.Internal)]
    public async Task What_goes_wrong_comes_back_with_a_code(string line, string code)
    {
        var reply = await Ask(line);
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(code, reply.GetProperty("error").GetProperty("code").GetString());
        Assert.NotEmpty(reply.GetProperty("error").GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task A_client_talks_over_the_pipe_and_several_lines_work()
    {
        var name = "habi-test-" + Guid.NewGuid().ToString("N");
        using var server = Server(name);
        server.Start();
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000, TestContext.Current.CancellationToken);
        var utf8 = new UTF8Encoding(false);
        await using var writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(pipe, utf8, leaveOpen: true);

        foreach (var id in new[] { 1, 2 })
        {
            await writer.WriteLineAsync($$"""{ "id": {{id}}, "method": "plugins.list" }""");
            var reply = JsonDocument.Parse((await reader.ReadLineAsync(TestContext.Current.CancellationToken))!).RootElement;
            Assert.Equal(id, reply.GetProperty("id").GetInt32());
            Assert.True(reply.GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public void The_pipe_name_follows_the_process_id() => Assert.Equal("habi-plugins-42", PluginApiServer.PipeNameFor(42));
}
