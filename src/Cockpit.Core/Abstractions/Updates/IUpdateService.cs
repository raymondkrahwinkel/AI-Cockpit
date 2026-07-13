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
}

/// <summary>Whether to look for updates at all, and which builds to be told about (#71).</summary>
public interface IUpdateSettingsStore
{
    Task<UpdateSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(UpdateSettings settings, CancellationToken cancellationToken = default);
}

/// <param name="CheckOnStartup">Look once when the cockpit starts. On by default: an update nobody is told about is an update nobody installs.</param>
/// <param name="Channel">Tagged releases, or also the nightly build of main.</param>
public sealed record UpdateSettings(bool CheckOnStartup = true, UpdateChannel Channel = UpdateChannel.Stable);
