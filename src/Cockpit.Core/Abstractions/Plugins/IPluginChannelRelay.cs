using System.Text.Json;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// Reaches a plugin's backend part on a server through its own channel (AC-1511), for the view that plugin offers an
/// admin of that server. Invokes only; the server's events do not travel this way.
/// </summary>
public interface IPluginChannelRelay
{
    Task<JsonElement> InvokeChannelAsync(string folderId, string action, JsonElement payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// The plugin's storage on the server as JSON text per key (AC-1571), every set credential reading as "(set on the server)".
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetStorageAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the keys a settings view changed and removes the ones it removed; <paramref name="secrets"/> names those it
    /// wrote as a credential. A value still reading "(set on the server)" keeps what the server holds.
    /// </summary>
    Task SaveStorageAsync(
        string folderId,
        IReadOnlyDictionary<string, string> changed,
        IReadOnlyCollection<string> removed,
        IReadOnlyCollection<string> secrets,
        CancellationToken cancellationToken = default);
}
