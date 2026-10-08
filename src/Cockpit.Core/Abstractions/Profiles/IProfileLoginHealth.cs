using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Profiles;

/// <summary>
/// The sign-in state of every provider profile, as the host's own poll last read it.
/// </summary>
public interface IProfileLoginHealth
{
    /// <summary>
    /// One row per profile checked so far, in profile order; empty until the first poll has run.
    /// </summary>
    IReadOnlyList<ProfileLoginHealth> Current { get; }

    /// <summary>
    /// Refreshes the profile sign-in readings when the host supports it.
    /// </summary>
    Task CheckAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
