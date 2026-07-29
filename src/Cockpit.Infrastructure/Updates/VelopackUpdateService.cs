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

/// <summary>
/// Asks the update feed whether a newer cockpit exists (#71, AC-387), through the same <see cref="UpdateManager"/>
/// that will later fetch and apply it.
/// <para>
/// One source, deliberately. The cockpit used to ask GitHub's releases API itself while the packaging wrote a
/// Velopack feed beside it — two answers to one question, free to disagree: a banner announcing a build the updater
/// cannot see, or an updater sitting on one the banner never mentioned.
/// </para>
/// <para>
/// A check that fails — no network, a rate limit, GitHub having a bad morning — returns a failure and says so. The
/// tempting alternative, reporting "you are up to date", is a lie the operator has every reason to believe.
/// </para>
/// </summary>
internal sealed class VelopackUpdateService(ILogger<VelopackUpdateService> logger) : IUpdateService, ISingletonService
{
    private const string RepositoryUrl = "https://github.com/raymondkrahwinkel/AI-Cockpit";

    /// <summary>The rolling tag every nightly is published onto — one release, replaced each night.</summary>
    private const string NightlyTag = "nightly";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public (string Version, string Commit) Current { get; } = _Read(typeof(VelopackUpdateService).Assembly);

    /// <summary>
    /// The build a successful <see cref="DownloadAsync"/> fetched, and the manager that fetched it — the only two
    /// things <see cref="ApplyDownloadedUpdateAndRestart"/>/<see cref="ApplyDownloadedUpdateSilentlyOnNextStart"/>
    /// need. This instance is registered as an <see cref="ISingletonService"/>, so the pair survives from the
    /// download to whichever apply call the operator eventually clicks; there is deliberately no way to apply
    /// anything else, because there is only ever one build worth applying: the one just fetched.
    /// </summary>
    private UpdateManager? _pendingManager;
    private VelopackAsset? _pendingRelease;

