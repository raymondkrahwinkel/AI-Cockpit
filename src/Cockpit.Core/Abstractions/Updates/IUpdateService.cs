using Cockpit.Core.Updates;

namespace Cockpit.Core.Abstractions.Updates;

/// <summary>
/// Asks GitHub whether there is a newer cockpit (#71). It checks and it tells; it does not install. Replacing a
/// running application on three platforms, unsigned, is a promise this project cannot keep today — and an updater
/// that half-keeps it is worse than a link the operator clicks themselves.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// What this build is: the version it carries, and the commit it was built from (which is a nightly's only identity).
    /// </summary>
    (string Version, string Commit) Current { get; }

    /// <summary>
    /// Looks for a build newer than this one, on the channel the operator chose. Never throws: a check that failed says so, because reporting "up to date" when nothing was asked would be a lie they would believe.
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the build now on offer on <paramref name="channel"/> (AC-388). Never throws — a failed download leaves the
    /// app as it was, reported rather than swallowed. <paramref name="progress"/> is 0-100 and may arrive off the UI
    /// thread (AC-368), so callers must marshal it. Remembered for a later Apply/Request call — never "apply this release".
    /// </summary>
    Task<UpdateDownloadResult> DownloadAsync(UpdateChannel channel, Action<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the build fetched by the most recent <see cref="DownloadAsync"/> and restarts into it now. Only call this
    /// from an explicit, operator-confirmed click (AC-388) — no undo once the process exits, unlike the generic
    /// app-restart service, which would restart into a discarded AppImage mount. A no-op when nothing was downloaded.
    /// </summary>
    void ApplyDownloadedUpdateAndRestart();

    /// <summary>
    /// Asks for the build fetched by the most recent successful <see cref="DownloadAsync"/> to be applied the next
    /// time the cockpit starts, leaving the session running now completely untouched (AC-388, AC-738).
    /// </summary>
    /// <returns>
    /// Whether the request was recorded; false when nothing was downloaded or the request could not be written.
    /// </returns>
    bool RequestUpdateOnNextStart();
}

/// <summary>
/// Whether to look for updates at all, and which builds to be told about (#71).
/// </summary>
public interface IUpdateSettingsStore
{
    Task<UpdateSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(UpdateSettings settings, CancellationToken cancellationToken = default);
}

// AC-1013: CheckOnStartup defaults true (an update nobody is told about is one nobody installs). Channel
// is null (not defaulted to Stable) when nobody chose one, so the build's own BuildChannel decides.
// Trimmed: defaulting to Stable would be indistinguishable from a choice — a nightly with no config file would land on stable and get offered a downgrade as its first update.
public sealed record UpdateSettings(bool CheckOnStartup = true, UpdateChannel? Channel = null);
