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

    /// <summary>The plugin has a configuration dialog (F9 > Options > Plugins).</summary>
    public virtual bool HasConfiguration => false;

    /// <summary>Shows the configuration dialog through <see cref="IPluginHost.Dialogs"/>.</summary>
    public virtual Task ConfigureAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A menu entry: its title and what it opens (a panel, or null after doing something else).</summary>
public sealed record PluginMenuItem(string Title, Func<CancellationToken, Task<IPanelProvider?>> Open);

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
