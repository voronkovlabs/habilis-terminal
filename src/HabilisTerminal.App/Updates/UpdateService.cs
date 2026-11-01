using System.Net.Http;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace HabilisTerminal.App.Updates;

/// <summary>
/// Background updates from GitHub Releases (SPEC §15): only for installed copies (not portable or development builds).
/// A found update is downloaded quietly; the window then offers "Restart to update", and otherwise it is applied when the
/// app exits.
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/voronkovlabs/habilis-terminal";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(15);
    private UpdateManager? _manager;
    private UpdateInfo? _ready;

    /// <summary>An update was downloaded: its version, ready for <see cref="RestartToUpdate"/>.</summary>
    public event Action<string>? UpdateReady;

    /// <summary>The installed version, or null for portable and development builds.</summary>
    public string? InstalledVersion => _manager is { IsInstalled: true } manager ? manager.CurrentVersion?.ToString() : null;

    public void Start(Dispatcher dispatcher)
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return;
        }

        if (!_manager.IsInstalled || App.Paths.IsPortable)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(FirstCheckDelay).ConfigureAwait(false);
            try
            {
                var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
                if (update is null)
                {
                    return;
                }

                await _manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
                _ready = update;
                var version = update.TargetFullRelease.Version.ToString();
                await dispatcher.BeginInvoke(() => UpdateReady?.Invoke(version));
            }
            catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or InvalidOperationException or TaskCanceledException or UnauthorizedAccessException)
            {
                // Offline, rate-limited or no release yet: try again on the next start.
            }
        });
    }

    /// <summary>Exits, applies the downloaded update and starts the new version.</summary>
    public void RestartToUpdate()
    {
        if (_manager is not null && _ready is not null)
        {
            _manager.ApplyUpdatesAndRestart(_ready);
        }
    }

    /// <summary>Called on exit: a downloaded update is installed after the process ends, without restarting.</summary>
    public void ApplyPendingOnExit()
    {
        if (_manager is not null && _ready is not null)
        {
            _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: false);
            _ready = null;
        }
    }
}
