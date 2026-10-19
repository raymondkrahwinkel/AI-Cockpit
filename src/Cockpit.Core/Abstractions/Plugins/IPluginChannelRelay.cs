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
    /// What the plugin's host on the server says about an MCP server that plugin registered, as the name of its auth state; never a token.
    /// </summary>
    Task<string> GetMcpServerAuthStateAsync(string folderId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs an MCP server that plugin registered in on the server (AC-1571 B2): the server runs the OAuth flow and keeps
    /// the token, this machine opens the browser and hands back the redirect. Answers the outcome's name; never a token.
    /// </summary>
    Task<string> SignInMcpServerAsync(string folderId, string name, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// The command-line tools the cockpit manages on the server (AC-1585), each with the path of its installed copy
    /// there, or null when none is installed; asked of the plugin's host there, as the plugin's own view would.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> GetManagedClisAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The installed and latest version of a managed tool on the server, and whether it updates itself there.
    /// </summary>
    Task<RemoteManagedCliStatus> GetManagedCliStatusAsync(string folderId, string cliName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs or updates a managed tool on the server; the answer says where it went, or why it did not.
    /// </summary>
    Task<RemoteManagedCliInstall> InstallManagedCliAsync(string folderId, string cliName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the server's managed copy of a tool; false when there was none.
    /// </summary>
    Task<bool> RemoveManagedCliAsync(string folderId, string cliName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns automatic updates of a managed tool on the server on or off.
    /// </summary>
    Task SetManagedCliAutoUpdateAsync(string folderId, string cliName, bool enabled, CancellationToken cancellationToken = default);
}

// AC-1585: a managed tool on the server as the plugin's view asks after it.
public sealed record RemoteManagedCliStatus(string? InstalledVersion, string? LatestVersion, bool AutoUpdate);

public sealed record RemoteManagedCliInstall(bool Success, string? Version, string? ExecutablePath, string? Error);
