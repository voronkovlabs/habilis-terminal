namespace HabilisTerminal.Plugins;

/// <summary>
/// A plugin's entry point. The plugin assembly has exactly one public non-abstract class derived from this; the host
/// creates it with its parameterless constructor and calls <see cref="Initialize"/> once.
/// </summary>
public abstract class Plugin
{
    /// <summary>The host, available from <see cref="Initialize"/> on.</summary>
    protected IPluginHost Host { get; private set; } = null!;

    /// <summary>Called once after loading. Keep it fast: no network calls.</summary>
    public virtual void Initialize(IPluginHost host) => Host = host;

    /// <summary>Items of the plugin menu (F11).</summary>
    public virtual IEnumerable<PluginMenuItem> MenuItems => [];

    /// <summary>Items of the drive menu (Alt+F1 / Alt+F2), under the plugin's name.</summary>
    public virtual IEnumerable<PluginMenuItem> DriveMenuItems => [];

    /// <summary>
    /// Command-line prefixes without the colon, e.g. "sqs". Enter on a line starting with "sqs:" calls
    /// <see cref="OpenAddressAsync"/> with the whole line instead of running it in the shell.
    /// </summary>
    public virtual IEnumerable<string> Prefixes => [];

    /// <summary>
    /// Shell commands to catch in front of this plugin's addresses, e.g. "cd" (SDK 1.4). Enter on "cd s3://bucket/",
    /// quoted or not, calls <see cref="OpenAddressAsync"/> with "s3://bucket/" instead of running the line in the shell.
    /// An address needs one of <see cref="Prefixes"/>; the same command with anything else runs as usual.
    /// </summary>
    public virtual IEnumerable<string> AddressCommands => [];

    /// <summary>
    /// Opens a panel for an address: a command line starting with one of <see cref="Prefixes"/> or one of
    /// <see cref="AddressCommands"/>, or a location the plugin reported earlier through
    /// <see cref="IPanelProvider.Location"/> (folder history). Null: nothing to open.
    /// </summary>
    public virtual Task<IPanelProvider?> OpenAddressAsync(string address, CancellationToken cancellationToken) =>
        Task.FromResult<IPanelProvider?>(null);

    /// <summary>
    /// File name endings this plugin opens as a folder, without the dot: "zip", "tar.gz" (SDK 1.5). Enter on such a file
    /// calls <see cref="OpenFileAsync"/>. Claim only files that are containers to the user: a document or a program
    /// should go on opening in its own program. A plugin with none here is not asked about files at all.
    /// </summary>
    public virtual IEnumerable<string> FileExtensions => [];

    /// <summary>
    /// Opens a local file as a folder, like going inside an archive (SDK 1.5). Null: the file is not this plugin's; Enter
    /// then does what it does with any file. Called off the UI thread. ".." at the top of the panel returns to the file.
    /// </summary>
    public virtual Task<IPanelProvider?> OpenFileAsync(FileOpenRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IPanelProvider?>(null);

    /// <summary>
    /// The plugin is being unloaded (SDK 1.5): its panels are already closed. Stop timers and threads, and free native
    /// libraries. What is left running keeps the old code in memory, and an exception here is only reported.
    /// </summary>
    public virtual Task ShutdownAsync() => Task.CompletedTask;

    /// <summary>
    /// Commands available anywhere while the plugin is loaded (SDK 1.6): in the command line (Ctrl+Shift+P), under the
    /// plugin's name. Commands of an open panel belong in <see cref="IPanelCommands"/>; <see cref="MenuItems"/> appear
    /// there by themselves.
    /// </summary>
    public virtual IEnumerable<PluginCommand> Commands => [];

    /// <summary>The plugin has a configuration dialog (F9 > Options > Plugins).</summary>
    public virtual bool HasConfiguration => false;

    /// <summary>Shows the configuration dialog through <see cref="IPluginHost.Dialogs"/>.</summary>
    public virtual Task ConfigureAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A command available anywhere. <paramref name="Key"/> uses the host's notation, e.g. "Ctrl+Shift+F5"; null: none. A key the
/// host itself uses is ignored and not shown, as the host's keys win. Leave Ctrl+Alt+letter alone: it is the quick filter.
/// </summary>
public sealed record PluginCommand(string Title, string? Key, Func<CancellationToken, Task> Run);

/// <summary>What <see cref="Plugin.OpenFileAsync"/> is asked to open.</summary>
/// <param name="Path">The full path of the local file.</param>
/// <param name="InnerPath">A place inside it to show, as <c>docs\sub</c> for <c>C:\a.zip\docs\sub</c>, or empty. It comes from folder history: when it no longer exists, show the top.</param>
/// <param name="AnyFormat">Ctrl+PgDn, or a location from history: look at the content whatever the file is called. Otherwise the file's name matched <see cref="Plugin.FileExtensions"/>.</param>
public sealed record FileOpenRequest(string Path, string InnerPath, bool AnyFormat);

/// <summary>A menu entry: its title and what it opens (a panel, or null after doing something else).</summary>
public sealed record PluginMenuItem(string Title, Func<CancellationToken, Task<IPanelProvider?>> Open)
{
    /// <summary>A preferred letter or digit when the user has not assigned another hotkey.</summary>
    public char? DefaultHotkey { get; init; }
}

/// <summary>An error for the user to read. Any other exception from a plugin is shown as an internal error.</summary>
public class PluginException : Exception
{
    public PluginException()
    {
    }

    public PluginException(string message)
        : base(message)
    {
    }

    public PluginException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
