namespace HabilisTerminal.Core.Settings;

/// <summary>An immutable snapshot of effective settings: values from settings.json over the catalog defaults.</summary>
public sealed class AppSettings
{
    private readonly IReadOnlyDictionary<string, object> _values;

    public AppSettings(IReadOnlyDictionary<string, object>? values = null) =>
        _values = values ?? new Dictionary<string, object>();

    public static AppSettings Defaults { get; } = new();

    /// <summary>Values set in settings.json (only those that passed validation).</summary>
    public IReadOnlyDictionary<string, object> UserValues => _values;

    public string Theme => Get<string>("theme");

    public bool UseAccentColor => Get<bool>("useAccentColor");

    public string IconTheme => Get<string>("iconTheme");

    public string PanelsFontFamily => Get<string>("panels.fontFamily");

    public int PanelsFontSize => Get<int>("panels.fontSize");

    public bool PanelsShowHidden => Get<bool>("panels.showHidden");

    public bool PanelsFolderSizes => Get<bool>("panels.folderSizes");

    public bool DiagnosticsPerformance => Get<bool>("diagnostics.performance");

    public string TerminalFontFamily => Get<string>("terminal.fontFamily");

    public int TerminalFontSize => Get<int>("terminal.fontSize");

    public int TerminalDefaultRows => Get<int>("terminal.defaultRows");

    public bool TerminalMultilinePasteWarning => Get<bool>("terminal.multilinePasteWarning");

    public string ShellPath => Get<string>("shell.path");

    public string ShellArgs => Get<string>("shell.args");

    public bool ShellIntegration => Get<bool>("shell.integration");

    public string ShellInitCommand => Get<string>("shell.initCommand");

    public bool DeleteToRecycleBin => Get<bool>("deleteToRecycleBin");

    public bool ConfirmExit => Get<bool>("confirmExit");

    public bool ConfirmDelete => Get<bool>("confirmDelete");

    public bool ViewerWrap => Get<bool>("viewer.wrap");

    public string ViewerFontFamily => Get<string>("viewer.fontFamily");

    public int ViewerFontSize => Get<int>("viewer.fontSize");

    public int ViewerTextModeMaxMB => Get<int>("viewer.textModeMaxMB");

    public bool EditorSyntaxHighlighting => Get<bool>("editor.syntaxHighlighting");

    public int EditorLargeFileThresholdMB => Get<int>("editor.largeFileThresholdMB");

    public string EditorFontFamily => Get<string>("editor.fontFamily");

    public int EditorFontSize => Get<int>("editor.fontSize");

    public int EditorTabSize => Get<int>("editor.tabSize");

    public bool EditorInsertSpaces => Get<bool>("editor.insertSpaces");

    public bool IsModified(string key) => _values.ContainsKey(key);

    public object GetValue(string key) =>
        _values.TryGetValue(key, out var value) ? value : (SettingsCatalog.Find(key) ?? throw new KeyNotFoundException(key)).Default;

    public T Get<T>(string key) => (T)GetValue(key);
}
