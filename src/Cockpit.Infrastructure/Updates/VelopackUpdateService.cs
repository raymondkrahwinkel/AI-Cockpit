using System.Reflection;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Updates;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Sources;

namespace Cockpit.Infrastructure.Updates;

// Asks the update feed whether a newer cockpit exists (#71, AC-387), through the same `UpdateManager`
// that will later fetch and apply it. One source, deliberately, so the banner and the updater cannot disagree.
// A failed check returns a failure rather than the lie "you are up to date".
internal sealed class VelopackUpdateService(ILogger<VelopackUpdateService> logger) : IUpdateService, ISingletonService
{
    private const string RepositoryUrl = "https://github.com/raymondkrahwinkel/AI-Cockpit";

    // The rolling tag every nightly is published onto — one release, replaced each night.
    private const string NightlyTag = "nightly";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public (string Version, string Commit) Current { get; } = _Read(typeof(VelopackUpdateService).Assembly);

    // The build a successful `DownloadAsync` fetched, and the manager that fetched it — the only two things
    // `ApplyDownloadedUpdateAndRestart`/`RequestUpdateOnNextStart` need. Kept on this `ISingletonService`
    // instance because there is deliberately no way to apply anything but the one build just fetched.
    private UpdateManager? _pendingManager;
    private VelopackAsset? _pendingRelease;

