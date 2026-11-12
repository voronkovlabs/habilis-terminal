using HabilisTerminal.Core.Panels;
using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Jsonc_values_override_defaults()
    {
        var result = SettingsDocument.Parse("""
            {
                // comment
                "$schema": "./settings.schema.json",
                "theme": "light_modern",
                "terminal.fontSize": 14, /* inline */
                "deleteToRecycleBin": false,
            }
            """);

        Assert.False(result.IsSyntaxError);
        Assert.Empty(result.Errors);
        var settings = new AppSettings(result.Values);
        Assert.Equal("light_modern", settings.Theme);
        Assert.Equal(14, settings.TerminalFontSize);
        Assert.False(settings.DeleteToRecycleBin);
        Assert.True(settings.UseAccentColor);
    }

    [Fact]
    public void Syntax_errors_report_the_line()
    {
        var result = SettingsDocument.Parse("{\n  \"theme\": \"auto\",\n  \"confirmExit\" false\n}");

        Assert.True(result.IsSyntaxError);
        Assert.Equal(3, Assert.Single(result.Errors).Line);
    }

    [Fact]
    public void Invalid_values_are_reported_with_their_line_and_skipped()
    {
        var result = SettingsDocument.Parse("{\n  \"terminal.fontSize\": 500,\n  \"iconTheme\": \"nope\",\n  \"confirmExit\": \"yes\",\n  \"mystery\": 1,\n  \"theme\": \"dark_plus\"\n}");

        Assert.False(result.IsSyntaxError);
        Assert.Equal([2, 3, 4, 5], result.Errors.Select(e => e.Line));
        Assert.Equal(["confirmExit", "iconTheme", "terminal.fontSize"], result.InvalidKeys.Order());
        Assert.Equal("dark_plus", Assert.Single(result.Values).Value);
    }

    [Fact]
    public void Enum_values_are_case_insensitive()
    {
        var result = SettingsDocument.Parse("""{ "iconTheme": "Material" }""");
        Assert.Equal("material", result.Values["iconTheme"]);
    }

    [Fact]
    public void Setting_a_value_keeps_comments_and_formatting()
    {
        var text = "{\r\n    // my theme\r\n    \"theme\": \"auto\", // trailing\r\n    \"confirmExit\": true\r\n}\r\n";

        var changed = SettingsDocument.SetValue(text, "theme", "hc_black");
        Assert.Equal("{\r\n    // my theme\r\n    \"theme\": \"hc_black\", // trailing\r\n    \"confirmExit\": true\r\n}\r\n", changed);

        var added = SettingsDocument.SetValue(changed, "terminal.fontSize", 15);
        Assert.Equal("{\r\n    // my theme\r\n    \"theme\": \"hc_black\", // trailing\r\n    \"confirmExit\": true,\r\n    \"terminal.fontSize\": 15\r\n}\r\n", added);
        Assert.Equal(15, SettingsDocument.Parse(added).Values["terminal.fontSize"]);
    }

    [Fact]
    public void Removing_a_value_removes_its_line()
    {
        var text = "{\n    \"theme\": \"hc_black\",\n    \"confirmExit\": false\n}\n";

        Assert.Equal("{\n    \"confirmExit\": false\n}\n", SettingsDocument.SetValue(text, "theme", null));
        var last = SettingsDocument.SetValue(text, "confirmExit", null);
        Assert.Equal("{\n    \"theme\": \"hc_black\",\n}\n", last);
        Assert.False(SettingsDocument.Parse(last).IsSyntaxError);
    }

    [Fact]
    public void Adding_to_an_empty_object_or_file()
    {
        Assert.Equal("{\n    \"confirmExit\": false\n}\n", SettingsDocument.SetValue(string.Empty, "confirmExit", false));
        Assert.Equal("{\n    \"confirmExit\": false\n}", SettingsDocument.SetValue("{}", "confirmExit", false));
    }

    [Fact]
    public void Store_keeps_last_valid_values_when_the_file_breaks()
    {
        using var sandbox = new TestSandbox();
        var path = Path.Combine(sandbox.Path, "settings.json");
        File.WriteAllText(path, """{ "theme": "hc_light", "terminal.fontSize": 13 }""");
        using var store = new SettingsStore(path);
        Assert.Equal("hc_light", store.Load().Theme);

        File.WriteAllText(path, """{ "theme": "dark_plus", """);
        Assert.Equal("hc_light", store.Load().Theme);
        Assert.Single(store.Errors);

        File.WriteAllText(path, """{ "theme": "dark_plus", "terminal.fontSize": "big" }""");
        var settings = store.Load();
        Assert.Equal("dark_plus", settings.Theme);
        Assert.Equal(13, settings.TerminalFontSize);
    }

    [Fact]
    public void Store_writes_only_changed_values_and_resets_to_defaults()
    {
        using var sandbox = new TestSandbox();
        var path = Path.Combine(sandbox.Path, "settings.json");
        using var store = new SettingsStore(path);

        Assert.False(store.Set("confirmExit", false).ConfirmExit);
        Assert.Contains("\"confirmExit\": false", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("$schema", File.ReadAllText(path), StringComparison.Ordinal);

        Assert.True(store.Set("confirmExit", true).ConfirmExit);
        Assert.DoesNotContain("confirmExit", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(sandbox.Path));
    }

    [Fact]
    public async Task Store_reloads_when_the_file_changes()
    {
        using var sandbox = new TestSandbox();
        var path = Path.Combine(sandbox.Path, "settings.json");
        using var store = new SettingsStore(path);
        store.Load();
        var changed = new TaskCompletionSource<AppSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += s => changed.TrySetResult(s);
        store.Watch();

        File.WriteAllText(path, """{ "viewer.wrap": true }""");

        var settings = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(settings.ViewerWrap);
    }

    [Fact]
    public void Schema_lists_every_setting()
    {
        using var schema = System.Text.Json.JsonDocument.Parse(SettingsCatalog.Schema());
        var properties = schema.RootElement.GetProperty("properties");
        foreach (var definition in SettingsCatalog.All)
        {
            Assert.True(properties.TryGetProperty(definition.Key, out _), definition.Key);
            Assert.Contains(definition.Category, SettingsCatalog.Categories);
        }
    }

    [Fact]
    public void Portable_mode_is_detected_by_marker_or_settings_file()
    {
        using var sandbox = new TestSandbox();
        var exe = Directory.CreateDirectory(Path.Combine(sandbox.Path, "app")).FullName;
        var roaming = Path.Combine(sandbox.Path, "roaming");
        var local = Path.Combine(sandbox.Path, "local");

        var installed = AppPaths.Resolve(exe, roaming, local);
        Assert.False(installed.IsPortable);
        Assert.Equal(Path.Combine(roaming, AppPaths.ProductFolder, "settings.json"), installed.SettingsFile);
        Assert.Equal(Path.Combine(local, "VoronkovLabs", AppPaths.ProductFolder, "state.json"), installed.StateFile);

        File.WriteAllText(Path.Combine(exe, "portable"), string.Empty);
        var portable = AppPaths.Resolve(exe, roaming, local);
        Assert.True(portable.IsPortable);
        Assert.Equal(Path.Combine(exe, "state.json"), portable.StateFile);
        Assert.Equal(Path.Combine(exe, "themes"), portable.ThemesDirectory);
    }

    [Fact]
    public void The_first_start_copies_the_wpf_builds_settings_once()
    {
        using var sandbox = new TestSandbox();
        var roaming = Path.Combine(sandbox.Path, "roaming");
        var wpf = Directory.CreateDirectory(Path.Combine(roaming, AppPaths.WpfProductFolder)).FullName;
        File.WriteAllText(Path.Combine(wpf, "settings.json"), "{ \"theme\": \"light_modern\" }");
        Directory.CreateDirectory(Path.Combine(wpf, "themes"));
        File.WriteAllText(Path.Combine(wpf, "themes", "mine.json"), "{}");
        Directory.CreateDirectory(Path.Combine(wpf, "plugin-settings"));
        File.WriteAllText(Path.Combine(wpf, "plugin-settings", "demo.json"), "{}");

        var paths = AppPaths.Resolve(Path.Combine(sandbox.Path, "app"), roaming, Path.Combine(sandbox.Path, "local"));
        Assert.NotEqual(Path.Combine(roaming, AppPaths.WpfProductFolder), paths.SettingsDirectory);
        AppPaths.CopyWpfSettings(paths, roaming);

        Assert.Contains("light_modern", File.ReadAllText(paths.SettingsFile), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(paths.ThemesDirectory, "mine.json")));
        Assert.True(File.Exists(Path.Combine(paths.PluginSettingsDirectory, "demo.json")));

        // Afterwards each build keeps its own.
        File.WriteAllText(paths.SettingsFile, "{}");
        AppPaths.CopyWpfSettings(paths, roaming);
        Assert.Equal("{}", File.ReadAllText(paths.SettingsFile));
    }

    [Fact]
    public void Installed_data_moves_out_of_the_package_root_once()
    {
        using var sandbox = new TestSandbox();
        var local = Path.Combine(sandbox.Path, "local");
        var legacy = Directory.CreateDirectory(Path.Combine(local, AppPaths.ProductFolder)).FullName;
        File.WriteAllText(Path.Combine(legacy, "state.json"), "state");
        File.WriteAllText(Path.Combine(legacy, "package.nupkg"), "package");
        Directory.CreateDirectory(Path.Combine(legacy, "plugins", "mine"));
        File.WriteAllText(Path.Combine(legacy, "plugins", "mine", "plugin.json"), "plugin");
        Directory.CreateDirectory(Path.Combine(legacy, "crashes"));
        File.WriteAllText(Path.Combine(legacy, "crashes", "crash.json"), "crash");
        Directory.CreateDirectory(Path.Combine(legacy, "diagnostics"));
        File.WriteAllText(Path.Combine(legacy, "diagnostics", "report.json"), "report");
        Directory.CreateDirectory(Path.Combine(legacy, "WebView2"));

        var paths = AppPaths.Resolve(Path.Combine(legacy, "current"), Path.Combine(sandbox.Path, "roaming"), local);
        AppPaths.MigrateLegacyData(paths, local);

        Assert.Equal("state", File.ReadAllText(paths.StateFile));
        Assert.True(File.Exists(Path.Combine(paths.PluginsDirectory, "mine", "plugin.json")));
        Assert.True(File.Exists(Path.Combine(paths.CrashDirectory, "crash.json")));
        Assert.True(File.Exists(Path.Combine(paths.DiagnosticsDirectory, "report.json")));
        Assert.False(File.Exists(Path.Combine(legacy, "state.json")));
        Assert.False(Directory.Exists(Path.Combine(legacy, "plugins")));
        Assert.True(File.Exists(Path.Combine(legacy, "package.nupkg")));
        Assert.True(Directory.Exists(Path.Combine(legacy, "WebView2")));
    }

    [Fact]
    public void Existing_new_data_wins_over_legacy_data()
    {
        using var sandbox = new TestSandbox();
        var local = Path.Combine(sandbox.Path, "local");
        var legacy = Directory.CreateDirectory(Path.Combine(local, AppPaths.ProductFolder)).FullName;
        File.WriteAllText(Path.Combine(legacy, "state.json"), "old");
        var paths = AppPaths.Resolve(Path.Combine(legacy, "current"), Path.Combine(sandbox.Path, "roaming"), local);
        Directory.CreateDirectory(paths.StateDirectory);
        File.WriteAllText(paths.StateFile, "new");

        AppPaths.MigrateLegacyData(paths, local);

        Assert.Equal("new", File.ReadAllText(paths.StateFile));
        Assert.Equal("old", File.ReadAllText(Path.Combine(legacy, "state.json")));
    }

    [Fact]
    public void Portable_data_is_never_migrated()
    {
        using var sandbox = new TestSandbox();
        var local = Path.Combine(sandbox.Path, "local");
        var legacy = Directory.CreateDirectory(Path.Combine(local, AppPaths.ProductFolder)).FullName;
        File.WriteAllText(Path.Combine(legacy, "state.json"), "installed");
        var portableDirectory = Directory.CreateDirectory(Path.Combine(sandbox.Path, "portable")).FullName;
        File.WriteAllText(Path.Combine(portableDirectory, AppPaths.PortableMarker), string.Empty);
        var paths = AppPaths.Resolve(portableDirectory, Path.Combine(sandbox.Path, "roaming"), local);

        AppPaths.MigrateLegacyData(paths, local);

        Assert.False(File.Exists(paths.StateFile));
        Assert.True(File.Exists(Path.Combine(legacy, "state.json")));
    }

    [Fact]
    public async Task State_saved_in_the_background_reaches_the_disk()
    {
        using var sandbox = new TestSandbox();
        var store = new StateStore(Path.Combine(sandbox.Path, "state.json"));
        await store.SaveInBackground(new AppState { ActivePanel = "Right" });
        Assert.Equal("Right", store.Load().ActivePanel);
    }

    [Fact]
    public async Task A_late_background_save_does_not_overwrite_a_newer_state()
    {
        using var sandbox = new TestSandbox();
        var store = new StateStore(Path.Combine(sandbox.Path, "state.json"));
        var saves = Enumerable.Range(1, 50).Select(i => store.SaveInBackground(new AppState { TerminalRows = i })).ToList();
        store.Save(new AppState { TerminalRows = 99 });
        await Task.WhenAll(saves);
        Assert.Equal(99, store.Load().TerminalRows);
    }

    [Fact]
    public void An_unchanged_state_leaves_the_disk_alone()
    {
        using var sandbox = new TestSandbox();
        var path = Path.Combine(sandbox.Path, "state.json");
        var store = new StateStore(path);
        store.Save(new AppState { TerminalRows = 5 });
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, old);

        store.Save(new AppState { TerminalRows = 5 });
        Assert.Equal(old, File.GetLastWriteTimeUtc(path));

        store.Save(new AppState { TerminalRows = 6 });
        Assert.NotEqual(old, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void State_round_trips_and_survives_a_broken_file()
    {
        using var sandbox = new TestSandbox();
        var store = new StateStore(Path.Combine(sandbox.Path, "state.json"));
        var state = new AppState
        {
            Left = new PanelSnapshot { Path = @"C:\Work", ViewMode = PanelViewMode.Brief, Sort = FileSystem.SortMode.Size, SortDescending = true },
            Right = new PanelSnapshot { Visible = false },
            ActivePanel = "Right",
            TerminalRows = 9,
            Window = new WindowPlacement(10, 20, 800, 600, true),
            FolderHistory = [@"C:\A", @"C:\B"],
            FileHistory = [new FileHistoryEntry(@"C:\A\x.txt", true)],
        };

        store.Save(state);
        var loaded = store.Load();

        Assert.Equal(state.Left, loaded.Left);
        Assert.Equal(state.Right, loaded.Right);
        Assert.Equal(state.Window, loaded.Window);
        Assert.Equal(state.FolderHistory, loaded.FolderHistory);
        Assert.Equal(state.FileHistory, loaded.FileHistory);
        Assert.Equal(9, loaded.TerminalRows);

        File.WriteAllText(store.FilePath, "{ broken");
        Assert.Equal(new AppState().ActivePanel, store.Load().ActivePanel);
    }
}
