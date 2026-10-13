using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Remote;

/// <summary>
/// A server's profiles over its admin API (AC-1473). Nothing here carries a provider credential, and a change names
/// only the fields it changes, so what the server holds and this side never sees stays where it is.
/// </summary>
public interface IServerProfiles
{
    /// <summary>
    /// Every profile on the server, without its secrets.
    /// </summary>
    Task<IReadOnlyList<RemoteProfile>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The provider ids a new profile on the server can name (AC-1534); empty from a server that predates the list.
    /// </summary>
    Task<IReadOnlyList<string>> ListProvidersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes a profile on the server. Throws <see cref="ArgumentException"/> with the server's reason when it refuses.
    /// </summary>
    Task<RemoteProfile> CreateAsync(RemoteNewProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the fields <paramref name="patch"/> names; null when the server has no profile <paramref name="label"/>.
    /// Throws <see cref="ArgumentException"/> with the server's reason when it refuses.
    /// </summary>
    Task<RemoteProfile?> UpdateAsync(string label, RemoteProfilePatch patch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the profile; false when the server had none by that label.
    /// </summary>
    Task<bool> DeleteAsync(string label, CancellationToken cancellationToken = default);
}