    public Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken cancellationToken = default) =>
        CheckAsync(channel, Source, locator: null, logger, Patience, cancellationToken);

    // The check, with the feed and the installation handed in. Velopack answers from an installation on disk, so a
    // test reaches this by supplying a source of its own and a locator that stands in for one — and what the source
    // is asked for is the channel this decides on, which is the part most worth pinning down.
    internal static async Task<UpdateCheckResult> CheckAsync(
        UpdateChannel channel,
        Func<UpdateChannel, IUpdateSource> source,
        IVelopackLocator? locator,
        ILogger logger,
        TimeSpan patience,
        CancellationToken cancellationToken)
    {
        // Two ways to be a copy the installer never placed, neither an error: no locator at all (a test host), or
        // a locator with no installed version (a checkout/tarball). Asked here rather than caught, because
        // reaching this through an exception would surface the library's own wording to the operator instead.
        if ((locator ?? (VelopackLocator.IsCurrentSet ? VelopackLocator.Current : null))?.CurrentlyInstalledVersion is null)
        {
            return _NotPackaged();
        }

        try
        {
            // Built per check rather than cached: the channel is a construction option, and the operator can change
            // it while the cockpit is running.
            var manager = new UpdateManager(
                source(channel),
                new UpdateOptions { ExplicitChannel = UpdateChannelName.For(channel) },
                locator);

            var check = manager.CheckForUpdatesAsync();

            using var waited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waited.CancelAfter(patience);

            // Velopack's check takes no cancellation token, so a slow feed is waited out rather than stopped: the
            // task is left to finish into nothing. Whatever it ends up doing is not watched — an unobserved task
            // exception is swallowed by the runtime, which this project does not opt out of.
            if (await Task.WhenAny(check, Task.Delay(Timeout.Infinite, waited.Token)) != check)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return UpdateCheckResult.Failed("The update feed did not answer in time.");
            }

            return await check is { TargetFullRelease: { } release }
                ? new UpdateCheckResult(_ToRelease(release, channel), null)
                : UpdateCheckResult.UpToDate;
        }
        catch (NotInstalledException)
        {
            // The reading above answers for the states that actually occur; this covers the one it cannot see —
            // Velopack also calls an installation with no application id uninstalled. Same answer either way, from
            // one place, so the two routes cannot come to tell the operator different things.
            return _NotPackaged();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "The update check failed.");

            return UpdateCheckResult.Failed($"The update check failed: {exception.Message}");
        }
    }

    public Task<UpdateDownloadResult> DownloadAsync(UpdateChannel channel, Action<int>? progress = null, CancellationToken cancellationToken = default) =>
        DownloadAsync(channel, Source, locator: null, logger, Patience, progress, cancellationToken);

    // The download, with the feed and the installation handed in — same seam as `CheckAsync`, so a test reaches
    // Velopack's own verification through a real `UpdateManager`. Unlike the check, a successful fetch is kept
    // on the instance for a later apply call, because the operator applies whatever was just downloaded.
    internal async Task<UpdateDownloadResult> DownloadAsync(
        UpdateChannel channel,
        Func<UpdateChannel, IUpdateSource> source,
        IVelopackLocator? locator,
        ILogger logger,
        TimeSpan patience,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        if ((locator ?? (VelopackLocator.IsCurrentSet ? VelopackLocator.Current : null))?.CurrentlyInstalledVersion is null)
        {
            return UpdateDownloadResult.Failed(
                "This copy was not installed by the cockpit's installer, so it cannot download an update.");
        }

        try
        {
            var manager = new UpdateManager(
                source(channel),
                new UpdateOptions { ExplicitChannel = UpdateChannelName.For(channel) },
                locator);

            var check = manager.CheckForUpdatesAsync();

            using var waited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waited.CancelAfter(patience);

            if (await Task.WhenAny(check, Task.Delay(Timeout.Infinite, waited.Token)) != check)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return UpdateDownloadResult.Failed("The update feed did not answer in time.");
            }

            if (await check is not { TargetFullRelease: { } release } info)
            {
                return UpdateDownloadResult.Failed("There is no newer build to download.");
            }

            await manager.DownloadUpdatesAsync(info, progress, cancellationToken);

            // Kept only once the transfer actually finished — a failed or cancelled download below must leave
            // whatever was fetched by an earlier, successful call untouched rather than half-overwrite it.
            _pendingManager = manager;
            _pendingRelease = release;

            return UpdateDownloadResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return UpdateDownloadResult.Failed("The download was cancelled.");
        }
        catch (NotInstalledException)
        {
            return UpdateDownloadResult.Failed(
                "This copy was not installed by the cockpit's installer, so it cannot download an update.");
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "The update download failed.");

            return UpdateDownloadResult.Failed($"The update download failed: {exception.Message}");
        }
    }

    // `UpdateManager.ApplyUpdatesAndRestart` rather than this project's own `AppRestartService` (AC-388): that
    // service relaunches `Environment.ProcessPath`, an AppImage FUSE mount Velopack has just discarded, so the
    // relaunch would target a path that no longer exists. A no-op when nothing has been downloaded.
    public void ApplyDownloadedUpdateAndRestart()
    {
        if (_pendingManager is null || _pendingRelease is null)
        {
            return;
        }

        _pendingManager.ApplyUpdatesAndRestart(_pendingRelease);
    }

    // AC-738: recorded for the next launch, not handed to Velopack now. `WaitExitThenApplyUpdates` does not mean
    // "next start" — it starts Update.exe immediately, waits sixty seconds for this process and then kills it,
    // silently. The package stays where Velopack put it; `Program.Main` applies it at the next launch.
    public bool RequestUpdateOnNextStart() =>
        _pendingManager is not null && _pendingRelease is not null && UpdateOnNextStart.Request();

    // What a copy nobody installed is told. Not phrased as a fault: it is the ordinary state of a checkout, a
    // tarball or a distribution's own package, and it is the same thing the Updates tab says in full.
    private static UpdateCheckResult _NotPackaged() => UpdateCheckResult.Failed(
        $"This copy was not installed by the cockpit's installer, so it cannot look for updates. See {RepositoryUrl}/releases");

    // The feed for one channel. `prerelease` lets the nightly be seen at all, withholding it on stable.
    // `AccessToken` being empty is load-bearing (AC-462): GitHub only lists draft releases to users with push
    // access, so asking anonymously keeps a half-finished draft from counting as an update.
    internal static IUpdateSource Source(UpdateChannel channel) =>
        new GithubSource(RepositoryUrl, AccessToken, prerelease: channel == UpdateChannel.Nightly);

    // Deliberately none — see `Source`. Named rather than passed inline so a test can hold it to that, since
    // the day this stops being null is the day drafts become update candidates again.
    internal static string? AccessToken => null;

    private static AppRelease _ToRelease(VelopackAsset release, UpdateChannel channel)
    {
        var version = release.Version.ToFullString();

        return new AppRelease(version, release.NotesMarkdown ?? string.Empty, _ReleasePage(version, channel));
    }

    // Where to read about a build. The feed is a list of packages and carries no page of its own, so this is derived
    // from the tag the workflow published under: a release is tagged `v&lt;version&gt;`, and every nightly lands
    // on the one rolling tag.
    private static string _ReleasePage(string version, UpdateChannel channel) =>
        $"{RepositoryUrl}/releases/tag/{(channel == UpdateChannel.Nightly ? NightlyTag : $"v{version}")}";

    // What this build is. The version carries the semver — including the `-nightly.&lt;run&gt;` tag a nightly is
    // packed with — and SourceRevisionId appends "+&lt;sha&gt;", which is the commit the operator sees beside it.
    private static (string Version, string Commit) _Read(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? string.Empty;

        var plus = informational.IndexOf('+');

        return plus < 0
            ? (informational, string.Empty)
            : (informational[..plus], informational[(plus + 1)..]);
    }
}
