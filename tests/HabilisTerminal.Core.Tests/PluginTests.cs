using System.Globalization;
using System.Text.Json.Nodes;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Panels;
using HabilisTerminal.Core.Plugins;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.Core.Tests;

/// <summary>A plugin class in this assembly, loaded from a copy of it by <see cref="PluginTests"/>.</summary>
public sealed class SamplePlugin : Plugin
{
    public override IEnumerable<string> Prefixes => ["sample"];
}

public class PluginTests
{
    private const string Valid = """{ "id": "test.sample", "name": "Sample", "version": "1.0.0", "assembly": "Sample.dll", "sdk": "1.0" }""";

    [Fact]
    public void Manifest_reads_all_fields()
    {
        var manifest = PluginManifest.Parse(Valid);
        Assert.Equal(("test.sample", "Sample", "1.0.0", "Sample.dll", "1.0"), (manifest.Id, manifest.Name, manifest.Version, manifest.Assembly, manifest.Sdk));
    }

    [Theory]
    [InlineData("""{ "name": "x", "version": "1", "assembly": "a.dll", "sdk": "1.0" }""", "\"id\"")]
    [InlineData("""{ "id": "bad id!", "name": "x", "version": "1", "assembly": "a.dll", "sdk": "1.0" }""", "may contain only")]
    [InlineData("""{ "id": "a", "name": "x", "version": "1", "assembly": "..\\a.dll", "sdk": "1.0" }""", "file name")]
    [InlineData("""{ "id": "a", "name": "x", "version": "1", "assembly": "a.dll", "sdk": "2.0" }""", "needs SDK 2.0")]
    [InlineData("""{ "id": "a", "name": "x", "version": "1", "assembly": "a.dll", "sdk": "1.9" }""", "needs SDK 1.9")]
    [InlineData("not json", "not valid JSON")]
    public void Manifest_errors_say_what_is_wrong(string json, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => PluginManifest.Parse(json));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Discover_finds_folders_with_a_manifest_and_the_newest_copy_of_a_plugin_wins()
    {
        using var sandbox = new TestSandbox();
        var bundled = Path.Combine(sandbox.Path, "bundled");
        var user = Path.Combine(sandbox.Path, "user");
        WriteManifest(Path.Combine(bundled, "a"), "a", "1.0.0");
        WriteManifest(Path.Combine(user, "a"), "a", "1.0.0"); // same version: the earlier root stays
        WriteManifest(Path.Combine(bundled, "b"), "b", "1.0.0");
        WriteManifest(Path.Combine(user, "b-newer"), "b", "1.2.0"); // a newer copy under another folder name
        WriteManifest(Path.Combine(user, "c"), "c", "2.0.0");
        Directory.CreateDirectory(Path.Combine(user, "no-manifest"));
        Directory.CreateDirectory(Path.Combine(user, "broken"));
        File.WriteAllText(Path.Combine(user, "broken", PluginManifest.FileName), "{ broken");

        var found = PluginLoader.Discover([bundled, user, Path.Combine(sandbox.Path, "missing")]);

        Assert.Equal(
            [Path.Combine(bundled, "a"), Path.Combine(user, "b-newer"), Path.Combine(user, "broken"), Path.Combine(user, "c")],
            found);
    }

