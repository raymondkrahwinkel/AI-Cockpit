using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Core.Abstractions.Remote;

/// <summary>
/// The connect servers this cockpit holds a connection to, each with the sessions it runs there (AC-1456).
/// </summary>
public interface IRemoteServers
{
    /// <summary>
    /// The servers connected now, in the registry's order.
    /// </summary>
    IReadOnlyList<IRemoteServer> Servers { get; }

    /// <summary>
    /// Raised on any thread when a server joins or leaves <see cref="Servers"/>.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Whether <paramref name="name"/> is a connect-key row as of the last reload, connected or disconnected for this run.
    /// </summary>
    bool Knows(string name);

    /// <summary>
    /// Connects every connect-key row in the MCP registry that is not connected yet, and drops those that are gone.
    /// </summary>
    Task ReloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the connection to <paramref name="name"/> for this run; what runs there keeps running.
    /// </summary>
    Task DisconnectAsync(string name);

    /// <summary>
    /// Takes back a <see cref="DisconnectAsync"/>, so the server connects again.
    /// </summary>
    Task ReconnectAsync(string name);
}

/// <summary>
/// One connect server: its sessions as the backend API streams them, and the state of that stream.
/// </summary>
public interface IRemoteServer
{
    /// <summary>
    /// The name the operator gave the server when connecting.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The sessions on the server this key may see, nothing outside its scope listed or counted.
    /// Null until the server first answered.
    /// </summary>
    ISessionRegistry? Sessions { get; }

    /// <summary>
    /// Starts and stops sessions on the server; null until it first answered.
    /// </summary>
    ISessionLauncher? Launcher { get; }

    /// <summary>
    /// The server's connect keys, lockouts and audit, which only an admin key may reach (AC-1446).
    /// </summary>
    IConnectKeyAdministration Administration { get; }

    /// <summary>
    /// The server's profiles, which only an admin key may change (AC-1473).
    /// </summary>
    IServerProfiles Profiles { get; }

    /// <summary>
    /// The server's health as its health route answers it (AC-1457).
    /// </summary>
    IRemoteServerHealth Health { get; }

    /// <summary>
    /// The server's installed plugins and configured stores, which only an admin key may reach.
    /// </summary>
    IPluginAdministration Plugins { get; }

    /// <summary>
    /// The server's projects: an operate key sees the ones in its scope by id and name, an admin key may clone,
    /// change and remove them (AC-1472).
    /// </summary>
    IServerProjects Projects { get; }

    /// <summary>
    /// Whether the stream is up, how fast the server answered, and what it says about this key.
    /// </summary>
    RemoteServerState State { get; }

    /// <summary>
    /// Raised on any thread when <see cref="State"/> changed.
    /// </summary>
    event EventHandler? StateChanged;
}

// AC-1456: `LatencyMs` is the /whoami round trip at the last (re)connect; `Key` is null until a /whoami answered.
// `KeyRefused` is a server that turned the key away, which no retry will change.
public sealed record RemoteServerState(bool IsConnected, long? LatencyMs, RemoteServerKey? Key, bool KeyRefused = false);

// AC-1456: what /whoami says about the key this cockpit connects with. `AssistantHeldBy` names the key holding it;
// `MayAnswerPermissions` (AC-1469) only decides whether the buttons are drawn, the server decides whether an answer counts.
public sealed record RemoteServerKey(
    string Label,
    string Capability,
    bool HoldsAssistant,
    string? AssistantHeldBy,
    string? Version,
    DateTimeOffset? StartedAt,
    bool MayAnswerPermissions = false);
