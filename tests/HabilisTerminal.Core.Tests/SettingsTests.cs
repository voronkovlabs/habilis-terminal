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
        Assert.Equal(Path.Combine(roaming, "HabilisTerminal", "settings.json"), installed.SettingsFile);
        Assert.Equal(Path.Combine(local, "HabilisTerminal", "state.json"), installed.StateFile);

        File.WriteAllText(Path.Combine(exe, "portable"), string.Empty);
        var portable = AppPaths.Resolve(exe, roaming, local);
        Assert.True(portable.IsPortable);
        Assert.Equal(Path.Combine(exe, "state.json"), portable.StateFile);
        Assert.Equal(Path.Combine(exe, "themes"), portable.ThemesDirectory);
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