    [Fact]
    public void Load_reports_a_missing_assembly_instead_of_throwing()
    {
        using var sandbox = new TestSandbox();
        var folder = Path.Combine(sandbox.Path, "a");
        WriteManifest(folder);

        var loaded = PluginLoader.Load(folder);

        Assert.Null(loaded.Instance);
        Assert.Contains("Sample.dll is missing", loaded.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_creates_the_plugin_in_its_own_load_context_with_the_shared_sdk()
    {
        // A loaded assembly stays locked for the process lifetime (as plugins stay loaded in the app), so this copy
        // lives under the test root rather than in a sandbox deleted at the end of the test.
        var folder = Path.Combine(TestSandbox.RootPath, "plugin-load-" + Guid.NewGuid().ToString("N"), "sample");
        Directory.CreateDirectory(folder);
        var testAssembly = typeof(SamplePlugin).Assembly.Location;
        var name = Path.GetFileName(testAssembly);
        File.Copy(testAssembly, Path.Combine(folder, name));
        File.Copy(Path.ChangeExtension(testAssembly, ".deps.json"), Path.Combine(folder, Path.ChangeExtension(name, ".deps.json")));
        File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), $$"""{ "id": "test.sample", "name": "Sample", "version": "1.0.0", "assembly": "{{name}}", "sdk": "1.0" }""");

        var loaded = PluginLoader.Load(folder);

        Assert.Null(loaded.Error);
        var plugin = Assert.IsAssignableFrom<Plugin>(loaded.Instance);
        Assert.Equal(["sample"], plugin.Prefixes);
        Assert.NotSame(typeof(SamplePlugin), plugin.GetType()); // a separate copy of the assembly
        Assert.Same(typeof(Plugin).Assembly, plugin.GetType().BaseType!.Assembly); // the host's SDK
    }

    private const string TestPluginFile = "HabilisTerminal.TestPlugin.dll";

