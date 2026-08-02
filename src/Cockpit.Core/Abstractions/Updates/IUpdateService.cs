using Cockpit.Core.Updates;

namespace Cockpit.Core.Abstractions.Updates;

/// <summary>
/// Asks GitHub whether there is a newer cockpit (#71). It checks and it tells; it does not install. Replacing a
/// running application on three platforms, unsigned, is a promise this project cannot keep today — and an updater
/// that half-keeps it is worse than a link the operator clicks themselves.
/// </summary>
public interface IUpdateService
{
    /// <summary>What this build is: the version it carries, and the commit it was built from (which is a nightly's only identity).</summary>
    (string Version, string Commit) Current { get; }

    /// <summary>Looks for a build newer than this one, on the channel the operator chose. Never throws: a check that failed says so, because reporting "up to date" when nothing was asked would be a lie they would believe.</summary>
    Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the build now on offer on <paramref name="channel"/> (AC-388) — the half of updating that runs before
    /// anything is applied. Never throws: a download that fails leaves the app exactly as it was, and the failure is
    /// reported rather than swallowed, the same discipline <see cref="CheckAsync"/> already holds.
    /// <para>
    /// <paramref name="progress"/> is 0-100 and, for the real implementation, arrives from whatever thread Velopack's
    /// own transfer runs on — not necessarily the UI thread (AC-368). A caller that touches view-model-bound state
    /// from it must marshal onto the UI thread itself; this service makes no assumption that one exists.
    /// </para>
    /// <para>
    /// A successful download is remembered by the service so a later <see cref="ApplyDownloadedUpdateAndRestart"/> or
    /// <see cref="ApplyDownloadedUpdateSilentlyOnNextStart"/> has something to act on — there is deliberately no
    /// "apply this specific release" overload, because there is only ever one build worth applying: the one just
    /// fetched.
    /// </para>
    /// </summary>
    Task<UpdateDownloadResult> DownloadAsync(UpdateChannel channel, Action<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the build fetched by the most recent successful <see cref="DownloadAsync"/> and restarts into it now.
    /// Only ever call this from an explicit, operator-confirmed click (AC-388) — there is no undo once the process
    /// has exited into the new build, unlike the generic app-restart service, which relaunches the same executable
    /// path and would restart into a discarded AppImage mount after an update. A no-op when nothing has been
    /// downloaded.
    /// </summary>
    void ApplyDownloadedUpdateAndRestart();

    /// <summary>
    /// Applies the build fetched by the most recent successful <see cref="DownloadAsync"/> the next time the cockpit
    /// starts, without touching the session running now (AC-388) — the "install on next start" offer that never
    /// interrupts whatever is running. A no-op when nothing has been downloaded.
    /// </summary>
    void ApplyDownloadedUpdateSilentlyOnNextStart();
}

/// <summary>Whether to look for updates at all, and which builds to be told about (#71).</summary>
public interface IUpdateSettingsStore
{
    Task<UpdateSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(UpdateSettings settings, CancellationToken cancellationToken = default);
}

// `CheckOnStartup`: Look once when the cockpit starts. On by default: an update nobody is told about is an update nobody installs.
// `Channel`:
// The channel the operator chose, or null when nobody has chosen one — then the build's own stream decides
// (`BuildChannel`). Nullable rather than defaulting to `UpdateChannel.Stable`, because a
// default is indistinguishable from a choice: a nightly started without a configuration file would land on stable
// and be offered a downgrade as its first update.
public sealed record UpdateSettings(bool CheckOnStartup = true, UpdateChannel? Channel = null);
