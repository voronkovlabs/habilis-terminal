using System.IO.Compression;
using HabilisTerminal.Core.Plugins;

namespace HabilisTerminal.Core.Tests;

public sealed class PluginStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "habi-tests", "pluginstore-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _shipped;
    private readonly string _user;
    private readonly string _temporary;
    private readonly PluginStore _store;

    public PluginStoreTests()
    {
        _shipped = Path.Combine(_root, "app", "plugins");
        _user = Path.Combine(_root, "user", "plugins");
        _temporary = Path.Combine(_root, "temp");
        _store = new PluginStore(_shipped, _user);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Plugin(string parent, string id, string version = "1.0.0", string? folderName = null)
    {
        var folder = Path.Combine(parent, folderName ?? id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), $$"""{ "id": "{{id}}", "name": "Name of {{id}}", "version": "{{version}}", "assembly": "p.dll", "sdk": "1.0" }""");
        File.WriteAllText(Path.Combine(folder, "p.dll"), "code");
        Directory.CreateDirectory(Path.Combine(folder, "native"));
        File.WriteAllText(Path.Combine(folder, "native", "n.dll"), "native");
        return folder;
    }

    private string Zip(string source, bool withFolder = false)
    {
        var zip = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8] + ".zip");
        ZipFile.CreateFromDirectory(source, zip, CompressionLevel.Fastest, withFolder);
        return zip;
    }

    private string Elsewhere(string id = "my.plugin", string version = "1.0.0") => Plugin(Path.Combine(_root, "elsewhere"), id, version);

    [Fact]
    public void Shipped_and_installed_plugins_are_told_apart()
    {
        Plugin(_shipped, "a.shipped");
        Plugin(_user, "b.user");

        var list = _store.List();

        Assert.Equal(PluginOrigin.Shipped, list.Single(p => p.Id == "a.shipped").Origin);
        Assert.Equal(PluginOrigin.User, list.Single(p => p.Id == "b.user").Origin);
    }

    [Fact]
    public void In_a_portable_copy_the_marker_tells_them_apart_in_one_folder()
    {
        var store = new PluginStore(_shipped, _shipped);
        Plugin(_shipped, "a.shipped");
        using var package = PluginStore.Open(Elsewhere(), _temporary);

        store.Install(package);

        Assert.Equal(PluginOrigin.Shipped, store.List().Single(p => p.Id == "a.shipped").Origin);
        Assert.Equal(PluginOrigin.User, store.List().Single(p => p.Id == "my.plugin").Origin);
    }

    [Fact]
    public void A_plugin_turned_off_stays_on_disk_and_does_not_load_after_a_restart()
    {
        Plugin(_shipped, "a.shipped");
        var keeps = Plugin(_shipped, "b.shipped");

        _store.SetDisabled("A.SHIPPED", true);
        var afterRestart = new PluginStore(_shipped, _user);

        Assert.True(afterRestart.IsDisabled("a.shipped"));
        Assert.Equal(2, afterRestart.List().Count);
        Assert.Equal([keeps], afterRestart.FoldersToLoad());

        afterRestart.SetDisabled("a.shipped", false);
        Assert.Equal(2, new PluginStore(_shipped, _user).FoldersToLoad().Count);
    }

    [Fact]
    public void A_damaged_list_of_plugins_turned_off_turns_none_off()
    {
        Plugin(_shipped, "a.shipped");
        Directory.CreateDirectory(_user);
        File.WriteAllText(Path.Combine(_user, "disabled.json"), "{ not json");

        Assert.False(_store.IsDisabled("a.shipped"));
        Assert.Single(_store.FoldersToLoad());
    }

    [Fact]
    public void A_folder_installs_with_all_its_files()
    {
        using var package = PluginStore.Open(Elsewhere(), _temporary);

        var target = _store.Install(package);

        Assert.Equal(Path.Combine(_user, "my.plugin"), target);
        Assert.True(File.Exists(Path.Combine(target, "native", "n.dll")));
        Assert.Equal(PluginOrigin.User, _store.List().Single().Origin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_zip_installs_with_the_plugin_at_its_top_or_in_one_folder(bool withFolder)
    {
        using var package = PluginStore.Open(Zip(Elsewhere(), withFolder), _temporary);

        var target = _store.Install(package);

        Assert.Equal("my.plugin", package.Manifest.Id);
        Assert.True(File.Exists(Path.Combine(target, "p.dll")));
    }

    [Fact]
    public void The_unpacked_zip_goes_when_done()
    {
        using (PluginStore.Open(Zip(Elsewhere()), _temporary))
        {
            Assert.NotEmpty(Directory.GetDirectories(_temporary));
        }

        Assert.Empty(Directory.GetDirectories(_temporary));
    }

    [Fact]
    public void A_bad_zip_is_refused_and_leaves_nothing()
    {
        var bad = Path.Combine(_root, "bad.zip");
        Directory.CreateDirectory(_root);
        File.WriteAllText(bad, "not a zip");

        Assert.Throws<InvalidDataException>(() => PluginStore.Open(bad, _temporary));
        Assert.Empty(Directory.Exists(_temporary) ? Directory.GetDirectories(_temporary) : []);
    }

    [Fact]
    public void Not_a_plugin_is_refused()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        var text = Path.Combine(_root, "a.txt");
        File.WriteAllText(text, "text");
        var noCode = Elsewhere();
        File.Delete(Path.Combine(noCode, "p.dll"));

        Assert.Contains(PluginManifest.FileName, Assert.Throws<InvalidDataException>(() => PluginStore.Open(empty, _temporary)).Message);
        Assert.Throws<InvalidDataException>(() => PluginStore.Open(text, _temporary));
        Assert.Throws<InvalidDataException>(() => PluginStore.Open(Path.Combine(_root, "nothing"), _temporary));
        Assert.Contains("p.dll", Assert.Throws<InvalidDataException>(() => PluginStore.Open(noCode, _temporary)).Message);
    }

    [Fact]
    public void A_copy_the_shipped_one_would_beat_is_refused()
    {
        Plugin(_shipped, "my.plugin", "2.0.0");

        using var same = PluginStore.Open(Elsewhere(version: "2.0.0"), _temporary);
        using var newer = PluginStore.Open(Plugin(Path.Combine(_root, "newer"), "my.plugin", "2.1.0"), _temporary);

        Assert.NotNull(_store.Refusal(same));
        Assert.Null(_store.Refusal(newer));
    }

    [Fact]
    public void Installing_from_the_plugins_own_folder_is_refused()
    {
        using var package = PluginStore.Open(Plugin(_user, "my.plugin"), _temporary);

        Assert.NotNull(_store.Refusal(package));
    }

    [Fact]
    public void A_second_version_gets_its_own_folder_and_the_first_can_go()
    {
        using var first = PluginStore.Open(Elsewhere(version: "1.0.0"), _temporary);
        var old = _store.Install(first);
        using var second = PluginStore.Open(Plugin(Path.Combine(_root, "second"), "my.plugin", "2.0.0"), _temporary);

        var target = _store.Install(second);

        Assert.NotEqual(old, target);
        Assert.True(_store.Remove(old));
        Assert.Equal("2.0.0", _store.List().Single().Version);
    }

    [Fact]
    public void Only_what_the_user_installed_can_be_removed()
    {
        var shipped = Plugin(_shipped, "a.shipped");
        var outside = Elsewhere();

        Assert.Throws<InvalidOperationException>(() => _store.Remove(shipped));
        Assert.Throws<InvalidOperationException>(() => _store.Remove(outside));
        Assert.True(File.Exists(Path.Combine(shipped, "p.dll")));
        Assert.True(File.Exists(Path.Combine(outside, "p.dll")));
    }

    [Fact]
    public void Removing_a_copy_brings_back_the_one_that_comes_with_the_app()
    {
        Plugin(_shipped, "my.plugin", "1.0.0");
        var user = Plugin(_user, "my.plugin", "2.0.0");
        Assert.Equal("2.0.0", _store.List().Single().Version);

        _store.Remove(user);

        Assert.Equal("1.0.0", _store.List().Single().Version);
    }

    [Fact]
    public void A_locked_plugin_no_longer_counts_and_goes_at_the_next_start()
    {
        var user = Plugin(_user, "my.plugin");
        using (new FileStream(Path.Combine(user, "p.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(_store.Remove(user));
            Assert.Empty(_store.List());
            _store.Purge();
            Assert.Empty(_store.FoldersToLoad());
        }

        _store.Purge();
        Assert.False(Directory.Exists(user));
    }
}
