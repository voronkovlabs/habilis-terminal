using System.Net.Http;
using Avalonia.Threading;
using HabilisTerminal.Core.Settings;
using Velopack;
using Velopack.Sources;

namespace HabilisTerminal.App.Updates;

/// <summary>What "Check for updates" found (issue #71).</summary>
public enum UpdateCheckKind
{
    /// <summary>The running version is the latest.</summary>
    UpToDate,

    /// <summary>An installed copy downloaded an update: restart to install it.</summary>
    Ready,

    /// <summary>A portable or development build: a newer release exists to download by hand.</summary>
    Available,

    /// <summary>The check could not be made (offline, rate-limited...).</summary>
    Failed,
}

public sealed record UpdateCheck(UpdateCheckKind Kind, string Version, string? Problem = null);

/// <summary>
/// Updates from GitHub Releases (SPEC §15). Installed copies check in the background after start and on request (F9 >
/// Options > Check for updates): a found update is downloaded quietly; the window then offers "Restart to update", and
/// otherwise it is applied when the app exits. Portable and development builds only learn on request whether a newer
/// release exists.
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/voronkovlabs/habilis-terminal";
    public const string ReleasesUrl = RepositoryUrl + "/releases/latest";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(15);
    private readonly Lock _gate = new();
    private Task<UpdateCheck>? _check;
    private UpdateManager? _manager;
    private UpdateInfo? _ready;
    private Dispatcher? _dispatcher;

    /// <summary>An update was downloaded: its version, ready for <see cref="RestartToUpdate"/>.</summary>
    public event Action<string>? UpdateReady;

    /// <summary>The installed version, or null for portable and development builds.</summary>
    public string? InstalledVersion => _manager is { IsInstalled: true } manager ? manager.CurrentVersion?.ToString() : null;

    /// <summary>The running version from the assembly (release builds carry the tag's version).</summary>
    public static string RunningVersion =>
        typeof(UpdateService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private bool UsesVelopack => _manager is { IsInstalled: true } && !App.Paths.IsPortable;

    public void Start(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        try
        {
            // The Avalonia build's releases are prereleases on their own channel (avalonia-win-x64, issue #256), so the WPF
            // build never sees them; this one looks at prereleases too.
            _manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: true));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return;
        }

        if (!UsesVelopack)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(FirstCheckDelay).ConfigureAwait(false);
            await CheckNowAsync().ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Checks now (joining a check already running); for an installed copy a found update is downloaded and
    /// <see cref="UpdateReady"/> raised.
    /// </summary>
    public Task<UpdateCheck> CheckNowAsync()
    {
        lock (_gate)
        {
            if (_check is not { IsCompleted: false })
            {
                _check = CheckAsync();
            }

            return _check;
        }
    }

    private async Task<UpdateCheck> CheckAsync()
    {
        try
        {
            if (_ready is not null)
            {
                return new UpdateCheck(UpdateCheckKind.Ready, _ready.TargetFullRelease.Version.ToString());
            }

            if (!UsesVelopack)
            {
                return await CheckLatestReleaseAsync().ConfigureAwait(false);
            }

            var update = await _manager!.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                return new UpdateCheck(UpdateCheckKind.UpToDate, InstalledVersion ?? RunningVersion);
            }

            await _manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
            _ready = update;
            var version = update.TargetFullRelease.Version.ToString();
            if (_dispatcher is { } dispatcher)
            {
                await dispatcher.InvokeAsync(() => UpdateReady?.Invoke(version));
            }

            return new UpdateCheck(UpdateCheckKind.Ready, version);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or InvalidOperationException or TaskCanceledException or UnauthorizedAccessException)
        {
            // Offline, rate-limited or no release yet: the background check tries again on the next start.
            return new UpdateCheck(UpdateCheckKind.Failed, RunningVersion, ex.Message);
        }
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

    /// <summary>Portable and development builds: GitHub's latest release against the running version.</summary>
    private static async Task<UpdateCheck> CheckLatestReleaseAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"HabilisTerminal/{RunningVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var json = await http.GetStringAsync(RepositoryUrl.Replace("https://github.com/", "https://api.github.com/repos/", StringComparison.Ordinal) + "/releases/latest").ConfigureAwait(false);
        var tag = ReleaseVersions.TagOf(json) ?? throw new InvalidOperationException("GitHub did not name the latest release.");
        return ReleaseVersions.Newer(tag, RunningVersion) is { } newer
            ? new UpdateCheck(UpdateCheckKind.Available, newer.ToString())
            : new UpdateCheck(UpdateCheckKind.UpToDate, RunningVersion);
    }
}
