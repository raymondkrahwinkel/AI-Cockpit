using Cockpit.Core.Abstractions.Mcp;
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

    /// <summary>
    /// Writes one whole profile (AC-1585): makes it when <paramref name="originalLabel"/> is null, else replaces that
    /// one, renaming it to the label <paramref name="profile"/> carries. Each secret that reads as the server's mask stays
    /// as the server holds it. Throws <see cref="ArgumentException"/> with the server's reason when it refuses.
    /// </summary>
    Task<RemoteProfile> SaveProfileAsync(string? originalLabel, SessionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// The providers a profile on the server can name, with what the profile editor needs of each (AC-1585).
    /// </summary>
    Task<IReadOnlyList<RemoteProviderInfo>> ListProviderInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The names of the MCP servers the server offers a profile to pre-select (AC-1585).
    /// </summary>
    Task<IReadOnlyList<string>> ListMcpServersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// What one of the server's MCP servers costs a session there in tools and tokens, counted on the server.
    /// </summary>
    Task<McpServerToolEstimate> EstimateMcpToolsAsync(string name, bool refresh, CancellationToken cancellationToken = default);

    /// <summary>
    /// The models a local-model server answers with, asked from the server. When <paramref name="apiKey"/> is the mask,
    /// the server uses the key it keeps for profile <paramref name="label"/>.
    /// </summary>
    Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? apiKey, string? label, CancellationToken cancellationToken = default);

    /// <summary>
    /// The server's own memory sources that list brains, each with its brains or the server's reason it has none (AC-1995);
    /// empty from a server that predates the list.
    /// </summary>
    Task<IReadOnlyList<RemoteBrainSource>> ListBrainSourcesAsync(CancellationToken cancellationToken = default);
}
