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

// AC-1456: what /whoami says about the key this cockpit connects with. `AssistantHeldBy` names the key holding it.
public sealed record RemoteServerKey(
    string Label,
    string Capability,
    bool HoldsAssistant,
    string? AssistantHeldBy,
    string? Version,
    DateTimeOffset? StartedAt);
