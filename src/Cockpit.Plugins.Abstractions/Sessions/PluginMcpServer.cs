namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One MCP server the host resolved from its shared registry for a session to expose to its provider (#44/#26).
/// The host does the resolving — it turns the operator's per-session selection into concrete endpoints — and a
/// driver that spawns an agent CLI (Codex) turns these into whatever config that CLI reads. A driver with no
/// tool source of its own simply ignores them.
/// </summary>
/// <remarks>
/// One of <see cref="Url"/> (an HTTP server) or <see cref="Command"/> (a stdio server) is set; the other is
/// <see langword="null"/>. <see cref="BearerToken"/> is the server's own credential, never the host's — it is a
/// secret, so it must never be written where another local account can read it (a process argument, a
/// world-readable file); see the driver that consumes it for how it is kept off the command line.
/// </remarks>
public sealed record PluginMcpServer
{
    /// <summary>The server's registry name — also the key the provider registers it under.</summary>
    public required string Name { get; init; }

    /// <summary>Endpoint URL for an HTTP server; <see langword="null"/> for a stdio server.</summary>
    public string? Url { get; init; }

    /// <summary>Executable for a stdio server; <see langword="null"/> for an HTTP server.</summary>
    public string? Command { get; init; }

    /// <summary>Arguments for the stdio <see cref="Command"/>.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>
    /// A static bearer token for an HTTP server, or <see langword="null"/> when the server needs none (a
    /// localhost server) or negotiates its own auth (OAuth). Sent as <c>Authorization: Bearer</c>.
    /// </summary>
    public string? BearerToken { get; init; }

    /// <summary>
    /// Overrides the record's auto-generated <c>ToString()</c>, which would otherwise print <see cref="BearerToken"/>
    /// in the clear — anywhere this lands in a log line or exception message (mirrors <c>CliAgentConfig.ToString()</c>).
    /// </summary>
    public override string ToString() =>
        $"{nameof(PluginMcpServer)} {{ Name = {Name}, Url = {Url}, Command = {Command}, " +
        $"BearerToken = {(string.IsNullOrEmpty(BearerToken) ? "null" : "***")} }}";
}