    private static string MakeTestPluginFolder(string root)
    {
        var folder = Path.Combine(root, "sample");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(AppContext.BaseDirectory, TestPluginFile), Path.Combine(folder, TestPluginFile));
        File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), $$"""{ "id": "test.sample", "name": "Sample", "version": "1.0.0", "assembly": "{{TestPluginFile}}", "sdk": "1.0" }""");
        return folder;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndUnload(string folder, string source)
    {
        var loaded = PluginLoader.Load(folder, source);
        Assert.Null(loaded.Error);
        Assert.Equal(source, loaded.Source);
        Assert.True(loaded.Context!.IsCollectible);
        return PluginLoader.Unload(loaded.Context);
    }

    [Fact]
    public async Task A_plugin_in_a_temporary_copy_unloads_and_its_source_stays_free_to_overwrite()
    {
        using var sandbox = new TestSandbox();
        var source = MakeTestPluginFolder(sandbox.Path);
        var shadows = Path.Combine(sandbox.Path, "shadow");

        var copy = PluginLoader.ShadowCopy(source, shadows);
        Assert.NotEqual(source, copy);
        var weak = LoadAndUnload(copy, source);

        // The source was never locked: a build can write over it while the copy runs.
        File.WriteAllText(Path.Combine(source, TestPluginFile), "rebuilt");

        for (var i = 0; i < 30 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.False(weak.IsAlive, "the load context was not freed");

        // The copy's files follow shortly after: the runtime lets go of them as it finishes the unload.
        var deleted = false;
        for (var i = 0; i < 50 && !deleted; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                Directory.Delete(shadows, recursive: true);
                deleted = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        Assert.True(deleted, "the temporary copy stayed locked");
    }

    [Fact]
    public void Temporary_copies_of_apps_that_have_gone_are_cleaned_and_a_running_ones_stay()
    {
        using var sandbox = new TestSandbox();
        var mine = Directory.CreateDirectory(Path.Combine(sandbox.Path, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "x")).FullName;
        var gone = Directory.CreateDirectory(Path.Combine(sandbox.Path, "999999999", "x")).FullName;
        PluginLoader.CleanShadowCopies(sandbox.Path);
        PluginLoader.CleanShadowCopies(Path.Combine(sandbox.Path, "absent"));
        Assert.True(Directory.Exists(mine));
        Assert.False(Directory.Exists(gone));
    }

    [Fact]
    public void A_plugin_that_cannot_load_keeps_its_source_and_the_reason()
    {
        using var sandbox = new TestSandbox();
        var folder = Path.Combine(sandbox.Path, "bad");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), """{ "id": "bad", "name": "Bad", "version": "1.0.0", "assembly": "Bad.dll", "sdk": "1.0" }""");
        File.WriteAllText(Path.Combine(folder, "Bad.dll"), "not an assembly");

        var loaded = PluginLoader.Load(folder, @"C:\source");

        Assert.Null(loaded.Instance);
        Assert.Equal(@"C:\source", loaded.Source);
        Assert.NotNull(loaded.Error);
        Assert.NotNull(loaded.ErrorDetail);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(""", "collectible": false""", false)]
    [InlineData(""", "collectible": true""", true)]
    public void A_manifest_may_ask_not_to_be_collectible(string extra, bool collectible) =>
        Assert.Equal(collectible, PluginManifest.Parse($$"""{ "id": "a", "name": "x", "version": "1", "assembly": "a.dll", "sdk": "1.0"{{extra}} }""").Collectible);

    [Fact]
    public void Sdk_assembly_version_is_the_sdk_version_not_the_app_version()
    {
        // Plugins bind to this version; a release build's -p:Version once moved it with the app (issue #60).
        var sdk = PluginManifest.HostSdkVersion;
        Assert.Equal(new Version(sdk.Major, sdk.Minor, 0, 0), typeof(Plugin).Assembly.GetName().Version);
    }

    [Fact]
    public void Settings_round_trip_and_a_broken_file_reads_as_empty()
    {
        using var sandbox = new TestSandbox();
        var store = new PluginSettingsStore(sandbox.Path, "test.sample");
        Assert.Empty(store.Load());

        store.Save(new JsonObject { ["profile"] = "dev", ["timeout"] = 30 });
        var loaded = store.Load();
        Assert.Equal("dev", (string?)loaded["profile"]);
        Assert.Equal(30, (int?)loaded["timeout"]);

        File.WriteAllText(store.FilePath, "{ broken");
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Plugin_panels_sort_groups_first_then_by_a_custom_column()
    {
        var day = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        FileEntry Row(string name) => new(name, FileAttributes.Normal, 0, day, day, day);
        var cost = new Dictionary<string, (int Group, double? Cost)>
        {
            ["done-cheap"] = (1, 1.5),
            ["done-dear"] = (1, 9.0),
            ["running"] = (0, 3.0),
            ["done-unknown"] = (1, null),
        };
        var extension = new PanelExtension
        {
            Group = e => cost[e.Name].Group,
            Cell = (e, key) => cost[e.Name].Cost is { } value ? (value.ToString(CultureInfo.InvariantCulture), value) : null,
        };
        var state = new PanelState { Culture = CultureInfo.InvariantCulture };
        state.SetListing("sm:dev/eu-west-1", [Row("done-dear"), Row("running"), Row("done-unknown"), Row("done-cheap")], hasParent: true);
        state.SetExtension(extension);

        // Name order inside each group; the running job first.
        Assert.Equal(["..", "running", "done-cheap", "done-dear", "done-unknown"], Names(state));

        state.ApplyCustomSortKey("cost");
        Assert.Equal(["..", "running", "done-cheap", "done-dear", "done-unknown"], Names(state));

        state.ApplyCustomSortKey("cost"); // again: descending; missing values stay last
        Assert.Equal(["..", "running", "done-dear", "done-cheap", "done-unknown"], Names(state));

        state.ApplySortKey(SortMode.Name); // a built-in key drops the custom sort
        Assert.Null(extension.SortKey);
        Assert.Equal(["..", "running", "done-cheap", "done-dear", "done-unknown"], Names(state));
    }

    [Fact]
    public void Custom_values_of_mixed_types_compare_without_throwing()
    {
        Assert.True(PanelExtension.CompareValues(1, 2.5) < 0);
        Assert.True(PanelExtension.CompareValues(TimeSpan.FromMinutes(1), TimeSpan.FromHours(1)) < 0);
        Assert.True(PanelExtension.CompareValues("a", 3) > 0); // text after number, by text
        Assert.True(PanelExtension.CompareValues(null, 3) > 0);
    }

    private static string[] Names(PanelState state) => Enumerable.Range(0, state.Count).Select(i => state.ItemAt(i).Name).ToArray();

    private static void WriteManifest(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), Valid);
    }

    private static void WriteManifest(string folder, string id, string version)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), $$"""{ "id": "{{id}}", "name": "{{id}}", "version": "{{version}}", "assembly": "{{id}}.dll", "sdk": "1.0" }""");
    }
}