    public Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken cancellationToken = default) =>
        CheckAsync(channel, Source, locator: null, logger, Patience, cancellationToken);

    /// <summary>
    /// The check, with the feed and the installation handed in. Velopack answers from an installation on disk, so a
    /// test reaches this by supplying a source of its own and a locator that stands in for one — and what the source
    /// is asked for is the channel this decides on, which is the part most worth pinning down.
    /// </summary>
    internal static async Task<UpdateCheckResult> CheckAsync(
        UpdateChannel channel,
        Func<UpdateChannel, IUpdateSource> source,
        IVelopackLocator? locator,
        ILogger logger,
        TimeSpan patience,
        CancellationToken cancellationToken)
    {
        // There are two ways to be a copy the installer never placed, and neither is an error: a host that never ran
        // VelopackApp.Build().Run() has no locator at all (a test host, the screenshot renderer), and a checkout or a
        // tarball has one that knows of no installed version. Asked here rather than caught, because both are ordinary
        // — reaching them through an exception would put a library's own wording in front of the operator, and the
        // constructor's is "No VelopackLocator has been set".
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

    /// <summary>
    /// The download, with the feed and the installation handed in — same seam as <see cref="CheckAsync"/>, and for
    /// the same reason: a test reaches Velopack's own verification (size, checksum) through a real
    /// <see cref="UpdateManager"/> rather than re-implementing it. Unlike the check, this one has somewhere to put
    /// its result: a successful fetch is kept on the instance for a later apply call to use, because the operator
    /// applies whatever was just downloaded, never a release named separately.
    /// </summary>
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

    /// <summary>
    /// <c>UpdateManager.ApplyUpdatesAndRestart</c> rather than this project's own <c>AppRestartService</c> (AC-388):
    /// that service relaunches <see cref="Environment.ProcessPath"/>, which on an AppImage is a FUSE mount Velopack
    /// has just discarded in favour of the new one — the relaunch would target a path that no longer exists.
    /// Velopack's own restart tracks <c>$APPIMAGE</c> internally and does not have that problem. A no-op when
    /// nothing has been downloaded, so a stray call before a successful <see cref="DownloadAsync"/> does nothing
    /// rather than restart into whatever the installer last left on disk.
    /// </summary>
    public void ApplyDownloadedUpdateAndRestart()
    {
        if (_pendingManager is null || _pendingRelease is null)
        {
            return;
        }

        _pendingManager.ApplyUpdatesAndRestart(_pendingRelease);
    }

    /// <summary>
    /// <c>silent: true, restart: false</c> (AC-388): the update lands next launch and this session is left running
    /// untouched, which is the whole point of offering it as the alternative to restarting now.
    /// </summary>
    public void ApplyDownloadedUpdateSilentlyOnNextStart()
    {
        if (_pendingManager is null || _pendingRelease is null)
        {
            return;
        }

        _pendingManager.WaitExitThenApplyUpdates(_pendingRelease, silent: true, restart: false);
    }

    /// <summary>
    /// What a copy nobody installed is told. Not phrased as a fault: it is the ordinary state of a checkout, a
    /// tarball or a distribution's own package, and it is the same thing the Updates tab says in full.
    /// </summary>
    private static UpdateCheckResult _NotPackaged() => UpdateCheckResult.Failed(
        $"This copy was not installed by the cockpit's installer, so it cannot look for updates. See {RepositoryUrl}/releases");

    /// <summary>
    /// The feed for one channel. <c>prerelease</c> is what lets the nightly be seen at all — the workflow publishes it
    /// as a GitHub pre-release — and withholding it on stable is the first of the two things keeping a stable install
    /// away from nightlies. The second is the channel name, which the check builds.
    /// <para>
    /// The <see cref="AccessToken"/> being empty is load-bearing, not an omission (AC-462). GitHub's release listing
    /// is documented as "Information about published releases are available to everyone. Only users with push access
    /// will receive listings for draft releases" — so asking anonymously is what keeps a half-finished draft from
    /// counting as an update. The cockpit used to drop drafts itself; Velopack cannot, because the model it reads a
    /// release into has no draft field to drop them by. Filtering on an empty publication date instead was considered
    /// and rejected: GitHub's schema marks that field nullable outright and nowhere ties it to draft status, so the
    /// rule would rest on undocumented behaviour with "silently skip a real release" as its failure.
    /// </para>
    /// </summary>
    internal static IUpdateSource Source(UpdateChannel channel) =>
        new GithubSource(RepositoryUrl, AccessToken, prerelease: channel == UpdateChannel.Nightly);

    /// <summary>
    /// Deliberately none — see <see cref="Source"/>. Named rather than passed inline so a test can hold it to that,
    /// because the day this stops being null is the day drafts become update candidates again, and the reason to
    /// change it (the anonymous API allows sixty requests an hour per address, shared by everyone behind it) has
    /// nothing to do with drafts and would not bring them to mind.
    /// </summary>
    internal static string? AccessToken => null;

    private static AppRelease _ToRelease(VelopackAsset release, UpdateChannel channel)
    {
        var version = release.Version.ToFullString();

        return new AppRelease(version, release.NotesMarkdown ?? string.Empty, _ReleasePage(version, channel));
    }

    /// <summary>
    /// Where to read about a build. The feed is a list of packages and carries no page of its own, so this is derived
    /// from the tag the workflow published under: a release is tagged <c>v&lt;version&gt;</c>, and every nightly lands
    /// on the one rolling tag.
    /// </summary>
    private static string _ReleasePage(string version, UpdateChannel channel) =>
        $"{RepositoryUrl}/releases/tag/{(channel == UpdateChannel.Nightly ? NightlyTag : $"v{version}")}";

    /// <summary>
    /// What this build is. The version carries the semver — including the <c>-nightly.&lt;run&gt;</c> tag a nightly is
    /// packed with — and SourceRevisionId appends "+&lt;sha&gt;", which is the commit the operator sees beside it.
    /// </summary>
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
